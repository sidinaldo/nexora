using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core;
using Nexora.Core.Captacao;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Captacao;
using Nexora.Infra.Evolution;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>CAPTAÇÃO POR QR CODE E LINK (INT-2).
///
/// O centro deste arquivo é o caminho do WEBHOOK, e ele roda em TENANT ZERO — exatamente como em
/// produção. Se algum `IgnoreQueryFilters` faltar na busca do canal, a consulta volta vazia em
/// silêncio e NENHUM canal atribui nada, para sempre, sem erro em lugar nenhum. É o modo de falha
/// mais caro do bloco, e é o que estes testes existem para pegar.
///
/// O outro eixo é o que o rastreio NÃO faz: não adivinha, não falha, não reescreve.</summary>
[Collection("banco")]
public class CanaisDbTests(BancoTeste banco)
{
    private const string Telefone = "5584988887777";
    private const string Jid = "5584988887777@s.whatsapp.net";

    // ==================================================================== atribuição
    [Fact]
    public async Task MENSAGEM_COM_CODIGO_CONHECIDO_CRIA_CONTATO_COM_A_ORIGEM_DO_CANAL()
    {
        var (db, tx, amb) = await PrepararAsync("atribui");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Panfleto Julho", OrigemLead.Qrcode);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-C1",
                CodigoCanal.TextoDoLink(canal.Codigo)), default);

        db.ChangeTracker.Clear();
        var contato = await ContatoAsync(db, amb, Telefone);

        Assert.Equal(OrigemLead.Qrcode, contato.Origem);
        // O NOME do canal vai para `origem_detalhe`: é o que o vendedor lê no card para saber de
        // onde o lead veio. "qrcode" sozinho não diz qual panfleto.
        Assert.Equal("Panfleto Julho", contato.OrigemDetalhe);

        Assert.Equal(1, await LeadsAsync(db, canal.Id));
    }

    [Fact]
    public async Task A_ORIGEM_E_A_DO_CANAL_E_NAO_SEMPRE_qrcode()
    {
        // O mesmo mecanismo serve para link na bio e parceiro. Travar em `qrcode` faria o
        // dashboard agrupar tudo numa barra só, e a pergunta "de onde vêm meus leads" perderia a
        // resposta.
        var (db, tx, amb) = await PrepararAsync("origem");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Link na bio", OrigemLead.Instagram);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-O1",
                CodigoCanal.TextoDoLink(canal.Codigo)), default);

        db.ChangeTracker.Clear();
        var contato = await ContatoAsync(db, amb, Telefone);
        Assert.Equal(OrigemLead.Instagram, contato.Origem);
        Assert.Equal("Link na bio", contato.OrigemDetalhe);
    }

    [Fact]
    public async Task Codigo_e_reconhecido_com_caixa_diferente()
    {
        // Quem digita à mão varia a caixa. O código é guardado em minúsculas e a comparação
        // normaliza o PARÂMETRO — nunca a coluna, que precisa continuar indexável.
        var (db, tx, amb) = await PrepararAsync("caixa");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Balcão", OrigemLead.Qrcode);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-CX1",
                $"Olá! Tenho interesse. #{canal.Codigo.ToUpperInvariant()}"), default);

        db.ChangeTracker.Clear();
        Assert.Equal("Balcão", (await ContatoAsync(db, amb, Telefone)).OrigemDetalhe);
    }

    // ==================================================================== nunca falha
    [Fact]
    public async Task CODIGO_INEXISTENTE_CAI_EM_whatsapp_SEM_FALHAR()
    {
        // A pessoa pode digitar errado, ou o canal pode ter sido apagado depois de o panfleto
        // sair. Nos dois casos o lead ENTRA — só não é atribuído.
        var (db, tx, amb) = await PrepararAsync("inexistente");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-X1", "Olá! Tenho interesse. #zzzz"),
            default);

        db.ChangeTracker.Clear();
        var contato = await ContatoAsync(db, amb, Telefone);

        Assert.Equal(OrigemLead.Whatsapp, contato.Origem);
        Assert.Null(contato.OrigemDetalhe);
        // E a mensagem entrou normalmente: o rastreio não pode custar o lead.
        Assert.True(await db.Mensagens.IgnoreQueryFilters().AnyAsync(m => m.WaMessageId == "WA-X1"));
    }

    [Fact]
    public async Task MENSAGEM_SEM_CODIGO_CAI_EM_whatsapp()
    {
        // O caso ESPERADO, não uma falha: a pessoa apagou o texto antes de mandar, ou escreveu a
        // dela por cima. Acontece, e é por isso que o contador do canal é piso, não total.
        var (db, tx, amb) = await PrepararAsync("sem-codigo");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Panfleto", OrigemLead.Qrcode);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-S1", "oi, quero um orçamento"), default);

        db.ChangeTracker.Clear();
        var contato = await ContatoAsync(db, amb, Telefone);

        Assert.Equal(OrigemLead.Whatsapp, contato.Origem);
        Assert.Null(contato.OrigemDetalhe);

        // ===== NÃO ADIVINHA =====
        // Existe UM canal ativo na empresa, criado minutos antes. Um sistema que "ajudasse"
        // atribuindo por proximidade de horário ou por ser o único candidato passaria a inventar
        // origem — e atribuição errada é pior que ausente, porque entra no relatório parecendo
        // verdade e o cliente decide onde gastar em cima dela.
        Assert.Equal(0, await LeadsAsync(db, canal.Id));
    }

    // ==================================================================== isolamento
    [Fact]
    public async Task CODIGO_DE_CANAL_DE_OUTRA_EMPRESA_E_IGNORADO()
    {
        var ctx = new ContextoMutavel();   // tenant zero, como o webhook real
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await TenantLimpoAsync(db, "canal-isol-a");
        var outra = await TenantLimpoAsync(db, "canal-isol-b");

        // O canal existe — mas na OUTRA empresa.
        var alheio = await CriarCanalAsync(db, outra, "Panfleto da concorrente", OrigemLead.Qrcode);

        var processador = NovoProcessador(db, out var painel);
        await processador.ProcessarAsync(
            PayloadEvolution.Mensagem(minha.Conexao.InstanceName, Jid, "WA-ISO1",
                CodigoCanal.TextoDoLink(alheio.Codigo)), default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters()
            .SingleAsync(c => c.EmpresaId == minha.Id && c.Telefone == Telefone);

        Assert.Equal(OrigemLead.Whatsapp, contato.Origem);
        Assert.Null(contato.OrigemDetalhe);

        // E o contador da OUTRA empresa não se mexeu.
        Assert.Equal(0, await LeadsAsync(db, alheio.Id));
        Assert.Single(painel.Contatos);
    }

    // ==================================================================== só na criação
    [Fact]
    public async Task CONTATO_QUE_JA_EXISTE_NAO_TEM_A_ORIGEM_REESCRITA()
    {
        // ===== A PRIMEIRA ORIGEM É A VERDADEIRA =====
        // O cliente que apareceu pelo Instagram em março e voltou pelo panfleto de julho continua
        // sendo lead do Instagram. Sobrescrever destruiria o relatório: toda campanha nova
        // reivindicaria os leads antigos, e a última sempre pareceria a melhor.
        var (db, tx, amb) = await PrepararAsync("nao-reescreve");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Panfleto Julho", OrigemLead.Qrcode);

        var existente = new Contato
        {
            EmpresaId = amb.Cenario.Id, Nome = "Cliente Antigo", Telefone = Telefone,
            Origem = OrigemLead.Indicacao, OrigemDetalhe = "Parceria com a padaria",
            EtapaId = amb.Cenario.PrimeiraEtapa.Id
        };
        db.Contatos.Add(existente);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-R1",
                CodigoCanal.TextoDoLink(canal.Codigo)), default);

        db.ChangeTracker.Clear();
        var depois = await ContatoAsync(db, amb, Telefone);

        Assert.Equal(OrigemLead.Indicacao, depois.Origem);
        Assert.Equal("Parceria com a padaria", depois.OrigemDetalhe);
        Assert.Equal(existente.Id, depois.Id);   // e não criou um segundo contato

        // O contador também NÃO sobe: contar aqui somaria a mesma pessoa duas vezes.
        Assert.Equal(0, await LeadsAsync(db, canal.Id));
    }

    [Fact]
    public async Task CONTAGEM_INCREMENTA_SO_NA_CRIACAO()
    {
        // Duas mensagens da MESMA pessoa, as duas com o código. A segunda não é um lead novo.
        var (db, tx, amb) = await PrepararAsync("conta-uma");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Vitrine", OrigemLead.Qrcode);
        var texto = CodigoCanal.TextoDoLink(canal.Codigo);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-N1", texto), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-N2", texto, timestamp: 1780000100),
            default);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await LeadsAsync(db, canal.Id));
        Assert.Equal(1, await db.Contatos.IgnoreQueryFilters()
            .CountAsync(c => c.EmpresaId == amb.Cenario.Id));
    }

    [Fact]
    public async Task Reentrega_do_MESMO_webhook_nao_conta_duas_vezes()
    {
        // A Evolution reentrega até receber 2xx. O dedupe de mensagem já existia; o que se prova
        // aqui é que o contador do canal não passou por fora dele.
        var (db, tx, amb) = await PrepararAsync("reentrega");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Adesivo", OrigemLead.Qrcode);
        var payload = PayloadEvolution.Mensagem(
            amb.Instancia, Jid, "WA-RE1", CodigoCanal.TextoDoLink(canal.Codigo));

        await amb.Processador.ProcessarAsync(payload, default);
        await amb.Processador.ProcessarAsync(payload, default);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await LeadsAsync(db, canal.Id));
    }

    [Fact]
    public async Task CANAL_DESATIVADO_NAO_ATRIBUI_MAS_O_LEAD_ENTRA()
    {
        // Desativar é como o cliente diz "essa campanha acabou". O material impresso continua no
        // mundo e quem escanear ainda cai na conversa — só que sem carimbo.
        var (db, tx, amb) = await PrepararAsync("desativado");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Campanha velha", OrigemLead.Qrcode);
        await ComoDono(amb).AlternarAtivoAsync(canal.Id, false, default);
        ComoWebhook(amb);
        db.ChangeTracker.Clear();

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-D1",
                CodigoCanal.TextoDoLink(canal.Codigo)), default);

        db.ChangeTracker.Clear();
        var contato = await ContatoAsync(db, amb, Telefone);

        Assert.Equal(OrigemLead.Whatsapp, contato.Origem);
        Assert.Equal(0, await LeadsAsync(db, canal.Id));
    }

    // ==================================================================== a mensagem crua
    [Fact]
    public async Task O_TEXTO_DA_MENSAGEM_E_GRAVADO_COMO_VEIO_COM_O_CODIGO()
    {
        // ===== A MENSAGEM É REGISTRO DO QUE ACONTECEU =====
        // Limpar o código antes de gravar deixaria a thread do vendedor diferente do que o cliente
        // realmente mandou. Se um dia o código incomodar na tela, quem esconde é a EXIBIÇÃO.
        var (db, tx, amb) = await PrepararAsync("texto-cru");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Balcão", OrigemLead.Qrcode);
        var texto = CodigoCanal.TextoDoLink(canal.Codigo);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-T1", texto), default);

        db.ChangeTracker.Clear();
        var mensagem = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.WaMessageId == "WA-T1");

        Assert.Equal(texto, mensagem.Texto);
        Assert.Contains($"#{canal.Codigo}", mensagem.Texto);
    }

    // ==================================================================== o link
    [Fact]
    public async Task O_LINK_APONTA_PARA_O_NUMERO_DA_CONEXAO_E_ESCAPA_O_TEXTO()
    {
        // ===== A FALHA SILENCIOSA DESTE BLOCO =====
        // O `#` do código é FRAGMENTO de URL. Sem escapar, tudo dali para a frente some antes de
        // chegar ao WhatsApp: o link abre a conversa, a frase chega truncada, e o código nunca
        // aparece. Funciona o suficiente para ninguém desconfiar, e a atribuição nunca acontece.
        var (db, tx, amb) = await PrepararAsync("link");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Cartão de visita", OrigemLead.Qrcode);
        db.ChangeTracker.Clear();

        var dto = (await ComoDono(amb).ListarAsync(default)).Itens.Single(c => c.Id == canal.Id);

        Assert.StartsWith($"https://wa.me/{amb.Cenario.Conexao.Numero}?text=", dto.Link);
        Assert.DoesNotContain("#", dto.Link);                    // escapado
        Assert.Contains("%23", dto.Link);                        // como %23
        Assert.Equal(CodigoCanal.TextoDoLink(canal.Codigo), dto.Texto);

        // E o texto do DTO é exatamente o que o webhook procura — o ciclo fecha.
        Assert.Equal([canal.Codigo], CodigoCanal.Extrair(dto.Texto));
    }

    [Fact]
    public async Task O_QR_DA_API_DECODIFICA_PARA_O_LINK_DO_CANAL()
    {
        var (db, tx, amb) = await PrepararAsync("qr-api");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Placa da vitrine", OrigemLead.Qrcode);
        db.ChangeTracker.Clear();

        var servico = ComoDono(amb);
        var dto = (await servico.ListarAsync(default)).Itens.Single(c => c.Id == canal.Id);
        var png = await servico.PngAsync(canal.Id, default);

        Assert.NotNull(png);
        Assert.Equal(dto.Link, Unidade.LeitorPngQr.Ler(png!.Value.Png));

        // O nome do arquivo leva o código: seis meses depois, com quatro SVGs na pasta de
        // downloads, é o que diz qual é qual.
        Assert.Contains(canal.Codigo, png.Value.NomeArquivo);
        Assert.EndsWith(".png", png.Value.NomeArquivo);

        var svg = await servico.SvgAsync(canal.Id, default);
        Assert.NotNull(svg);
        Assert.StartsWith("<svg", svg!.Svg.TrimStart());
    }

    // ==================================================================== CRUD
    [Fact]
    public async Task EMPRESA_SEM_CONEXAO_PAREADA_NAO_GERA_CANAL()
    {
        // O link embute o telefone. Sem número, sairia `https://wa.me/?text=...` — um QR que
        // escaneia, abre o WhatsApp e não leva a lugar nenhum. Impresso em panfleto, é dinheiro
        // jogado fora, e o cliente só descobre depois da gráfica.
        var (db, tx, amb) = await PrepararAsync("sem-numero");
        using var _ = db; using var __ = tx;

        await db.Conexoes.IgnoreQueryFilters()
            .Where(c => c.EmpresaId == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Numero, (string?)null));
        db.ChangeTracker.Clear();

        var servico = ComoDono(amb);

        var lista = await servico.ListarAsync(default);
        Assert.Empty(lista.Conexoes);
        Assert.False(lista.PodeCriar);

        // O id da conexão EXISTE e é desta empresa — o que falta é o número pareado. Passar um id
        // válido é o ponto: a recusa não pode vir de "conexão não encontrada".
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.CriarAsync(
                new NovoCanal("Balcão", amb.Cenario.Conexao.Id, "qrcode"), default));
        Assert.Contains("conectado", erro.Message);

        db.ChangeTracker.Clear();
        Assert.Empty((await servico.ListarAsync(default)).Itens);
    }

    [Fact]
    public async Task CANAL_COM_LEAD_NAO_PODE_SER_EXCLUIDO()
    {
        var (db, tx, amb) = await PrepararAsync("com-lead");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Panfleto Julho", OrigemLead.Qrcode);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-L1",
                CodigoCanal.TextoDoLink(canal.Codigo)), default);
        db.ChangeTracker.Clear();

        var servico = ComoDono(amb);
        var dto = (await servico.ListarAsync(default)).Itens.Single(c => c.Id == canal.Id);
        Assert.Equal(1, dto.LeadsRecebidos);
        Assert.False(dto.PodeRemover);
        Assert.Contains("desative", dto.MotivoNaoRemove);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.RemoverAsync(canal.Id, default));

        // O MESMO texto que a lista mostrou no botão. Duas mensagens diferentes para a mesma
        // recusa seriam duas cópias da regra, e elas divergiriam.
        Assert.Equal(dto.MotivoNaoRemove, erro.Message);

        db.ChangeTracker.Clear();
        Assert.Single((await servico.ListarAsync(default)).Itens);
    }

    [Fact]
    public async Task Canal_sem_lead_pode_ser_excluido()
    {
        var (db, tx, amb) = await PrepararAsync("sem-lead");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Teste", OrigemLead.Qrcode);
        db.ChangeTracker.Clear();

        var servico = ComoDono(amb);
        await servico.RemoverAsync(canal.Id, default);
        db.ChangeTracker.Clear();

        Assert.Empty((await servico.ListarAsync(default)).Itens);
    }

    [Fact]
    public async Task RENOMEAR_NAO_MUDA_O_CODIGO_NEM_REESCREVE_OS_LEADS_QUE_JA_VIERAM()
    {
        // ===== O CÓDIGO JÁ ESTÁ IMPRESSO =====
        // Trocar o código transformaria todo material distribuído em link sem atribuição —
        // funcionando, mas mudo. E `origem_detalhe` é uma CÓPIA do nome no dia do lead: reescrever
        // apagaria a história do lead para acertar um rótulo.
        var (db, tx, amb) = await PrepararAsync("renomear");
        using var _ = db; using var __ = tx;

        var canal = await CanalAsync(amb, "Panfleto Julho", OrigemLead.Qrcode);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-RN1",
                CodigoCanal.TextoDoLink(canal.Codigo)), default);
        db.ChangeTracker.Clear();

        var servico = ComoDono(amb);
        await servico.AtualizarAsync(
            canal.Id, new NovoCanal("Panfleto Agosto", amb.Cenario.Conexao.Id, "instagram"), default);
        db.ChangeTracker.Clear();

        var dto = (await servico.ListarAsync(default)).Itens.Single(c => c.Id == canal.Id);
        Assert.Equal("Panfleto Agosto", dto.Nome);
        Assert.Equal("instagram", dto.Origem);
        Assert.Equal(canal.Codigo, dto.Codigo);            // o código NÃO mudou

        var contato = await ContatoAsync(db, amb, Telefone);
        Assert.Equal("Panfleto Julho", contato.OrigemDetalhe);   // o lead antigo NÃO foi reescrito
        Assert.Equal(OrigemLead.Qrcode, contato.Origem);
    }

    [Fact]
    public async Task Nome_repetido_na_mesma_empresa_e_recusado()
    {
        // O nome vai para `origem_detalhe`. Dois canais "Panfleto" tornam o relatório de origem
        // impossível de ler.
        var (db, tx, amb) = await PrepararAsync("nome-repetido");
        using var _ = db; using var __ = tx;

        await CanalAsync(amb, "Panfleto", OrigemLead.Qrcode);
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => ComoDono(amb).CriarAsync(
            new NovoCanal("Panfleto", amb.Cenario.Conexao.Id, "qrcode"), default));
    }

    [Fact]
    public async Task CANAL_DE_OUTRA_EMPRESA_NAO_EXISTE_POR_NENHUM_CAMINHO()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await TenantLimpoAsync(db, "canal-crud-a");
        var outra = await TenantLimpoAsync(db, "canal-crud-b");
        var alheio = await CriarCanalAsync(db, outra, "Da concorrente", OrigemLead.Qrcode);

        ctx.EmpresaId = minha.Id;
        ctx.UsuarioId = minha.Dono.Id;
        ctx.Papel = "dono";
        var canais = new ServicoCanais(db, ctx, new GeradorQrCoder());

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => canais.AtualizarAsync(alheio.Id, new NovoCanal("Roubado", minha.Conexao.Id, "qrcode"), default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => canais.RemoverAsync(alheio.Id, default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => canais.AlternarAtivoAsync(alheio.Id, false, default));

        // O QR devolve NULO — o mesmo nulo de "não existe", senão a diferença entre as respostas
        // conta que o canal existe em outro tenant.
        Assert.Null(await canais.SvgAsync(alheio.Id, default));
        Assert.Null(await canais.PngAsync(alheio.Id, default));

        db.ChangeTracker.Clear();
        Assert.Equal("Da concorrente", (await db.CanaisCaptacao.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Id == alheio.Id)).Nome);
        Assert.Empty((await canais.ListarAsync(default)).Itens);
    }

    [Fact]
    public async Task Codigo_e_unico_dentro_da_empresa_e_convive_entre_empresas()
    {
        // O código NÃO resolve o tenant (quem resolve é o `instance_name`), então único global
        // gastaria espaço de código à toa. Único por empresa é o que o webhook precisa.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var a = await TenantLimpoAsync(db, "canal-cod-a");
        var b = await TenantLimpoAsync(db, "canal-cod-b");

        var na = new CanalCaptacao
        {
            EmpresaId = a.Id, Nome = "Panfleto", Codigo = "k7m2", ConexaoId = a.Conexao.Id
        };
        var nb = new CanalCaptacao
        {
            EmpresaId = b.Id, Nome = "Panfleto", Codigo = "k7m2", ConexaoId = b.Conexao.Id
        };
        db.CanaisCaptacao.AddRange(na, nb);
        await db.SaveChangesAsync();       // o MESMO código nas duas empresas: passa
        db.ChangeTracker.Clear();

        db.CanaisCaptacao.Add(new CanalCaptacao
        {
            EmpresaId = a.Id, Nome = "Outro", Codigo = "k7m2", ConexaoId = a.Conexao.Id
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, string Instancia, ProcessadorEventoEvolution Processador,
        IServicoCanais Canais, ContextoMutavel Contexto, NotificadorFalso Painel);

    /// <summary>Monta o processador em TENANT ZERO (como o webhook real) e o serviço de canais
    /// AUTENTICADO — os dois sobre o mesmo DbContext, com o mesmo `ContextoMutavel`.
    ///
    /// O `EmpresaId` do contexto fica em 0: quem precisa de sessão é só o `ServicoCanais`, e ele
    /// recebe o tenant na hora de usar. Deixar 0 durante o webhook é o que faz este arquivo provar
    /// que o `IgnoreQueryFilters` da busca do canal está no lugar.</summary>
    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await TenantLimpoAsync(db, $"canal-{sufixo}");

        var processador = NovoProcessador(db, out var painel);
        var canais = new ServicoCanais(db, ctx, new GeradorQrCoder());

        return (db, tx, new Ambiente(
            cenario, cenario.Conexao.InstanceName, processador, canais, ctx, painel));
    }

    private static ProcessadorEventoEvolution NovoProcessador(
        NexoraDbContext db, out NotificadorFalso painel)
    {
        painel = new NotificadorFalso();
        return new ProcessadorEventoEvolution(
            db, new ClienteWhatsAppFalso(), new ArmazenamentoFalso(), painel,
            PublicadorDeTeste.Novo(db),
            TimeProvider.System, NullLogger<ProcessadorEventoEvolution>.Instance);
    }

    /// <summary>Um tenant sem o contato/conversa/mensagem de exemplo do semeador: este arquivo
    /// conta contatos criados PELO WEBHOOK, e a linha de exemplo estragaria a contagem.</summary>
    private static async Task<Cenario> TenantLimpoAsync(NexoraDbContext db, string sufixo)
    {
        var cenario = await Semeador.TenantAsync(db, sufixo);

        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        return cenario;
    }

    /// <summary>O serviço de canais com a SESSÃO do dono no contexto.
    ///
    /// Existe porque este arquivo alterna entre os dois mundos de propósito: o webhook roda em
    /// tenant zero e o painel roda autenticado, sobre o MESMO DbContext. Sem o troco explícito, um
    /// teste passaria por acidente — a lista voltaria vazia e o `Criar` recusaria por "sem número
    /// conectado" em vez da regra que se queria provar.</summary>
    private static IServicoCanais ComoDono(Ambiente amb)
    {
        amb.Contexto.EmpresaId = amb.Cenario.Id;
        amb.Contexto.UsuarioId = amb.Cenario.Dono.Id;
        amb.Contexto.Papel = "dono";
        return amb.Canais;
    }

    /// <summary>Volta ao tenant zero. O webhook roda sem sessão, e é assim que ele tem que rodar
    /// enquanto o teste dispara os payloads — é o que faz este arquivo provar que o
    /// `IgnoreQueryFilters` da busca do canal está no lugar.</summary>
    private static void ComoWebhook(Ambiente amb) => amb.Contexto.EmpresaId = 0;

    /// <summary>Cria um canal PELO SERVIÇO — é o caminho real, e é o que garante que o código
    /// sorteado é do formato que o webhook procura. Devolve o contexto ao tenant zero.</summary>
    private static async Task<CanalCaptacao> CanalAsync(Ambiente amb, string nome, OrigemLead origem)
    {
        var servico = ComoDono(amb);

        var id = await servico.CriarAsync(
            new NovoCanal(nome, amb.Cenario.Conexao.Id, origem.ToString().ToLowerInvariant()), default);

        var dto = (await servico.ListarAsync(default)).Itens.Single(c => c.Id == id);

        ComoWebhook(amb);

        return new CanalCaptacao
        {
            Id = dto.Id, EmpresaId = amb.Cenario.Id, Nome = dto.Nome,
            Codigo = dto.Codigo, ConexaoId = dto.ConexaoId, Origem = origem
        };
    }

    private static async Task<CanalCaptacao> CriarCanalAsync(
        NexoraDbContext db, Cenario cenario, string nome, OrigemLead origem)
    {
        var canal = new CanalCaptacao
        {
            EmpresaId = cenario.Id, Nome = nome, Codigo = CodigoCanal.Gerar(),
            ConexaoId = cenario.Conexao.Id, Origem = origem
        };
        db.CanaisCaptacao.Add(canal);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return canal;
    }

    private static async Task<Contato> ContatoAsync(NexoraDbContext db, Ambiente amb, string telefone) =>
        await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.EmpresaId == amb.Cenario.Id && c.Telefone == telefone);

    private static async Task<int> LeadsAsync(NexoraDbContext db, long canalId)
    {
        db.ChangeTracker.Clear();
        return await db.CanaisCaptacao.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == canalId).Select(c => c.LeadsRecebidos).SingleAsync();
    }
}
