using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Api.Controllers;
using Nexora.Api.Seguranca;
using Nexora.Core;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;
using Nexora.Tests.Unidade;

namespace Nexora.Tests.Integracao;

/// <summary>Captação por formulário do site: um endpoint PÚBLICO que aceita ESCRITA.
///
/// O risco concentrado aqui é o tenant zero — sem sessão, `EmpresaId` é 0 e o query filter global
/// devolve vazio EM SILÊNCIO. O lead simplesmente não aparece, e não há erro em lugar nenhum.</summary>
[Collection("banco")]
public class CapturaDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ==================================================================== o caminho feliz
    [Fact]
    public async Task CAPTURA_CRIA_CONTATO_COM_ORIGEM_SITE_E_SEM_NEGOCIO()
    {
        var (db, tx, amb) = await PrepararAsync("feliz");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Orçamento do site");

        var resultado = await amb.Captura.ReceberAsync(
            chave, new LeadDoFormulario("Marcos Antunes", "(84) 98888-7777", "marcos@exemplo.com",
                "Queria um orçamento de troca de óleo", null),
            DadosDaConexao.De("https://www.cliente.com.br"), default);

        Assert.Equal(ResultadoCaptura.ContatoCriado, resultado);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.EmpresaId == amb.Cenario.Id && c.Nome == "Marcos Antunes");

        Assert.Equal(OrigemLead.Site, contato.Origem);
        Assert.Equal("Orçamento do site", contato.OrigemDetalhe);
        Assert.Equal("5584988887777", contato.Telefone);   // canonicalizado, não como foi digitado
        Assert.Null(contato.ResponsavelId);                // cai em "não atribuídas"
        Assert.Contains("troca de óleo", contato.Observacoes);

        // ===================== O TESTE INVERTEU DE ENUNCIADO (E6) =====================
        // Ele se chamava `..._NA_ETAPA_DE_MENOR_ORDEM_...` e exigia que o lead nascesse no topo
        // do funil padrao. Agora exige o contrario, e a razao esta na propria tela: quem preencheu
        // um formulario pediu contato, nao declarou um negocio. Abrir negociacao para todos enche
        // o quadro de card que ninguem trabalha, e o vendedor aprende a ignorar o quadro.
        //
        // ⚠️ E AFIRMACAO MAIS FORTE QUE A ANTERIOR: "nenhuma negociacao" nao passa por acidente
        // como "a etapa certa" passaria — qualquer volta ao comportamento antigo reprova aqui.
        // ==============================================================================
        Assert.False(await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(n => n.ContatoId == contato.Id));

        // E o contador do formulário andou.
        Assert.Equal(1, (await db.FormulariosCaptura.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(f => f.Chave == chave)).LeadsRecebidos);
    }

    [Fact]
    public async Task O_LEAD_GANHA_LEMBRETE_DE_PRIMEIRO_CONTATO_E_NOTIFICACAO_NO_PAINEL()
    {
        // O lead chegou, mas NÃO há conversa: ninguém mandou mensagem no WhatsApp. Sem o
        // lembrete, ele fica parado no funil esperando alguém reparar.
        var (db, tx, amb) = await PrepararAsync("lembrete");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Landing da campanha");
        await amb.Captura.ReceberAsync(
            chave, new LeadDoFormulario("Juliana Prado", "84988887777", null, null, null), DadosDaConexao.Nenhuma, default);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.EmpresaId == amb.Cenario.Id && l.Origem == OrigemLembrete.Automatico);

        Assert.Contains("Primeiro contato", lembrete.Titulo);
        Assert.Contains("Landing da campanha", lembrete.Titulo);
        Assert.Equal(DateOnly.FromDateTime(QuintaDeManha.UtcDateTime), lembrete.DataAlvo);

        // ===== NENHUMA MENSAGEM DE WHATSAPP =====
        // O lead deu o telefone num formulário; não iniciou conversa. Mensagem não solicitada é
        // o caminho curto para o número ser denunciado.
        Assert.False(lembrete.EnviaMensagem);
        Assert.Null(lembrete.TextoMensagem);

        // E o badge sobe na hora.
        Assert.Single(amb.Painel.Contatos);
        Assert.Equal("Juliana Prado", amb.Painel.Contatos[0].Nome);
    }

    [Fact]
    public async Task NENHUMA_MENSAGEM_DE_WHATSAPP_E_DISPARADA_PELA_CAPTURA()
    {
        // A prova pelo outro lado: a tabela `mensagens` é a outbox — se alguma linha de SAÍDA
        // aparecesse, ela viraria disparo na próxima drenagem do motor.
        var (db, tx, amb) = await PrepararAsync("sem-whatsapp");
        using var _ = db; using var __ = tx;

        var antes = await db.Mensagens.IgnoreQueryFilters()
            .CountAsync(m => m.EmpresaId == amb.Cenario.Id);

        var chave = await FormularioAsync(db, amb, "Rodapé");
        await amb.Captura.ReceberAsync(
            chave, new LeadDoFormulario("Rafael Bezerra", "84988887777", null, null, null), DadosDaConexao.Nenhuma, default);

        db.ChangeTracker.Clear();
        Assert.Equal(antes, await db.Mensagens.IgnoreQueryFilters()
            .CountAsync(m => m.EmpresaId == amb.Cenario.Id));
    }

    // ==================================================================== isolamento
    [Fact]
    public async Task A_CHAVE_DA_EMPRESA_A_NUNCA_ESCREVE_NA_EMPRESA_B()
    {
        // ===================== A ARMADILHA DESTE BLOCO =====================
        // Sem sessão, `EmpresaId` é 0. Se a captura confiasse no query filter, ela não acharia o
        // formulário e o lead sumiria — ou, pior, escreveria no tenant errado. A chave é a única
        // fonte de verdade aqui, e toda consulta filtra por `empresaId` explicitamente.
        // ==================================================================
        var (db, tx, amb) = await PrepararAsync("tenant-a");
        using var _ = db; using var __ = tx;

        var outra = await Semeador.TenantAsync(db, "captura-tenant-b");
        var chaveDeB = await FormularioAsync(db, amb, "Site da B", outra.Id);

        // O contexto está apontando para A — e mesmo assim o lead tem que cair em B, porque a
        // CHAVE é de B. É exatamente o inverso do erro clássico.
        amb.Contexto.EmpresaId = amb.Cenario.Id;

        await amb.Captura.ReceberAsync(
            chaveDeB, new LeadDoFormulario("Lead da B", "84988887777", null, null, null), DadosDaConexao.Nenhuma, default);

        db.ChangeTracker.Clear();
        Assert.True(await db.Contatos.IgnoreQueryFilters()
            .AnyAsync(c => c.EmpresaId == outra.Id && c.Nome == "Lead da B"));
        Assert.False(await db.Contatos.IgnoreQueryFilters()
            .AnyAsync(c => c.EmpresaId == amb.Cenario.Id && c.Nome == "Lead da B"));
    }

    [Fact]
    public async Task A_CAPTURA_FUNCIONA_COM_O_CONTEXTO_ZERADO()
    {
        // O caminho REAL de produção: requisição pública, sem sessão nenhuma. Se algo aqui
        // dependesse do query filter, este teste falharia e os outros passariam.
        var (db, tx, amb) = await PrepararAsync("tenant-zero");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Público");

        amb.Contexto.EmpresaId = 0;   // como chega de verdade
        amb.Contexto.UsuarioId = 0;
        amb.Contexto.Papel = null;

        var resultado = await amb.Captura.ReceberAsync(
            chave, new LeadDoFormulario("Sem Sessão", "84988887777", null, null, null), DadosDaConexao.Nenhuma, default);

        Assert.Equal(ResultadoCaptura.ContatoCriado, resultado);

        db.ChangeTracker.Clear();
        Assert.True(await db.Contatos.IgnoreQueryFilters()
            .AnyAsync(c => c.EmpresaId == amb.Cenario.Id && c.Nome == "Sem Sessão"));
    }

    // ==================================================================== chave
    [Fact]
    public async Task CHAVE_INVALIDA_OU_REVOGADA_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("chave");
        using var _ = db; using var __ = tx;

        var lead = new LeadDoFormulario("Alguém", "84988887777", null, null, null);

        // Inexistente.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Captura.ReceberAsync("chave-que-nao-existe", lead, DadosDaConexao.Nenhuma, default));

        // Desativado — mesma mensagem da inexistente, de propósito: distinguir contaria a quem
        // sonda que a chave existe e só está pausada.
        var chave = await FormularioAsync(db, amb, "Pausado");
        await db.FormulariosCaptura.IgnoreQueryFilters().Where(f => f.Chave == chave)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Ativo, false));
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Captura.ReceberAsync(chave, lead, DadosDaConexao.Nenhuma, default));
        Assert.Equal("Formulário não encontrado.", erro.Message);

        // REGERAR também invalida a anterior na hora — é o que se faz quando a chave vaza.
        var novo = await FormularioAsync(db, amb, "Vazado");
        var idDoVazado = await db.FormulariosCaptura.IgnoreQueryFilters()
            .Where(f => f.Chave == novo).Select(f => f.Id).SingleAsync();
        amb.Contexto.EmpresaId = amb.Cenario.Id;
        await amb.Formularios.RegerarChaveAsync(idDoVazado, default);
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Captura.ReceberAsync(novo, lead, DadosDaConexao.Nenhuma, default));
    }

    // ==================================================================== duplicata
    [Fact]
    public async Task TELEFONE_JA_EXISTENTE_GERA_LEMBRETE_E_NAO_CONTATO_DUPLICADO()
    {
        // ===================== POR QUE NÃO DEIXAR O BANCO BARRAR =====================
        // `uq_contatos_telefone` recusaria — e viraria 500 no formulário do site do cliente. Erro
        // de banco não é fluxo de controle. Aqui a duplicata é uma CONSULTA, e o resultado é um
        // recado para quem já cuida do contato.
        // ============================================================================
        var (db, tx, amb) = await PrepararAsync("duplicata");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Contato");
        var lead = new LeadDoFormulario("Camila Nogueira", "84988887777", null, "Voltei a precisar", null);

        Assert.Equal(ResultadoCaptura.ContatoCriado,
            await amb.Captura.ReceberAsync(chave, lead, DadosDaConexao.Nenhuma, default));

        var segunda = await amb.Captura.ReceberAsync(chave, lead, DadosDaConexao.Nenhuma, default);
        Assert.Equal(ResultadoCaptura.LembreteParaContatoExistente, segunda);

        db.ChangeTracker.Clear();

        // UM contato, não dois.
        Assert.Equal(1, await db.Contatos.IgnoreQueryFilters()
            .CountAsync(c => c.EmpresaId == amb.Cenario.Id && c.Telefone == "5584988887777"));

        // E o lembrete MANUAL carrega o que a pessoa escreveu.
        var manual = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.EmpresaId == amb.Cenario.Id && l.Origem == OrigemLembrete.Manual);
        Assert.Contains("Voltei a precisar", manual.Observacao);
        Assert.False(manual.EnviaMensagem);

        // Os dois envios contam como lead recebido — é o número de submissões aceitas.
        Assert.Equal(2, (await db.FormulariosCaptura.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(f => f.Chave == chave)).LeadsRecebidos);
    }

    [Fact]
    public async Task SEGUNDO_LEMBRETE_AUTOMATICO_NO_MESMO_DIA_E_BARRADO_SEM_EXCECAO()
    {
        // ===================== O TETO DIÁRIO, E POR QUE A GUARDA É DA APLICAÇÃO =====================
        // `uq_lembrete_teto_diario` é PARCIAL: só cobre `envia_mensagem = true`. O lembrete da
        // captura tem `envia_mensagem = false` — obrigatoriamente, porque ela não pode disparar
        // WhatsApp —, então o índice NÃO o cobre e o banco não impediria um segundo.
        //
        // A guarda é explícita no serviço: já existe automático pendente hoje? Não cria outro.
        // ============================================================================================
        var (db, tx, amb) = await PrepararAsync("teto");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Teto");

        // O contato do cenário já existe; damos a ele um lembrete automático de hoje, como o
        // motor de follow-up faria.
        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Cenario.Contato.Id,
            Origem = OrigemLembrete.Automatico,
            Status = StatusLembrete.Pendente,
            DataAlvo = DateOnly.FromDateTime(QuintaDeManha.UtcDateTime),
            Titulo = "Retomar contato",
            EnviaMensagem = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // O MESMO telefone chega pelo formulário: cai no caminho de duplicata, que cria lembrete
        // MANUAL — e nenhum automático a mais.
        var resultado = await amb.Captura.ReceberAsync(
            chave,
            new LeadDoFormulario("Contato do cenário", amb.Cenario.Contato.Telefone, null, null, null),
            DadosDaConexao.Nenhuma, default);

        Assert.Equal(ResultadoCaptura.LembreteParaContatoExistente, resultado);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Lembretes.IgnoreQueryFilters().CountAsync(
            l => l.ContatoId == amb.Cenario.Contato.Id && l.Origem == OrigemLembrete.Automatico));
    }

    // ==================================================================== proteções
    [Fact]
    public async Task HONEYPOT_PREENCHIDO_RESPONDE_SUCESSO_E_NAO_CRIA_NADA()
    {
        // Bot que recebe ERRO tenta de novo com outra variação. Bot que recebe SUCESSO risca o
        // alvo da lista. Por isso não é exceção: é descarte silencioso com 200.
        var (db, tx, amb) = await PrepararAsync("honeypot");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Com armadilha");
        var antes = await db.Contatos.IgnoreQueryFilters().CountAsync(c => c.EmpresaId == amb.Cenario.Id);

        var resultado = await amb.Captura.ReceberAsync(
            chave,
            new LeadDoFormulario("Bot Silva", "84988887777", "bot@spam.com", "compre agora",
                Armadilha: "http://spam.example"),
            DadosDaConexao.Nenhuma, default);

        Assert.Equal(ResultadoCaptura.DescartadoComoBot, resultado);

        db.ChangeTracker.Clear();
        Assert.Equal(antes, await db.Contatos.IgnoreQueryFilters()
            .CountAsync(c => c.EmpresaId == amb.Cenario.Id));

        // Nem o contador do formulário anda: não foi um lead.
        Assert.Equal(0, (await db.FormulariosCaptura.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(f => f.Chave == chave)).LeadsRecebidos);
    }

    [Fact]
    public async Task ORIGEM_NAO_PERMITIDA_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("origem");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Só do site", dominio: "www.cliente.com.br");
        var lead = new LeadDoFormulario("Alguém", "84988887777", null, null, null);

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Captura.ReceberAsync(chave, lead, DadosDaConexao.De("https://site-do-golpista.com"), default));

        // O domínio certo passa, com esquema e tudo — o serviço compara só o HOST, porque o
        // navegador manda `https://www.cliente.com.br` e o cliente cadastra `www.cliente.com.br`.
        Assert.Equal(ResultadoCaptura.ContatoCriado,
            await amb.Captura.ReceberAsync(chave, lead, DadosDaConexao.De("https://www.cliente.com.br"), default));
    }

    [Fact]
    public async Task Sem_cabecalho_Origin_a_checagem_de_dominio_nao_barra()
    {
        // A camada 3 existe para cortar abuso VIA NAVEGADOR. Sem `Origin` não há navegador na
        // frente (curl, servidor, app), e recusar aí bloquearia integração legítima sem impedir
        // nada de quem monta a requisição à mão.
        var (db, tx, amb) = await PrepararAsync("sem-origin");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Via servidor", dominio: "www.cliente.com.br");

        Assert.Equal(ResultadoCaptura.ContatoCriado, await amb.Captura.ReceberAsync(
            chave, new LeadDoFormulario("Integração", "84988887777", null, null, null), DadosDaConexao.Nenhuma, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("não é telefone")]
    [InlineData("9999")]
    public async Task TELEFONE_INVALIDO_E_RECUSADO(string telefone)
    {
        // Sem telefone válido o lead não serve para nada: é por ele que a conversa do WhatsApp
        // vai casar com o contato quando a pessoa escrever.
        var (db, tx, amb) = await PrepararAsync($"tel-{telefone.Length}");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Validação");

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Captura.ReceberAsync(
            chave, new LeadDoFormulario("Fulano", telefone, null, null, null), DadosDaConexao.Nenhuma, default));
    }

    [Fact]
    public async Task Nome_curto_e_recusado_e_mensagem_longa_e_cortada()
    {
        var (db, tx, amb) = await PrepararAsync("tamanhos");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Tamanhos");

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Captura.ReceberAsync(
            chave, new LeadDoFormulario("A", "84988887777", null, null, null), DadosDaConexao.Nenhuma, default));

        // Corpo com teto: sem isso, um POST com megabytes de texto vira uma linha gigante no
        // banco e um cartão ilegível na tela.
        await amb.Captura.ReceberAsync(
            chave,
            new LeadDoFormulario("Nome Válido", "84988887777", null, new string('x', 5000), null),
            DadosDaConexao.Nenhuma, default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.EmpresaId == amb.Cenario.Id && c.Nome == "Nome Válido");

        Assert.Equal(ServicoCaptura.TamanhoMaximoMensagem, contato.Observacoes!.Length);
    }

    // ==================================================================== o rastro (INT-4)
    [Fact]
    public async Task O_RASTRO_DO_ANUNCIO_E_GUARDADO_COM_O_LEAD()
    {
        var (db, tx, amb) = await PrepararAsync("rastro");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Promoção de março");
        var eventoDoNavegador = Guid.NewGuid();

        await amb.Captura.ReceberAsync(chave,
            new LeadDoFormulario("Bruna Lima", "84988881111", "bruna@exemplo.com", null, null,
                new RastreioDoSite(
                    UtmSource: "instagram", UtmMedium: "cpc", UtmCampaign: "promo-de-marco",
                    Pagina: "https://cliente.com.br/promo?email=bruna@exemplo.com&x=1",
                    Referencia: "https://l.instagram.com/?u=abc",
                    Fbclid: "IwAR-do-clique", Fbp: "fb.1.123.456",
                    EventoId: eventoDoNavegador)),
            new DadosDaConexao("https://cliente.com.br", "203.0.113.7", "Mozilla/5.0 (iPhone)"),
            default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.EmpresaId == amb.Cenario.Id && c.Nome == "Bruna Lima");

        var rastro = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.ContatoId == contato.Id);

        Assert.Equal(FonteRastreio.FormularioSite, rastro.Fonte);
        Assert.Equal("instagram", rastro.UtmSource);
        Assert.Equal("promo-de-marco", rastro.UtmCampaign);
        Assert.Equal(eventoDoNavegador, rastro.EventoId);

        // ⚠️ A QUERY STRING NÃO ENTROU, e é o e-mail no meio dela que explica por quê: a URL é do
        // site do cliente e nós não auditamos o que ele põe lá.
        Assert.Equal("https://cliente.com.br/promo", rastro.Pagina);
        Assert.Equal("https://l.instagram.com/", rastro.Referencia);

        // O que a Meta usa para casar a pessoa — da CONEXÃO, não do corpo.
        Assert.Equal("203.0.113.7", rastro.Ip);
        Assert.Equal("Mozilla/5.0 (iPhone)", rastro.UserAgent);

        var ids = RegrasRastreio.Ler(rastro.Identificadores);
        Assert.Equal("IwAR-do-clique", ids[RegrasRastreio.ChaveFbclid]);
        Assert.Equal("fb.1.123.456", ids[RegrasRastreio.ChaveFbp]);
    }

    [Fact]
    public async Task O_PRIMEIRO_RASTRO_GANHA__A_SEGUNDA_VISITA_NAO_SOBRESCREVE()
    {
        // ⚠️ É A REGRA QUE SUSTENTA A ATRIBUIÇÃO. O `fbc` do clique original é o que, três dias
        // depois, diz à Meta qual anúncio trouxe a venda. Trocá-lo pelo de uma visita posterior
        // apagaria justamente a resposta que o bloco existe para dar.
        var (db, tx, amb) = await PrepararAsync("primeiro-ganha");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Orçamento");
        var lead = new LeadDoFormulario("Caio Souza", "84988882222", null, null, null,
            new RastreioDoSite(UtmCampaign: "campanha-de-marco", Fbclid: "clique-original"));

        Assert.Equal(ResultadoCaptura.ContatoCriado,
            await amb.Captura.ReceberAsync(chave, lead, DadosDaConexao.Nenhuma, default));

        // A MESMA pessoa volta semanas depois, por outra campanha.
        var devolta = new LeadDoFormulario("Caio Souza", "84988882222", null, null, null,
            new RastreioDoSite(UtmCampaign: "campanha-de-abril", Fbclid: "clique-novo"));

        Assert.Equal(ResultadoCaptura.LembreteParaContatoExistente,
            await amb.Captura.ReceberAsync(chave, devolta, DadosDaConexao.Nenhuma, default));

        db.ChangeTracker.Clear();
        var rastro = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.EmpresaId == amb.Cenario.Id);

        Assert.Equal("campanha-de-marco", rastro.UtmCampaign);
        Assert.Equal("clique-original", RegrasRastreio.Ler(rastro.Identificadores)[RegrasRastreio.ChaveFbclid]);

        // ⚠️ E SEM ERRO NO LOG — é isto que faz o `ON CONFLICT DO NOTHING` ser load-bearing.
        // Sem ele a segunda gravação estoura, o `catch` engole, e o resultado acima fica idêntico:
        // a sabotagem não derrubava teste nenhum. Só que não é idêntico — toda pessoa que preenche
        // duas vezes viraria um erro no log, e log cheio de alarme falso é log que ninguém lê no
        // dia do alarme verdadeiro.
        Assert.Empty(amb.Log.Erros);
    }

    [Fact]
    public async Task QUEM_CHEGOU_PELO_WHATSAPP_GANHA_RASTRO_QUANDO_PREENCHE_O_FORMULARIO()
    {
        // O caso que mais paga, e o oposto do óbvio: o contato do cenário veio pelo WhatsApp e não
        // tem rastro nenhum. Se agora clicou num anúncio e preencheu o formulário, passamos a saber
        // de onde ele veio — não havia nada para preservar.
        var (db, tx, amb) = await PrepararAsync("whatsapp-depois");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Orçamento");
        var jaExistia = amb.Cenario.Contato;

        var resultado = await amb.Captura.ReceberAsync(chave,
            new LeadDoFormulario(jaExistia.Nome, jaExistia.Telefone, null, null, null,
                new RastreioDoSite(UtmCampaign: "remarketing", Fbclid: "clique-de-agora")),
            DadosDaConexao.Nenhuma, default);

        Assert.Equal(ResultadoCaptura.LembreteParaContatoExistente, resultado);

        db.ChangeTracker.Clear();
        var rastro = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.ContatoId == jaExistia.Id);

        Assert.Equal("remarketing", rastro.UtmCampaign);
    }

    [Fact]
    public async Task FORMULARIO_ANTIGO_SEM_RASTREIO_NAO_CRIA_LINHA_VAZIA()
    {
        // O código que o cliente colou no site ano passado não manda nada disso. O lead entra
        // igual, e NÃO nasce uma linha de rastro em branco — uma tabela cheia delas faria a tela do
        // contato mostrar "De onde veio" vazio para todo mundo.
        var (db, tx, amb) = await PrepararAsync("sem-rastreio");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Formulário velho");

        var resultado = await amb.Captura.ReceberAsync(chave,
            new LeadDoFormulario("Denise Rocha", "84988883333", null, null, null),
            DadosDaConexao.Nenhuma, default);

        Assert.Equal(ResultadoCaptura.ContatoCriado, resultado);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.EmpresaId == amb.Cenario.Id).ToListAsync());
    }

    [Fact]
    public async Task SO_O_IP_JA_VIRA_RASTRO__PORQUE_E_ELE_QUE_A_META_USA()
    {
        // Sem `utm_*` e sem identificador de clique, mas com IP e User-Agent: a linha vale, porque
        // o casamento por IP e navegador é o que faz isto funcionar para quem não tem pixel.
        var (db, tx, amb) = await PrepararAsync("so-ip");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Orçamento");

        await amb.Captura.ReceberAsync(chave,
            new LeadDoFormulario("Elias Mota", "84988884444", null, null, null),
            new DadosDaConexao(null, "198.51.100.3", "Mozilla/5.0"), default);

        db.ChangeTracker.Clear();
        var rastro = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.EmpresaId == amb.Cenario.Id);

        Assert.Equal("198.51.100.3", rastro.Ip);
        Assert.Null(rastro.UtmCampaign);
        Assert.Equal("{}", rastro.Identificadores);
    }

    [Fact]
    public async Task O_RASTRO_NAO_DERRUBA_A_CAPTURA_NEM_COM_TEXTO_GIGANTE()
    {
        // Sem a truncagem, o INSERT estoura "value too long" e o visitante do site do cliente vê
        // 500 — perdendo o lead por causa de um campo de rastreio. Perder a atribuição é ruim;
        // perder o lead é inaceitável.
        var (db, tx, amb) = await PrepararAsync("gigante");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Orçamento");

        var resultado = await amb.Captura.ReceberAsync(chave,
            new LeadDoFormulario("Fábio Nunes", "84988885555", null, null, null,
                new RastreioDoSite(
                    UtmCampaign: new string('x', 5000),
                    Pagina: "https://cliente.com.br/" + new string('y', 5000),
                    Fbclid: new string('z', 5000))),
            new DadosDaConexao(null, new string('9', 200), new string('u', 5000)), default);

        Assert.Equal(ResultadoCaptura.ContatoCriado, resultado);

        db.ChangeTracker.Clear();
        var rastro = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.EmpresaId == amb.Cenario.Id);

        Assert.Equal(RastreioLead.TetoUtm, rastro.UtmCampaign!.Length);
        Assert.Equal(RastreioLead.TetoUrl, rastro.Pagina!.Length);
        Assert.Equal(RastreioLead.TetoIp, rastro.Ip!.Length);
        Assert.Equal(RastreioLead.TetoUserAgent, rastro.UserAgent!.Length);
    }

    [Fact]
    public async Task SE_O_RASTRO_FALHAR_O_LEAD_AINDA_ENTRA()
    {
        // ⚠️ A GARANTIA QUE SÓ UM TESTE DESTES PROVA. Perder a atribuição é ruim; perder o lead é
        // inaceitável — quem preencheu o formulário é o dinheiro do cliente entrando pela porta.
        //
        // A tabela é derrubada DENTRO da transação do teste, então ela volta no rollback. É o
        // único jeito de fazer o INSERT do rastro falhar de verdade pelo caminho público, e o que
        // se prova é o `catch`: sem ele, o visitante do site veria 500.
        var (db, tx, amb) = await PrepararAsync("rastro-quebrado");
        using var _ = db; using var __ = tx;

        var chave = await FormularioAsync(db, amb, "Orçamento");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE rastreios_lead");

        var resultado = await amb.Captura.ReceberAsync(chave,
            new LeadDoFormulario("Gisele Prado", "84988887000", null, null, null,
                new RastreioDoSite(UtmCampaign: "promo")),
            DadosDaConexao.Nenhuma, default);

        Assert.Equal(ResultadoCaptura.ContatoCriado, resultado);

        db.ChangeTracker.Clear();
        Assert.True(await db.Contatos.IgnoreQueryFilters()
            .AnyAsync(c => c.EmpresaId == amb.Cenario.Id && c.Nome == "Gisele Prado"));
    }

    [Fact]
    public async Task O_RASTRO_DE_UMA_EMPRESA_NAO_APARECE_PARA_A_OUTRA()
    {
        // Tenant zero: a captação roda sem sessão, e o `empresa_id` do rastro vem da CHAVE do
        // formulário. Se ele saísse do contexto (que é 0 aqui), a linha nasceria órfã ou na
        // empresa errada.
        var (db, tx, amb) = await PrepararAsync("iso-rastro");
        using var _ = db; using var __ = tx;

        var outra = await Semeador.TenantAsync(db, "captura-iso-rastro-b");
        var chaveDeB = await FormularioAsync(db, amb, "Form da B", empresaId: outra.Id);

        await amb.Captura.ReceberAsync(chaveDeB,
            new LeadDoFormulario("Lead da B", "84988886666", null, null, null,
                new RastreioDoSite(UtmCampaign: "campanha-da-b")),
            DadosDaConexao.Nenhuma, default);

        db.ChangeTracker.Clear();

        // Olhando como a empresa A (o contexto do ambiente), o rastro da B não existe.
        Assert.Empty(await db.RastreiosLead.AsNoTracking().ToListAsync());

        var daB = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.UtmCampaign == "campanha-da-b");
        Assert.Equal(outra.Id, daB.EmpresaId);
    }

    // ==================================================================== rate limit
    [Fact]
    public void O_ENDPOINT_PUBLICO_TEM_RATE_LIMIT_DE_10_POR_MINUTO()
    {
        // Não dá para exercitar o limiter do ASP.NET sem subir o host; o que se fixa aqui é o
        // contrato: a rota é pública, tem política PRÓPRIA (não a geral, que só cobre requisição
        // autenticada) e o teto é 10 — a 11ª no minuto é barrada pelo middleware.
        var metodo = typeof(CapturaController).GetMethod(nameof(CapturaController.Receber))!;

        Assert.NotEmpty(typeof(CapturaController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));

        var limite = metodo.GetCustomAttributes(typeof(EnableRateLimitingAttribute), true)
            .Cast<EnableRateLimitingAttribute>().Single();

        Assert.Equal(RateLimitingConfig.PolCaptura, limite.PolicyName);
        Assert.Equal(10, new OpcoesRateLimit().CapturaPorMinuto);
    }

    [Fact]
    public void Configurar_formulario_e_so_do_DONO()
    {
        // A chave gerada ali abre um endpoint de escrita na internet.
        // Quem ENTRA, e não como o atributo escreve — ver `PapeisDaRota`.
        Assert.Equal("dono", PapeisDaRota.DaClasse(typeof(FormulariosController)));
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto,
        IServicoCaptura Captura, IServicoFormularios Formularios, NotificadorFalso Painel,
        LoggerQueGuarda<ServicoCaptura> Log);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)>
        PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"captura-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var painel = new NotificadorFalso();

        // Log guardado, e não `NullLogger`: ver `LoggerQueGuarda` — é o que faz "repetir o
        // formulário é fluxo normal, não erro" ser uma afirmação testável.
        var log = new LoggerQueGuarda<ServicoCaptura>();

        return (db, tx, new Ambiente(
            cenario, ctx,
            new ServicoCaptura(db, painel, PublicadorDeTeste.Novo(db, relogio), PublicadorConversoesDeTeste.Novo(db, relogio), relogio, log),
            new ServicoFormularios(db, ctx, relogio),
            painel, log));
    }

    /// <summary>Cria o formulário direto no banco e devolve a chave. `empresaId` explícito para os
    /// testes de isolamento, que precisam de um formulário de OUTRO tenant.</summary>
    private static async Task<string> FormularioAsync(
        NexoraDbContext db, Ambiente amb, string nome, long? empresaId = null, string? dominio = null)
    {
        var formulario = new FormularioCaptura
        {
            EmpresaId = empresaId ?? amb.Cenario.Id,
            Nome = nome,
            Chave = ServicoCaptura.GerarChave(),
            DominioPermitido = dominio,
            Ativo = true
        };
        db.FormulariosCaptura.Add(formulario);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return formulario.Chave;
    }
}
