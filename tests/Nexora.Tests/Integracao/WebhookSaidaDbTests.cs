using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Webhooks;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;
using Nexora.Infra.Webhooks;
using Nexora.Tests.Unidade;

namespace Nexora.Tests.Integracao;

/// <summary>WEBHOOK DE SAÍDA contra Postgres real (INT-3).
///
/// Três eixos:
///   • os eventos SAEM quando marcados e NÃO saem quando desmarcados;
///   • a entrega não acontece no caminho do usuário, e o retry para;
///   • a URL é validada duas vezes — no cadastro e antes de cada entrega.</summary>
[Collection("banco")]
public class WebhookSaidaDbTests(BancoTeste banco)
{
    private const string UrlOk = "https://webhook.cliente.com/nexora";

    // ==================================================================== os eventos
    [Fact]
    public async Task LEAD_CRIADO_DISPARA_QUANDO_MARCADO()
    {
        var (db, tx, amb) = await PrepararAsync("lead-criado");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);

        await amb.Contatos.CriarAsync(
            new NovoContato("Maria Silva", "84988887777", null, null, null, null, null, null, null), default);

        var entrega = Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(EventoWebhook.LeadCriado, entrega.Evento);
        Assert.Equal(StatusEntregaWebhook.Pendente, entrega.Status);
        Assert.Equal(UrlOk, entrega.Url);

        // O corpo já sai pronto e versionado — ele é o que vai ser assinado, e remontá-lo na hora
        // do envio deixaria as três tentativas com corpos diferentes.
        using var doc = JsonDocument.Parse(entrega.Payload);
        Assert.Equal(1, doc.RootElement.GetProperty("versao").GetInt32());
        Assert.Equal("lead.criado", doc.RootElement.GetProperty("evento").GetString());
        Assert.Contains("Maria Silva", entrega.Payload);
    }

    [Fact]
    public async Task LEAD_CRIADO_NAO_DISPARA_QUANDO_DESMARCADO()
    {
        var (db, tx, amb) = await PrepararAsync("lead-desmarcado");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb, m => m with { EmLeadCriado = false });

        await amb.Contatos.CriarAsync(
            new NovoContato("Maria Silva", "84988887777", null, null, null, null, null, null, null), default);

        Assert.Empty(await EntregasAsync(db, amb));
    }

    [Fact]
    public async Task WEBHOOK_DESATIVADO_NAO_ENFILEIRA_NADA()
    {
        // Desligado não é "enfileira e não manda": é não enfileirar. Guardar eventos de um webhook
        // desligado encheria a tabela com o que ninguém pediu, e no dia de religar tudo sairia de
        // uma vez.
        var (db, tx, amb) = await PrepararAsync("desativado");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb, m => m with { Ativo = false });

        await amb.Contatos.CriarAsync(
            new NovoContato("Maria", "84988887777", null, null, null, null, null, null, null), default);

        Assert.Empty(await EntregasAsync(db, amb));
    }

    [Fact]
    public async Task VENDA_FECHADA_E_PERDIDA_DISPARAM_COM_A_ETAPA_ANTERIOR()
    {
        var (db, tx, amb) = await PrepararAsync("terminal");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);

        var ganho = await amb.Contatos.CriarAsync(
            new NovoContato("Ganha", "84988880001", null, null, null, null, null, null, null), default);
        var perdido = await amb.Contatos.CriarAsync(
            new NovoContato("Perdida", "84988880002", null, null, null, null, null, null, null), default);
        db.ChangeTracker.Clear();

        await amb.Contatos.MarcarGanhoAsync(ganho, 2500m, default);
        await amb.Contatos.MarcarPerdidoAsync(perdido, "achou caro", default);

        var eventos = (await EntregasAsync(db, amb)).Select(e => e.Evento).ToList();
        Assert.Contains(EventoWebhook.VendaFechada, eventos);
        Assert.Contains(EventoWebhook.VendaPerdida, eventos);

        // UM evento por ação: carimbar o ganho move de etapa junto, e emitir `lead.movido` também
        // faria o receptor ver dois fatos onde houve um.
        Assert.DoesNotContain(EventoWebhook.LeadMovido, eventos);

        var fechada = (await EntregasAsync(db, amb)).First(e => e.Evento == EventoWebhook.VendaFechada);
        using var doc = JsonDocument.Parse(fechada.Payload);
        Assert.True(doc.RootElement.GetProperty("dados")
            .TryGetProperty("etapaAnteriorId", out var anterior) && anterior.GetInt64() > 0);
    }

    [Fact]
    public async Task LEAD_MOVIDO_SO_DISPARA_QUANDO_A_ETAPA_MUDA()
    {
        // Reordenar o card DENTRO da coluna passa pelo mesmo método. Para o sistema do cliente a
        // posição não significa nada — um evento por arrasto encheria a fila de ruído.
        var (db, tx, amb) = await PrepararAsync("movido");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);

        var id = await amb.Contatos.CriarAsync(
            new NovoContato("Móvel", "84988880003", null, null, null, null, null, null, null), default);
        db.ChangeTracker.Clear();
        await LimparEntregasAsync(db, amb);

        var etapas = await db.EtapasFunil.AsNoTracking().OrderBy(e => e.Ordem).ToListAsync();
        var origem = etapas[0];
        var destino = etapas.First(e => !e.EGanho && e.Id != origem.Id);

        // Mesma etapa: NÃO dispara.
        await amb.Funil.MoverAsync(id, new MoverContato(origem.Id, null, null), default);
        db.ChangeTracker.Clear();
        Assert.Empty(await EntregasAsync(db, amb));

        // Etapa diferente: dispara.
        await amb.Funil.MoverAsync(id, new MoverContato(destino.Id, null, null), default);
        db.ChangeTracker.Clear();

        var entrega = Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(EventoWebhook.LeadMovido, entrega.Evento);

        using var doc = JsonDocument.Parse(entrega.Payload);
        var dados = doc.RootElement.GetProperty("dados");
        Assert.Equal(origem.Id, dados.GetProperty("etapaAnteriorId").GetInt64());
        Assert.Equal(destino.Id, dados.GetProperty("etapaId").GetInt64());
    }

    [Fact]
    public async Task MENSAGEM_RECEBIDA_SO_SAI_SE_MARCADA()
    {
        // O evento de maior volume, e o único desmarcado por padrão.
        var (db, tx, amb) = await PrepararAsync("mensagem");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);   // padrão: mensagem.recebida DESLIGADO
        amb.Contexto.EmpresaId = 0;   // o webhook da Evolution roda em tenant zero

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Cenario.Conexao.InstanceName,
                "5584911112222@s.whatsapp.net", "WA-M1", "oi, quero orçamento"), default);

        var soLead = await EntregasAsync(db, amb);
        Assert.Single(soLead);
        Assert.Equal(EventoWebhook.LeadCriado, soLead[0].Evento);

        // Agora LIGA e manda de outro número.
        await LimparEntregasAsync(db, amb);
        amb.Contexto.EmpresaId = amb.Cenario.Id;
        await ConfigurarAsync(amb, m => m with { EmMensagemRecebida = true });
        amb.Contexto.EmpresaId = 0;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Cenario.Conexao.InstanceName,
                "5584933334444@s.whatsapp.net", "WA-M2", "bom dia"), default);

        var eventos = (await EntregasAsync(db, amb)).Select(e => e.Evento).ToList();
        Assert.Contains(EventoWebhook.MensagemRecebida, eventos);
        Assert.Contains(EventoWebhook.LeadCriado, eventos);
    }

    [Fact]
    public async Task WEBHOOK_DE_OUTRA_EMPRESA_NAO_RECEBE_EVENTO_NENHUM()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await Semeador.TenantAsync(db, "wh-isol-a");
        var outra = await Semeador.TenantAsync(db, "wh-isol-b");

        // SÓ a outra empresa tem webhook.
        db.WebhooksSaida.Add(new WebhookSaida
        {
            EmpresaId = outra.Id, Url = UrlOk, Segredo = AssinaturaWebhook.GerarSegredo()
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        ctx.EmpresaId = minha.Id;
        ctx.UsuarioId = minha.Dono.Id;
        ctx.Papel = "dono";

        var contatos = new ServicoContatos(
            db, ctx, PublicadorDeTeste.Novo(db), new ColetorAuditoria(), TimeProvider.System);
        await contatos.CriarAsync(
            new NovoContato("Meu Lead", "84988889999", null, null, null, null, null, null, null), default);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.EntregasWebhook.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.EmpresaId == minha.Id || e.EmpresaId == outra.Id).ToListAsync());
    }

    // ==================================================================== fora da requisição
    [Fact]
    public async Task RECEPTOR_LENTO_NAO_ATRASA_A_REQUISICAO_DO_USUARIO()
    {
        // ===================== O PONTO DO BLOCO INTEIRO =====================
        // Fechar uma venda não pode ficar lento porque o servidor do cliente está devagar. O que
        // acontece no caminho do usuário é um INSERT; quem posta é a rodada, depois.
        //
        // O cliente HTTP deste ambiente dorme 3 segundos em TODA entrega. Se a publicação
        // entregasse, a chamada abaixo levaria 3s.
        // ====================================================================
        var (db, tx, amb) = await PrepararAsync("lento");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);
        amb.Cliente.Demora = TimeSpan.FromSeconds(3);

        var cronometro = Stopwatch.StartNew();
        await amb.Contatos.CriarAsync(
            new NovoContato("Rápido", "84988887777", null, null, null, null, null, null, null), default);
        cronometro.Stop();

        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(1),
            $"criar contato levou {cronometro.Elapsed.TotalSeconds:0.0}s — a entrega vazou para o "
          + "caminho do usuário");

        // E o evento está na fila, não perdido.
        Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(0, amb.Cliente.Chamadas);   // nada foi postado ainda
    }

    // ==================================================================== a rodada
    [Fact]
    public async Task A_RODADA_ENTREGA_E_ASSINA_O_CORPO_EXATO()
    {
        var (db, tx, amb) = await PrepararAsync("entrega");
        using var _ = db; using var __ = tx;

        var segredo = await ConfigurarAsync(amb);
        await amb.Contatos.CriarAsync(
            new NovoContato("Maria", "84988887777", null, null, null, null, null, null, null), default);
        db.ChangeTracker.Clear();

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Entregues);
        var post = Assert.Single(amb.Cliente.Enviados);

        // O corpo postado é IDÊNTICO ao guardado — nada foi reserializado no caminho, e é isso que
        // faz a assinatura conferir do lado do receptor.
        var entrega = Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(entrega.Payload, post.Corpo);
        Assert.Equal(StatusEntregaWebhook.Entregue, entrega.Status);
        Assert.Equal(200, entrega.CodigoResposta);
        Assert.NotNull(entrega.EntregueEm);
        Assert.Null(entrega.ProximaTentativaEm);

        // E o receptor consegue validar com o segredo que ele guardou.
        Assert.True(AssinaturaWebhook.Confere(segredo, post.Timestamp, post.Corpo, post.Assinatura));
        Assert.Equal("lead.criado", post.Evento);
    }

    [Fact]
    public async Task FALHA_REAGENDA_COM_BACKOFF_E_PARA_NA_TERCEIRA()
    {
        var relogio = new RelogioFalso(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));
        var (db, tx, amb) = await PrepararAsync("backoff", relogio);
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);
        await amb.Contatos.CriarAsync(
            new NovoContato("Maria", "84988887777", null, null, null, null, null, null, null), default);
        db.ChangeTracker.Clear();

        amb.Cliente.Codigo = 500;

        // 1ª falha → +1 min
        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Reagendadas);
        var entrega = Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(1, entrega.Tentativas);
        Assert.Equal(StatusEntregaWebhook.Pendente, entrega.Status);
        Assert.Equal(relogio.GetUtcNow().UtcDateTime.AddMinutes(1), entrega.ProximaTentativaEm);

        // Antes de vencer, a rodada NÃO pega a linha — senão o backoff seria decorativo.
        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Tentadas);

        // 2ª falha → +5 min
        relogio.Avancar(TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Reagendadas);
        db.ChangeTracker.Clear();
        entrega = Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(2, entrega.Tentativas);
        Assert.Equal(relogio.GetUtcNow().UtcDateTime.AddMinutes(5), entrega.ProximaTentativaEm);

        // 3ª falha → PARA
        relogio.Avancar(TimeSpan.FromMinutes(5));
        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Desistidas);
        db.ChangeTracker.Clear();
        entrega = Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(3, entrega.Tentativas);
        Assert.Equal(StatusEntregaWebhook.Falhou, entrega.Status);
        Assert.Null(entrega.ProximaTentativaEm);
        Assert.Equal(500, entrega.CodigoResposta);

        // E não volta sozinha, nem daqui a um mês.
        relogio.Avancar(TimeSpan.FromDays(30));
        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Tentadas);
        Assert.Equal(3, amb.Cliente.Chamadas);
    }

    [Fact]
    public async Task ENTREGA_PARA_IP_PRIVADO_E_RECUSADA_NA_HORA_DO_ENVIO()
    {
        // ===== A SEGUNDA VALIDAÇÃO =====
        // A URL passou no cadastro apontando para um IP público. O DNS mudou depois — a zona é do
        // cliente. Sem a checagem na entrega, o Nexora postaria para dentro da própria rede.
        var (db, tx, amb) = await PrepararAsync("ssrf-entrega");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);
        await amb.Contatos.CriarAsync(
            new NovoContato("Maria", "84988887777", null, null, null, null, null, null, null), default);
        db.ChangeTracker.Clear();

        // O DNS "vira" e passa a apontar para dentro.
        amb.Dns["webhook.cliente.com"] = ["169.254.169.254"];

        await amb.Motor.ExecutarAsync();
        db.ChangeTracker.Clear();

        var entrega = Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(0, amb.Cliente.Chamadas);   // NADA foi postado
        Assert.Contains("interno", entrega.Erro);
        Assert.Equal(1, entrega.Tentativas);
    }

    [Fact]
    public async Task URL_INTERNA_E_RECUSADA_NO_CADASTRO()
    {
        var (db, tx, amb) = await PrepararAsync("ssrf-cadastro");
        using var _ = db; using var __ = tx;

        foreach (var url in new[]
                 {
                     "http://webhook.cliente.com/x",       // sem https
                     "https://127.0.0.1/x",
                     "https://192.168.0.10/x",
                     "https://169.254.169.254/x"
                 })
        {
            await Assert.ThrowsAsync<RegraDeNegocioException>(
                () => amb.Webhooks.SalvarAsync(Configuracao(url), default));
        }

        // E um nome que RESOLVE para dentro também: o formato passa, o DNS não.
        amb.Dns["interno.cliente.com"] = ["10.0.0.5"];
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Webhooks.SalvarAsync(Configuracao("https://interno.cliente.com/x"), default));

        db.ChangeTracker.Clear();
        Assert.Null((await amb.Webhooks.ObterAsync(default)).Webhook);
    }

    // ==================================================================== teste e reenvio
    [Fact]
    public async Task O_EVENTO_DE_TESTE_FUNCIONA_SEM_DADO_REAL()
    {
        // Numa conta recém-criada — que é exatamente quando o dono configura a integração — não
        // existe contato nenhum. O teste não pode depender de um.
        var (db, tx, amb) = await PrepararAsync("teste");
        using var _ = db; using var __ = tx;

        // Na ordem das FKs: mensagem → conversa → contato. Deixar qualquer uma para trás faz o
        // banco recusar, e o teste falharia por motivo que não tem nada a ver com webhook.
        await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.EmpresaId == amb.Cenario.Id).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters()
            .Where(c => c.EmpresaId == amb.Cenario.Id).ExecuteDeleteAsync();
        await db.Contatos.IgnoreQueryFilters()
            .Where(c => c.EmpresaId == amb.Cenario.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        var segredo = await ConfigurarAsync(amb);
        var r = await amb.Webhooks.TestarAsync(default);

        Assert.True(r.Ok);
        Assert.Equal(200, r.Codigo);

        var post = Assert.Single(amb.Cliente.Enviados);
        Assert.Equal("webhook.teste", post.Evento);
        Assert.True(AssinaturaWebhook.Confere(segredo, post.Timestamp, post.Corpo, post.Assinatura));

        // Tipo PRÓPRIO, não um `lead.criado` de mentira: senão o primeiro clique no botão criaria
        // um lead fantasma no ERP do cliente.
        using var doc = JsonDocument.Parse(post.Corpo);
        Assert.Equal("webhook.teste", doc.RootElement.GetProperty("evento").GetString());
        Assert.Equal(-1, doc.RootElement.GetProperty("dados").GetProperty("exemplo")
            .GetProperty("id").GetInt32());

        // Registrado como qualquer outra entrega, e SEM reagendamento: o teste é síncrono.
        db.ChangeTracker.Clear();
        var entrega = Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(EventoWebhook.Teste, entrega.Evento);
        Assert.Null(entrega.ProximaTentativaEm);
    }

    [Fact]
    public async Task Reenviar_devolve_a_entrega_falha_para_a_fila()
    {
        var (db, tx, amb) = await PrepararAsync("reenvio");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);
        await amb.Contatos.CriarAsync(
            new NovoContato("Maria", "84988887777", null, null, null, null, null, null, null), default);
        db.ChangeTracker.Clear();

        amb.Cliente.Codigo = 500;
        for (var i = 0; i < 3; i++)
        {
            await amb.Motor.ExecutarAsync();
            db.ChangeTracker.Clear();
            amb.Relogio.Avancar(TimeSpan.FromMinutes(31));
        }

        var falha = Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(StatusEntregaWebhook.Falhou, falha.Status);
        var eventoId = falha.EventoId;

        amb.Cliente.Codigo = 200;
        await amb.Webhooks.ReenviarAsync(falha.Id, default);
        db.ChangeTracker.Clear();

        var refila = Assert.Single(await EntregasAsync(db, amb));
        Assert.Equal(StatusEntregaWebhook.Pendente, refila.Status);
        Assert.Equal(0, refila.Tentativas);
        // O id do EVENTO não muda: o receptor precisa continuar reconhecendo que é o mesmo fato,
        // senão o reenvio vira duplicata do lado dele.
        Assert.Equal(eventoId, refila.EventoId);

        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Entregues);
    }

    [Fact]
    public async Task Reenviar_o_que_ja_foi_aceito_e_recusado()
    {
        var (db, tx, amb) = await PrepararAsync("reenvio-ok");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);
        await amb.Contatos.CriarAsync(
            new NovoContato("Maria", "84988887777", null, null, null, null, null, null, null), default);
        db.ChangeTracker.Clear();

        await amb.Motor.ExecutarAsync();
        db.ChangeTracker.Clear();

        var entregue = Assert.Single(await EntregasAsync(db, amb));
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Webhooks.ReenviarAsync(entregue.Id, default));
    }

    // ==================================================================== segredo
    [Fact]
    public async Task O_SEGREDO_SAI_UMA_VEZ_E_NAO_VOLTA()
    {
        var (db, tx, amb) = await PrepararAsync("segredo");
        using var _ = db; using var __ = tx;

        var criado = await amb.Webhooks.SalvarAsync(Configuracao(UrlOk), default);
        Assert.NotNull(criado);
        Assert.True(criado!.Novo);
        Assert.Equal(64, criado.Segredo.Length);
        db.ChangeTracker.Clear();

        // Atualizar NÃO revela de novo — senão "mostrado uma vez" seria só uma frase na tela.
        Assert.Null(await amb.Webhooks.SalvarAsync(Configuracao(UrlOk) with { Ativo = false }, default));
        db.ChangeTracker.Clear();

        // E o painel nunca traz o segredo: `WebhookDto` não tem o campo.
        var painel = await amb.Webhooks.ObterAsync(default);
        Assert.NotNull(painel.Webhook);
        Assert.DoesNotContain("segredo", JsonSerializer.Serialize(painel.Webhook),
            StringComparison.OrdinalIgnoreCase);

        // Regerar troca a chave.
        db.ChangeTracker.Clear();
        var novo = await amb.Webhooks.RegerarSegredoAsync(default);
        Assert.False(novo.Novo);
        Assert.NotEqual(criado.Segredo, novo.Segredo);
    }

    // ==================================================================== expurgo
    [Fact]
    public async Task ENTREGA_COM_MAIS_DE_30_DIAS_E_EXPURGADA()
    {
        var (db, tx, amb) = await PrepararAsync("expurgo");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);
        await amb.Contatos.CriarAsync(
            new NovoContato("Maria", "84988887777", null, null, null, null, null, null, null), default);
        db.ChangeTracker.Clear();

        var entrega = Assert.Single(await EntregasAsync(db, amb));

        // Envelhece a linha por SQL: `InterceptorAuditoria` carimba `criado_em` em todo INSERT, e
        // é de propósito — não dá para nascer com data antiga pela aplicação.
        await db.EntregasWebhook.IgnoreQueryFilters().Where(e => e.Id == entrega.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(
                e => e.CriadoEm, amb.Relogio.GetUtcNow().UtcDateTime.AddDays(-31)));
        db.ChangeTracker.Clear();

        Assert.Equal(1, await amb.Motor.ExpurgarAntigasAsync());
        Assert.Empty(await EntregasAsync(db, amb));
    }

    [Fact]
    public async Task Entrega_recente_NAO_e_expurgada()
    {
        // O contra-teste: sem ele um expurgo que apagasse tudo passaria no teste acima.
        var (db, tx, amb) = await PrepararAsync("expurgo-nao");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb);
        await amb.Contatos.CriarAsync(
            new NovoContato("Maria", "84988887777", null, null, null, null, null, null, null), default);
        db.ChangeTracker.Clear();

        var entrega = Assert.Single(await EntregasAsync(db, amb));
        await db.EntregasWebhook.IgnoreQueryFilters().Where(e => e.Id == entrega.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(
                e => e.CriadoEm, amb.Relogio.GetUtcNow().UtcDateTime.AddDays(-29)));
        db.ChangeTracker.Clear();

        Assert.Equal(0, await amb.Motor.ExpurgarAntigasAsync());
        Assert.Single(await EntregasAsync(db, amb));
    }

    // ==================================================================== PII
    [Fact]
    public async Task MODO_SO_IDS_NAO_DEIXA_NOME_NEM_TELEFONE_SAIR()
    {
        var (db, tx, amb) = await PrepararAsync("so-ids");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(amb, m => m with { SomenteIds = true });

        await amb.Contatos.CriarAsync(
            new NovoContato("Marcos Antunes", "84988887777", "marcos@exemplo.com",
                null, null, null, null, null, null), default);
        db.ChangeTracker.Clear();

        var entrega = Assert.Single(await EntregasAsync(db, amb));

        Assert.DoesNotContain("Marcos", entrega.Payload);
        Assert.DoesNotContain("5584988887777", entrega.Payload);
        Assert.DoesNotContain("marcos@exemplo.com", entrega.Payload);

        // Mas o evento CHEGA — o modo não é "desligar", é "sem dado pessoal".
        using var doc = JsonDocument.Parse(entrega.Payload);
        Assert.Equal("lead.criado", doc.RootElement.GetProperty("evento").GetString());
        Assert.True(doc.RootElement.GetProperty("dados").GetProperty("id").GetInt64() > 0);
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto, RelogioFalso Relogio, DnsFalso Dns,
        ClienteWebhookFalso Cliente, IServicoWebhooks Webhooks, IServicoContatos Contatos,
        IServicoFunil Funil, MotorWebhooks Motor,
        Nexora.Infra.Evolution.ProcessadorEventoEvolution Processador);

    private static SalvarWebhook Configuracao(string url) =>
        new(url, Ativo: true, SomenteIds: false,
            EmLeadCriado: true, EmLeadMovido: true, EmVendaFechada: true,
            EmVendaPerdida: true, EmMensagemRecebida: false);

    /// <summary>Cria o webhook pelo SERVIÇO — é o caminho real, e é o que garante que a URL passou
    /// pela validação. Devolve o segredo revelado.</summary>
    private static async Task<string> ConfigurarAsync(
        Ambiente amb, Func<SalvarWebhook, SalvarWebhook>? ajustar = null)
    {
        var config = Configuracao(UrlOk);
        var revelado = await amb.Webhooks.SalvarAsync(ajustar?.Invoke(config) ?? config, default);

        if (revelado is not null) return revelado.Segredo;

        // Já existia (segunda chamada do mesmo teste): pega do banco.
        return (await amb.Webhooks.RegerarSegredoAsync(default)).Segredo;
    }

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo, RelogioFalso? relogio = null)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"wh-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        relogio ??= new RelogioFalso(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));

        var dns = new DnsFalso { ["webhook.cliente.com"] = ["203.0.113.10"] };
        var cliente = new ClienteWebhookFalso();
        var publicador = PublicadorDeTeste.Novo(db, relogio);

        return (db, tx, new Ambiente(
            cenario, ctx, relogio, dns, cliente,
            new ServicoWebhooks(db, ctx, dns, cliente, relogio),
            new ServicoContatos(db, ctx, publicador, new ColetorAuditoria(), relogio),
            new ServicoFunil(db, publicador, new ColetorAuditoria()),
            new MotorWebhooks(db, cliente, dns, relogio, NullLogger<MotorWebhooks>.Instance),
            new Nexora.Infra.Evolution.ProcessadorEventoEvolution(
                db, new ClienteWhatsAppFalso(), new ArmazenamentoFalso(), new NotificadorFalso(),
                publicador, relogio,
                NullLogger<Nexora.Infra.Evolution.ProcessadorEventoEvolution>.Instance)));
    }

    private static async Task<List<EntregaWebhook>> EntregasAsync(NexoraDbContext db, Ambiente amb)
    {
        db.ChangeTracker.Clear();
        return await db.EntregasWebhook.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.EmpresaId == amb.Cenario.Id)
            .OrderBy(e => e.Id).ToListAsync();
    }

    private static async Task LimparEntregasAsync(NexoraDbContext db, Ambiente amb)
    {
        await db.EntregasWebhook.IgnoreQueryFilters()
            .Where(e => e.EmpresaId == amb.Cenario.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
    }
}

/// <summary>O receptor de mentira. Guarda o que foi postado para o teste conferir a ASSINATURA
/// contra o corpo exato — que é a única coisa que prova que um receptor de verdade conseguiria
/// validar.</summary>
public sealed class ClienteWebhookFalso : IClienteWebhook
{
    public record Postagem(string Url, string Corpo, string Assinatura, long Timestamp, string Evento);

    public List<Postagem> Enviados { get; } = [];
    public int Chamadas => Enviados.Count;

    /// <summary>O que o "servidor do cliente" responde.</summary>
    public int Codigo { get; set; } = 200;

    /// <summary>Quanto ele demora. Usado para provar que a entrega NÃO acontece no caminho do
    /// usuário — se acontecesse, criar um contato levaria este tempo.</summary>
    public TimeSpan Demora { get; set; } = TimeSpan.Zero;

    public async Task<ResultadoEntrega> EntregarAsync(
        string url, string segredo, string corpo, string evento, Guid eventoId, CancellationToken ct)
    {
        if (Demora > TimeSpan.Zero) await Task.Delay(Demora, ct);

        // Assina com o MESMO código do cliente real: o teste confere a assinatura de verdade, não
        // uma string inventada aqui.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Enviados.Add(new Postagem(
            url, corpo, AssinaturaWebhook.Calcular(segredo, timestamp, corpo), timestamp, evento));

        return PoliticaEntrega.Aceitou(Codigo)
            ? new ResultadoEntrega(true, Codigo, null)
            : new ResultadoEntrega(false, Codigo, $"O receptor respondeu {Codigo}.");
    }
}
