using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== LPA-1 · QUEM PAROU DE SER TRABALHADO =====================
///
/// O relógio destes testes está em 06/08/2026. "Velho" aqui é 01/05/2026 (97 dias) e "recente" é
/// 05/08/2026 (ontem) — os dois longe da borda de 30 dias, para o teste não medir a aritmética da
/// data em vez da regra.
///
/// ⚠️ O TESTE QUE MAIS IMPORTA É O DO CONTATO SEM CONVERSA. Ele é o lead frio mais comum — entrou
/// por formulário ou importação e ninguém chamou —, e é o único que a forma óbvia da consulta
/// (`cv.ultima_mensagem_em` sozinho, num LEFT JOIN) deixaria de fora para sempre.
/// ============================================================================================</summary>
[Collection("banco")]
public class LeadsParadosDbTests(BancoTeste banco)
{
    /// <summary>97 dias antes do relógio: parado em qualquer janela.</summary>
    private static readonly DateTime Velho = new(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Ontem: não está parado em janela nenhuma.</summary>
    private static readonly DateTime Recente = new(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>O relógio está em 06/08/2026, então estas são as bordas da validação de data.</summary>
    private static readonly DateOnly Amanha = new(2026, 8, 7);

    private static readonly DateOnly Ontem = new(2026, 8, 5);

    /// <summary>Quando a etiqueta foi colada nos testes da metrica. `IEntidadeCriada` grava
    /// `criado_em` com o relogio REAL, nao com o `relogio` falso do teste — por isso os testes que
    /// mexem na data usam `ExecuteUpdate`, e esta constante e o ponto de referencia deles.</summary>
    private static readonly DateTime MarcaEm = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A janela padrao dos testes da metrica: larga o bastante para pegar o `criado_em`
    /// real que o EF grava (hoje de verdade) e as datas forjadas de julho.</summary>
    private static readonly (DateOnly De, DateOnly Ate) Janela =
        (new DateOnly(2026, 1, 1), new DateOnly(2030, 12, 31));

    private static FiltroReativacao Rea(long etiquetaId) =>
        new(etiquetaId, Janela.De, Janela.Ate, null);

    private static FiltroLeadsParados Filtro(
        int dias = 30, long? responsavel = null, AbaDeLeads aba = AbaDeLeads.Parados) =>
        new(dias, responsavel, 1, 50, Aba: aba);

    // ==================================================================== a regra de "parado"

    [Fact]
    public async Task CONVERSA_VELHA_ENTRA_E_CONVERSA_DE_ONTEM_FICA_FORA()
    {
        var (db, tx, amb) = await PrepararAsync("datas");
        using var _ = db; using var __ = tx;

        var frio = await LeadAsync(db, amb, "frio", comConversaEm: Velho);
        await LeadAsync(db, amb, "quente", comConversaEm: Recente);

        var pagina = await Servico(amb).ListarAsync(Filtro(), default);

        var linha = Assert.Single(pagina.Itens);
        Assert.Equal(frio, linha.ContatoId);
        Assert.Equal(1, pagina.Total);
    }

    /// <summary>===================== O CONTATO QUE NUNCA CONVERSOU =====================
    ///
    /// ⚠️ ESTE É O TESTE QUE JUSTIFICA O `UNION`. A consulta óbvia seria um LEFT JOIN com
    /// `COALESCE(cv.ultima_mensagem_em, c.criado_em)`, e ela acerta este caso — mas foi MEDIDA
    /// contra o banco e descarta o índice, deixando a data como `Filter` depois de juntar todo
    /// contato com toda conversa da empresa.
    ///
    /// Em dois ramos o índice volta, e este teste é o que impede o segundo ramo de ser apagado por
    /// quem achar que o primeiro basta.
    /// ==============================================================</summary>
    [Fact]
    public async Task CONTATO_QUE_NUNCA_CONVERSOU_ENTRA_PELA_DATA_DE_CRIACAO()
    {
        var (db, tx, amb) = await PrepararAsync("semconversa");
        using var _ = db; using var __ = tx;

        var nunca = await LeadAsync(
            db, amb, "nunca", criadoEm: Velho, comConversaEm: null, comNegocio: false);
        await LeadAsync(
            db, amb, "novo", criadoEm: Recente, comConversaEm: null, comNegocio: false);

        var pagina = await Servico(amb).ListarAsync(Filtro(), default);

        var linha = Assert.Single(pagina.Itens);
        Assert.Equal(nunca, linha.ContatoId);

        // Sem negócio aberto, a tela mostra travessão — e o dado tem de dizer isso com nulo, nunca
        // com texto vazio, que a tela imprimiria como espaço em branco.
        Assert.Null(linha.NegociacaoId);
        Assert.Null(linha.PipelineNome);
        Assert.Null(linha.EtapaNome);
    }

    /// <summary>A janela é lista fechada, e o corte respeita o dia.</summary>
    [Theory]
    [InlineData(15, 1)]
    [InlineData(90, 1)]
    public async Task A_JANELA_MUDA_O_CORTE(int dias, int esperado)
    {
        var (db, tx, amb) = await PrepararAsync($"janela{dias}");
        using var _ = db; using var __ = tx;

        await LeadAsync(db, amb, "frio", comConversaEm: Velho);

        Assert.Equal(esperado, (await Servico(amb).ListarAsync(Filtro(dias), default)).Total);
    }

    [Fact]
    public async Task JANELA_FORA_DA_LISTA_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("janelaruim");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).ListarAsync(Filtro(45), default));
    }

    // ==================================================================== os filtros

    /// <summary>===================== CADA FILTRO RECORTA, E SOZINHO =====================
    /// Um `[Theory]` por filtro, cada um com DUAS linhas: a que casa e a que nao casa. Um teste
    /// que so afirmasse "a que casa aparece" passaria num filtro inerte — o `($6 IS NULL OR ...)`
    /// cravado em NULL deixa tudo passar e parece funcionar.
    /// ==============================================================</summary>
    [Fact]
    public async Task O_FILTRO_DE_FUNIL_E_DE_ETAPA_RECORTA()
    {
        var (db, tx, amb) = await PrepararAsync("funil");
        using var _ = db; using var __ = tx;

        var meu = await LeadAsync(db, amb, "dovendas", comConversaEm: Velho);
        var outro = await LeadAsync(db, amb, "dooutro", comConversaEm: Velho);
        await MudarFunilAsync(db, amb, outro);

        var pipelineId = amb.Cenario.Etapas[0].PipelineId;
        var etapaId = amb.Cenario.Etapas[0].Id;

        var porFunil = await Servico(amb).ListarAsync(
            Filtro() with { PipelineId = pipelineId }, default);
        Assert.Equal(meu, Assert.Single(porFunil.Itens).ContatoId);

        var porEtapa = await Servico(amb).ListarAsync(
            Filtro() with { EtapaId = etapaId }, default);
        Assert.Equal(meu, Assert.Single(porEtapa.Itens).ContatoId);
    }

    [Fact]
    public async Task O_FILTRO_DE_ORIGEM_RECORTA()
    {
        var (db, tx, amb) = await PrepararAsync("origem");
        using var _ = db; using var __ = tx;

        var insta = await LeadAsync(db, amb, "insta", comConversaEm: Velho, origem: OrigemLead.Instagram);
        await LeadAsync(db, amb, "google", comConversaEm: Velho, origem: OrigemLead.Google);

        var pagina = await Servico(amb).ListarAsync(Filtro() with { Origem = "instagram" }, default);

        var linha = Assert.Single(pagina.Itens);
        Assert.Equal(insta, linha.ContatoId);
        Assert.Equal("instagram", linha.Origem);
    }

    /// <summary>⚠️ A ETIQUETA E A DA NEGOCIACAO, nao a do contato. O projeto tem as duas, e este
    /// teste e quem impede alguem de trocar `negociacoes_etiquetas` por `contatos_etiquetas`
    /// achando que da no mesmo: dava, ate a acao em lote da entrega 4 escrever na primeira.</summary>
    [Fact]
    public async Task O_FILTRO_DE_ETIQUETA_OLHA_A_DA_NEGOCIACAO()
    {
        var (db, tx, amb) = await PrepararAsync("etiqueta");
        using var _ = db; using var __ = tx;

        var marcado = await LeadAsync(db, amb, "marcado", comConversaEm: Velho);
        var limpo = await LeadAsync(db, amb, "limpo", comConversaEm: Velho);

        var etiquetaId = await EtiquetaAsync(db, amb, "reativacao");
        await MarcarNegociacaoAsync(db, amb, await NegociacaoDeAsync(db, marcado), etiquetaId);
        // O OUTRO leva a mesma etiqueta no CONTATO: se a consulta olhar a tabela errada, ele entra.
        await MarcarContatoAsync(db, amb, limpo, etiquetaId);

        var pagina = await Servico(amb).ListarAsync(
            Filtro() with { EtiquetaId = etiquetaId }, default);

        Assert.Equal(marcado, Assert.Single(pagina.Itens).ContatoId);
    }

    [Fact]
    public async Task O_FILTRO_DE_VALOR_RECORTA_PELAS_DUAS_PONTAS()
    {
        var (db, tx, amb) = await PrepararAsync("valor");
        using var _ = db; using var __ = tx;

        var barato = await LeadAsync(db, amb, "barato", comConversaEm: Velho, valor: 100m);
        var caro = await LeadAsync(db, amb, "caro", comConversaEm: Velho, valor: 5000m);

        var deMil = await Servico(amb).ListarAsync(Filtro() with { ValorMin = 1000m }, default);
        Assert.Equal(caro, Assert.Single(deMil.Itens).ContatoId);

        var ateMil = await Servico(amb).ListarAsync(Filtro() with { ValorMax = 1000m }, default);
        Assert.Equal(barato, Assert.Single(ateMil.Itens).ContatoId);
    }

    /// <summary>⚠️ FILTRO DE NEGOCIACAO ESCONDE QUEM NAO TEM NEGOCIO, e e o certo: o lead que
    /// ninguem abriu nao esta em funil nenhum. Este teste existe para que a consequencia seja uma
    /// DECISAO escrita, e nao uma surpresa descoberta pelo cliente.</summary>
    [Fact]
    public async Task FILTRO_DE_NEGOCIACAO_ESCONDE_QUEM_NAO_TEM_NEGOCIO()
    {
        var (db, tx, amb) = await PrepararAsync("semnegocio");
        using var _ = db; using var __ = tx;

        await LeadAsync(db, amb, "sonho", comConversaEm: Velho, comNegocio: false);

        // Sem filtro ele aparece.
        Assert.Single((await Servico(amb).ListarAsync(Filtro(), default)).Itens);

        // Com filtro de funil, nao.
        var comFunil = await Servico(amb).ListarAsync(
            Filtro() with { PipelineId = amb.Cenario.Etapas[0].PipelineId }, default);
        Assert.Empty(comFunil.Itens);
    }

    // ==================================================================== quem sai

    /// <summary>⚠️ QUEM COMPROU NÃO É LEAD FRIO. O contato com negociação ganha ou concluída e nada
    /// aberto é cliente — pô-lo nesta lista mandaria o dono "recuperar" quem já comprou.</summary>
    [Theory]
    [InlineData(StatusNegociacao.Ganha)]
    [InlineData(StatusNegociacao.Concluida)]
    [InlineData(StatusNegociacao.Perdida)]
    public async Task NEGOCIO_FECHADO_TIRA_O_CONTATO_DA_LISTA(StatusNegociacao status)
    {
        var (db, tx, amb) = await PrepararAsync($"fechado{status}");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "fechado", comConversaEm: Velho);
        await MudarStatusAsync(db, id, status);

        Assert.Empty((await Servico(amb).ListarAsync(Filtro(), default)).Itens);
    }

    /// <summary>O par do teste acima. Cancelar desfaz o negócio: o contato volta a ser lead, e
    /// voltar para a lista é o comportamento certo.</summary>
    [Fact]
    public async Task NEGOCIO_CANCELADO_DEVOLVE_O_CONTATO_A_LISTA()
    {
        var (db, tx, amb) = await PrepararAsync("cancelado");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "cancelado", comConversaEm: Velho);
        await MudarStatusAsync(db, id, StatusNegociacao.Cancelada);

        var linha = Assert.Single((await Servico(amb).ListarAsync(Filtro(), default)).Itens);
        Assert.Equal(id, linha.ContatoId);
        Assert.Null(linha.NegociacaoId);
    }

    /// <summary>⚠️ O CLIENTE RECORRENTE COM PÓS-VENDA PARADO APARECE, e tem de aparecer: aquele
    /// negócio está parado de verdade. A regra não é "o contato nunca comprou" — é "há um negócio
    /// aberto sem avanço".</summary>
    [Fact]
    public async Task QUEM_JA_COMPROU_MAS_TEM_OUTRO_NEGOCIO_ABERTO_APARECE()
    {
        var (db, tx, amb) = await PrepararAsync("recorrente");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "recorrente", comConversaEm: Velho);
        await MudarStatusAsync(db, id, StatusNegociacao.Concluida);
        await NegocioAbertoAsync(db, amb, id);

        var linha = Assert.Single((await Servico(amb).ListarAsync(Filtro(), default)).Itens);
        Assert.Equal(id, linha.ContatoId);
        Assert.NotNull(linha.NegociacaoId);
    }

    [Fact]
    public async Task CONTATO_ANONIMIZADO_FICA_FORA()
    {
        var (db, tx, amb) = await PrepararAsync("lgpd");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "apagado", comConversaEm: Velho);
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AnonimizadoEm, Velho));
        db.ChangeTracker.Clear();

        Assert.Empty((await Servico(amb).ListarAsync(Filtro(), default)).Itens);
    }

    // ==================================================================== o recorte e o tenant

    /// <summary>⚠️ O RECORTE É POR `negociacoes.responsavel_id`, nunca pelo do contato. A
    /// `LiberacaoDeCiclo` zera a coluna do contato ao concluir a venda — foi esse detalhe que
    /// tornou instável a conversão do relatório antigo.</summary>
    [Fact]
    public async Task QUEM_NAO_VE_OS_NUMEROS_DA_EQUIPE_RECEBE_SO_OS_SEUS()
    {
        var (db, tx, amb) = await PrepararAsync("recorte");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb, "ana");
        var bruno = await VendedorAsync(db, amb, "bruno");

        await LeadAsync(db, amb, "daana", comConversaEm: Velho, responsavelId: ana.Id);
        await LeadAsync(db, amb, "dobruno", comConversaEm: Velho, responsavelId: bruno.Id);

        Assert.Equal(2, (await Servico(amb).ListarAsync(Filtro(), default)).Total);

        amb.Contexto.UsuarioId = ana.Id;
        amb.Contexto.Papel = "vendedor";

        var daAna = await Servico(amb).ListarAsync(Filtro(), default);
        var so = Assert.Single(daAna.Itens);
        Assert.Equal(ana.Id, so.ResponsavelId);
    }

    /// <summary>===================== A ATRIBUICAO E DA NEGOCIACAO =====================
    ///
    /// ⚠️ ESTE TESTE NASCEU DE UMA SABOTAGEM QUE NAO DERRUBOU NADA. Troquei
    /// `n.responsavel_id` por `c.responsavel_id` na consulta e a suite inteira ficou verde — a
    /// fixture gravava o mesmo responsavel nas DUAS colunas, entao nenhum teste conseguia
    /// distingui-las.
    ///
    /// Aqui as duas DIVERGEM de proposito, reproduzindo o que a `LiberacaoDeCiclo` faz de
    /// verdade: ela ZERA `contatos.responsavel_id` quando a venda e concluida, e nao toca no da
    /// negociacao. Contar pelo contato faria o lead sumir da lista do vendedor que o trabalha.
    /// ==============================================================</summary>
    [Fact]
    public async Task A_ATRIBUICAO_SEGUE_A_NEGOCIACAO_E_NAO_O_CONTATO()
    {
        var (db, tx, amb) = await PrepararAsync("atribui");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb, "ana");

        var id = await LeadAsync(db, amb, "repassado", comConversaEm: Velho, responsavelId: ana.Id);

        // O que a LiberacaoDeCiclo faz: o contato perde o dono, a negociacao mantem o dela.
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, (long?)null));
        db.ChangeTracker.Clear();

        var daAna = await Servico(amb).ListarAsync(Filtro(responsavel: ana.Id), default);

        var linha = Assert.Single(daAna.Itens);
        Assert.Equal(id, linha.ContatoId);
        Assert.Equal(ana.Id, linha.ResponsavelId);
    }

    /// <summary>===================== O LEAD SEM NEGOCIO TEM DONO =====================
    ///
    /// ⚠️ O CASO DA REVISAO: o lead que chegou pelo WhatsApp e ninguem abriu card — o lead frio
    /// mais comum — tem dono so no CONTATO. A lista filtrava por `n.responsavel_id`, `n` era nulo,
    /// e ele sumia da lista PROPRIA do vendedor que o atendeu. E a coluna dizia "sem responsavel".
    ///
    /// Com negocio aberto continua valendo o do negocio — e o teste de cima, que diverge as duas
    /// colunas de proposito.
    /// ============================================================</summary>
    [Fact]
    public async Task O_LEAD_SEM_NEGOCIO_APARECE_NA_LISTA_DO_DONO_DO_CONTATO()
    {
        var (db, tx, amb) = await PrepararAsync("sem-negocio-dono");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb, "ana");
        var bruno = await VendedorAsync(db, amb, "bruno");

        var id = await LeadAsync(db, amb, "frio", comConversaEm: Velho, responsavelId: bruno.Id,
            comNegocio: false);

        // O vendedor, que so ve o seu.
        amb.Contexto.UsuarioId = bruno.Id;
        amb.Contexto.Papel = "vendedor";

        var doBruno = Assert.Single((await Servico(amb).ListarAsync(Filtro(), default)).Itens);
        Assert.Equal(id, doBruno.ContatoId);
        Assert.Null(doBruno.NegociacaoId);
        Assert.Equal(bruno.Id, doBruno.ResponsavelId);
        Assert.Equal(bruno.Nome, doBruno.ResponsavelNome);

        // E a colega nao o recebe.
        amb.Contexto.UsuarioId = ana.Id;
        Assert.Empty((await Servico(amb).ListarAsync(Filtro(), default)).Itens);
    }

    /// <summary>⚠️ SQL cru passa por fora do filtro global do EF. O `empresa_id = $2` é a única
    /// coisa que separa as empresas aqui, e não há teste mecânico que o exija — então é exigido
    /// à mão.</summary>
    [Fact]
    public async Task A_EMPRESA_VIZINHA_NAO_ENTRA()
    {
        var (db, tx, amb) = await PrepararAsync("tenant");
        using var _ = db; using var __ = tx;

        await LeadAsync(db, amb, "meu", comConversaEm: Velho);

        var vizinha = await Semeador.TenantAsync(db, "lpa-vizinha");
        await ZerarAsync(db, vizinha.Id);
        var outro = new ContextoMutavel
        {
            EmpresaId = vizinha.Id, UsuarioId = vizinha.Dono.Id, Papel = "dono"
        };
        var ambVizinha = amb with { Cenario = vizinha, Contexto = outro };

        for (var i = 0; i < 5; i++)
            await LeadAsync(db, ambVizinha, $"viz{i}", comConversaEm: Velho);

        Assert.Equal(1, (await Servico(amb).ListarAsync(Filtro(), default)).Total);
    }

    // ==================================================================== a paginação

    /// <summary>O total vem da MESMA consulta, por `COUNT(*) OVER ()`. Uma segunda consulta de
    /// contagem pode discordar da primeira quando alguém escreve no meio das duas.</summary>
    [Fact]
    public async Task O_TOTAL_CONTA_TUDO_E_A_PAGINA_TRAZ_SO_A_FATIA()
    {
        var (db, tx, amb) = await PrepararAsync("pagina");
        using var _ = db; using var __ = tx;

        for (var i = 0; i < 7; i++)
            await LeadAsync(db, amb, $"p{i}", comConversaEm: Velho.AddHours(i));

        var primeira = await Servico(amb).ListarAsync(new FiltroLeadsParados(30, null, 1, 3), default);

        Assert.Equal(7, primeira.Total);
        Assert.Equal(3, primeira.Itens.Count);

        var terceira = await Servico(amb).ListarAsync(new FiltroLeadsParados(30, null, 3, 3), default);

        Assert.Equal(7, terceira.Total);
        Assert.Single(terceira.Itens);
    }

    /// <summary>O teto é do SERVIÇO, não do campo da tela: quem manda pela API também passa por
    /// ele. Pedir 500 devolve 50.</summary>
    [Fact]
    public async Task O_TAMANHO_DE_PAGINA_TEM_TETO()
    {
        var (db, tx, amb) = await PrepararAsync("teto");
        using var _ = db; using var __ = tx;

        for (var i = 0; i < 3; i++)
            await LeadAsync(db, amb, $"t{i}", comConversaEm: Velho.AddHours(i));

        var pagina = await Servico(amb).ListarAsync(new FiltroLeadsParados(30, null, 1, 500), default);

        Assert.Equal(3, pagina.Itens.Count);
        Assert.Equal(JanelasDeParada.TamanhoMaximoPagina, 50);
    }

    [Fact]
    public async Task OS_MAIS_PARADOS_VEM_PRIMEIRO()
    {
        var (db, tx, amb) = await PrepararAsync("ordem");
        using var _ = db; using var __ = tx;

        await LeadAsync(db, amb, "meio", comConversaEm: Velho.AddDays(10));
        var maisVelho = await LeadAsync(db, amb, "velho", comConversaEm: Velho);

        var pagina = await Servico(amb).ListarAsync(Filtro(), default);

        Assert.Equal(maisVelho, pagina.Itens[0].ContatoId);
        Assert.True(pagina.Itens[0].DiasParado > pagina.Itens[1].DiasParado);
    }

    // ==================================================================== o lembrete em lote

    /// <summary>===================== A TAREFA CAI NO MEU DIA DE QUEM TRABALHA O LEAD =====================
    ///
    /// ⚠️ E NAO NO DE QUEM CLICOU. `ServicoLembretes.CriarAsync` atribui a quem cria — "quem cria
    /// assume" — e ali esta certo: o vendedor marca o proprio retorno. Aqui seria o oposto: o dono
    /// seleciona trinta leads de cinco pessoas e levaria as trinta no Meu Dia dele, enquanto os
    /// cinco vendedores nao receberiam nada.
    /// ==============================================================</summary>
    [Fact]
    public async Task O_LEMBRETE_VAI_PARA_O_RESPONSAVEL_DO_LEAD_NAO_PARA_QUEM_CLICOU()
    {
        var (db, tx, amb) = await PrepararAsync("lote-dono");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb, "ana");
        var bruno = await VendedorAsync(db, amb, "bruno");

        var daAna = await LeadAsync(db, amb, "a", comConversaEm: Velho, responsavelId: ana.Id);
        var doBruno = await LeadAsync(db, amb, "b", comConversaEm: Velho, responsavelId: bruno.Id);

        // Quem clica e o DONO.
        var r = await Servico(amb).CriarLembretesAsync(
            new LembreteEmLote([daAna, doBruno], Amanha, "Retomar", null), default);

        Assert.Equal(2, r.Criados);

        db.ChangeTracker.Clear();
        var lembretes = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ToListAsync();

        Assert.Equal(ana.Id, lembretes.Single(l => l.ContatoId == daAna).ResponsavelId);
        Assert.Equal(bruno.Id, lembretes.Single(l => l.ContatoId == doBruno).ResponsavelId);
        // E quem pediu fica registrado, sem levar a tarefa.
        Assert.All(lembretes, l => Assert.Equal(amb.Cenario.Dono.Id, l.CriadoPor));
    }

    /// <summary>Lead sem dono cai para quem pediu: a tarefa precisa aparecer na lista de ALGUEM, e
    /// lembrete sem responsavel nao aparece em Meu Dia nenhum.</summary>
    [Fact]
    public async Task LEAD_SEM_DONO_CAI_PARA_QUEM_PEDIU()
    {
        var (db, tx, amb) = await PrepararAsync("lote-semdono");
        using var _ = db; using var __ = tx;

        var orfao = await LeadAsync(db, amb, "orfao", comConversaEm: Velho);

        await Servico(amb).CriarLembretesAsync(
            new LembreteEmLote([orfao], Amanha, "Retomar", null), default);

        db.ChangeTracker.Clear();
        var l = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.ContatoId == orfao);

        Assert.Equal(amb.Cenario.Dono.Id, l.ResponsavelId);
    }

    /// <summary>⚠️ NUNCA ENVIA MENSAGEM, e e a premissa da fase inteira: o WhatsApp roda via
    /// Baileys e disparo em massa queima o numero do cliente. Nao ha nem parametro para isso — o
    /// teste existe para que acrescentar um exija passar por aqui.</summary>
    [Fact]
    public async Task O_LEMBRETE_EM_LOTE_NUNCA_ENVIA_MENSAGEM()
    {
        var (db, tx, amb) = await PrepararAsync("lote-semmsg");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "quieto", comConversaEm: Velho);

        await Servico(amb).CriarLembretesAsync(
            new LembreteEmLote([id], Amanha, "Retomar", null), default);

        db.ChangeTracker.Clear();
        var l = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.ContatoId == id);

        Assert.False(l.EnviaMensagem);
        Assert.Null(l.TextoMensagem);
        // MANUAL: `uq_lembrete_teto_diario` so cobre o automatico que envia mensagem. Marcar como
        // automatico o poria num teto que nao e dele e barraria o segundo lote do dia em silencio.
        Assert.Equal(OrigemLembrete.Manual, l.Origem);
        Assert.Empty(await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.ContatoId == id).ToListAsync());
    }

    /// <summary>⚠️ "PULADO" NAO E ERRO. Mesma regra do motor de follow-up: contato com lembrete
    /// pendente nao ganha outro, senao o vendedor recebe a mesma tarefa todo dia ate fazer. E
    /// `Pulados` e um numero SEPARADO de `Falhou` para o operador nao procurar um problema que
    /// nao existe.</summary>
    [Fact]
    public async Task QUEM_JA_TEM_LEMBRETE_PENDENTE_E_PULADO_E_ISSO_NAO_E_ERRO()
    {
        var (db, tx, amb) = await PrepararAsync("lote-pulado");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "jatem", comConversaEm: Velho);
        var outro = await LeadAsync(db, amb, "livre", comConversaEm: Velho);

        await Servico(amb).CriarLembretesAsync(
            new LembreteEmLote([id], Amanha, "Primeiro", null), default);

        var r = await Servico(amb).CriarLembretesAsync(
            new LembreteEmLote([id, outro], Amanha, "Segundo", null), default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Pulados);
        Assert.Equal(0, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.Single(await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.ContatoId == id).ToListAsync());
    }

    /// <summary>⚠️ SEM O GESTO, NADA ACONTECE — nem parcialmente. A tela de LISTAGEM nao tem
    /// guarda de proposito (ver nao e agir), entao a trava precisa ser da acao.</summary>
    [Fact]
    public async Task SEM_O_GESTO_DE_AGIR_EM_LOTE_A_ACAO_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("lote-sempermissao");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "zeca");
        var id = await LeadAsync(db, amb, "alvo", comConversaEm: Velho, responsavelId: vendedor.Id);

        amb.Contexto.UsuarioId = vendedor.Id;
        amb.Contexto.Papel = "vendedor";

        // Ele VE a propria lista...
        Assert.Single((await Servico(amb).ListarAsync(Filtro(), default)).Itens);

        // ...e nao age sobre ela em lote.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).CriarLembretesAsync(
                new LembreteEmLote([id], Amanha, "Retomar", null), default));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ToListAsync());
    }

    /// <summary>⚠️ ID DE OUTRA EMPRESA NAO ENTRA, e quem o barra e o filtro global: ele
    /// simplesmente nao volta da consulta de alvos. O cliente monta a lista de ids — nada impede
    /// de mandar um id que ele viu noutro lugar.</summary>
    [Fact]
    public async Task ID_DE_OUTRA_EMPRESA_NAO_GANHA_LEMBRETE()
    {
        var (db, tx, amb) = await PrepararAsync("lote-tenant");
        using var _ = db; using var __ = tx;

        var meu = await LeadAsync(db, amb, "meu", comConversaEm: Velho);

        var vizinha = await Semeador.TenantAsync(db, "lpa-lote-vizinha");
        await ZerarAsync(db, vizinha.Id);
        var ambVizinha = amb with
        {
            Cenario = vizinha,
            Contexto = new ContextoMutavel
            {
                EmpresaId = vizinha.Id, UsuarioId = vizinha.Dono.Id, Papel = "dono"
            }
        };
        var dela = await LeadAsync(db, ambVizinha, "dela", comConversaEm: Velho);

        var r = await Servico(amb).CriarLembretesAsync(
            new LembreteEmLote([meu, dela], Amanha, "Retomar", null), default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.ContatoId == dela).ToListAsync());
    }

    [Fact]
    public async Task DATA_NO_PASSADO_E_TITULO_VAZIO_SAO_RECUSADOS()
    {
        var (db, tx, amb) = await PrepararAsync("lote-validacao");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "alvo", comConversaEm: Velho);

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).CriarLembretesAsync(
                new LembreteEmLote([id], Ontem, "Retomar", null), default));

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).CriarLembretesAsync(
                new LembreteEmLote([id], Amanha, "   ", null), default));
    }

    /// <summary>===================== "HOJE" E O DA EMPRESA =====================
    ///
    /// ⚠️ O CASO DA REVISAO: a data era conferida em UTC. As 22h30 de Brasilia o servidor ja esta em
    /// 01h30 do dia seguinte, e o lembrete "para hoje" era recusado como "no passado" toda noite,
    /// das 21h a meia-noite.
    /// =============================================================</summary>
    [Fact]
    public async Task LEMBRETE_PARA_HOJE_A_NOITE_NAO_E_RECUSADO_COMO_PASSADO()
    {
        var (db, tx, amb) = await PrepararAsync("lote-noite");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "alvo", comConversaEm: Velho);

        // De 13h30 UTC para 01h30 UTC do dia seguinte: 22h30 de 06/08 em Brasilia.
        ((RelogioFalso)amb.Relogio).Avancar(TimeSpan.FromHours(12));
        var hojeEmBrasilia = new DateOnly(2026, 8, 6);

        var r = await Servico(amb).CriarLembretesAsync(
            new LembreteEmLote([id], hojeEmBrasilia, "Retomar", null), default);

        Assert.Equal(1, r.Criados);
    }

    // ==================================================================== a etiqueta em lote

    /// <summary>===================== ADICIONA, NAO SUBSTITUI =====================
    ///
    /// ⚠️ O DEFEITO QUE ESTE TESTE IMPEDE. `ServicoEtiquetas.AplicarNaNegociacaoAsync` recebe o
    /// CONJUNTO FINAL — mandar uma etiqueta remove as outras. Chamar aquele metodo num laco de
    /// cinquenta cards apagaria "Urgente" e "Aguardando" de todos eles, e quem quis marcar
    /// "reativacao-out" nao teria como perceber nem como desfazer.
    /// ====================================================================</summary>
    [Fact]
    public async Task A_ETIQUETA_EM_LOTE_SOMA_E_NAO_APAGA_AS_OUTRAS()
    {
        var (db, tx, amb) = await PrepararAsync("etq-soma");
        using var _ = db; using var __ = tx;

        var urgente = await EtiquetaAsync(db, amb, "urgente");
        var reativacao = await EtiquetaAsync(db, amb, "reativacao-out");

        var id = await LeadAsync(db, amb, "joana", comConversaEm: Velho);
        var negociacao = await NegociacaoDeAsync(db, id);
        await MarcarNegociacaoAsync(db, amb, negociacao, urgente);

        var r = await Servico(amb).AplicarEtiquetaAsync(
            new EtiquetaEmLote([negociacao], reativacao), default);

        Assert.Equal(1, r.Criados);

        db.ChangeTracker.Clear();
        var marcas = await db.NegociacoesEtiquetas.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao)
            .Select(x => x.EtiquetaId).ToListAsync();

        Assert.Equal([urgente, reativacao], marcas.Order());
    }

    /// <summary>⚠️ QUEM JA TEM A ETIQUETA CONSERVA O `criado_em` DO PRIMEIRO DIA, e nao e detalhe:
    /// a metrica de reativados compara `negociacoes_etiquetas.criado_em` com `negociacoes.ganha_em`.
    /// Reinserir empurraria a data para hoje e a venda de ontem passaria a parecer anterior a
    /// reativacao — a metrica cairia para zero sem ninguem mexer nela.</summary>
    [Fact]
    public async Task APLICAR_DE_NOVO_NAO_REESCREVE_A_DATA_DA_MARCA()
    {
        var (db, tx, amb) = await PrepararAsync("etq-data");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");
        var id = await LeadAsync(db, amb, "joana", comConversaEm: Velho);
        var negociacao = await NegociacaoDeAsync(db, id);

        await Servico(amb).AplicarEtiquetaAsync(new EtiquetaEmLote([negociacao], etq), default);

        db.ChangeTracker.Clear();
        var antes = await db.NegociacoesEtiquetas.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao).Select(x => x.CriadoEm).SingleAsync();

        // Volta a aplicar: pulado, e a data fica.
        var r = await Servico(amb).AplicarEtiquetaAsync(
            new EtiquetaEmLote([negociacao], etq), default);

        Assert.Equal(0, r.Criados);
        Assert.Equal(1, r.Pulados);

        db.ChangeTracker.Clear();
        var depois = await db.NegociacoesEtiquetas.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao).Select(x => x.CriadoEm).SingleAsync();

        Assert.Equal(antes, depois);
    }

    /// <summary>⚠️ A ETIQUETA VAI NO NEGOCIO MARCADO, E SO NELE. E a razao de a etiqueta ser de
    /// negociacao e nao de contato: a mesma pessoa pode ter dois negocios abertos, e marcar o
    /// contato atribuiria a reativacao a venda do OUTRO quando ela fosse ganha.</summary>
    [Fact]
    public async Task DOIS_NEGOCIOS_DA_MESMA_PESSOA_E_SO_UM_FICA_MARCADO()
    {
        var (db, tx, amb) = await PrepararAsync("etq-um-so");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");
        var id = await LeadAsync(db, amb, "ysia", comConversaEm: Velho);

        var vendas = await NegociacaoDeAsync(db, id);

        // O segundo negocio aberto, noutro funil: `uq_negociacoes_card_por_funil` permite um por
        // funil, e este e o caso do cliente recorrente.
        await NegocioAbertoAsync(db, amb, id);
        var posVenda = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.ContatoId == id && n.Id != vendas).Select(n => n.Id).SingleAsync();

        await Servico(amb).AplicarEtiquetaAsync(new EtiquetaEmLote([vendas], etq), default);

        db.ChangeTracker.Clear();
        var marcadas = await db.NegociacoesEtiquetas.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.EmpresaId == amb.Cenario.Id)
            .Select(x => x.NegociacaoId).ToListAsync();

        Assert.Equal([vendas], marcadas);
        Assert.DoesNotContain(posVenda, marcadas);
    }

    /// <summary>Negocio no teto de oito etiquetas e PULADO, e o lote segue. Recusar a chamada
    /// inteira por causa de um card cheio faria o operador perder os outros quarenta e nove, sem
    /// saber qual era o problematico.</summary>
    [Fact]
    public async Task CARD_NO_TETO_DE_OITO_E_PULADO_E_O_LOTE_SEGUE()
    {
        var (db, tx, amb) = await PrepararAsync("etq-teto");
        using var _ = db; using var __ = tx;

        var cheio = await LeadAsync(db, amb, "cheio", comConversaEm: Velho);
        var negocioCheio = await NegociacaoDeAsync(db, cheio);

        for (var i = 0; i < ServicoEtiquetas.MaximoPorNegociacao; i++)
            await MarcarNegociacaoAsync(
                db, amb, negocioCheio, await EtiquetaAsync(db, amb, $"enche-{i}"));

        var livre = await LeadAsync(db, amb, "livre", comConversaEm: Velho);
        var negocioLivre = await NegociacaoDeAsync(db, livre);

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");

        var r = await Servico(amb).AplicarEtiquetaAsync(
            new EtiquetaEmLote([negocioCheio, negocioLivre], etq), default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Pulados);
        Assert.Equal(0, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.NegociacoesEtiquetas.IgnoreQueryFilters()
            .Where(x => x.NegociacaoId == negocioCheio && x.EtiquetaId == etq).ToListAsync());
    }

    /// <summary>⚠️ SO NEGOCIO ABERTO. Marcar um ganho diria que ele foi reativado hoje, e a metrica
    /// compararia `criado_em` com um `ganha_em` que e ANTERIOR — creditando a reativacao por uma
    /// venda que aconteceu antes dela.</summary>
    [Fact]
    public async Task NEGOCIO_QUE_NAO_ESTA_ABERTO_NAO_GANHA_ETIQUETA()
    {
        var (db, tx, amb) = await PrepararAsync("etq-fechado");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");
        var id = await LeadAsync(db, amb, "ganhou", comConversaEm: Velho);
        var negociacao = await NegociacaoDeAsync(db, id);
        await MudarStatusAsync(db, id, StatusNegociacao.Ganha);

        var r = await Servico(amb).AplicarEtiquetaAsync(
            new EtiquetaEmLote([negociacao], etq), default);

        Assert.Equal(0, r.Criados);
        Assert.Equal(1, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.NegociacoesEtiquetas.IgnoreQueryFilters()
            .Where(x => x.NegociacaoId == negociacao).ToListAsync());
    }

    [Fact]
    public async Task SEM_O_GESTO_A_ETIQUETA_EM_LOTE_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("etq-sempermissao");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");
        var vendedor = await VendedorAsync(db, amb, "zeca");
        var id = await LeadAsync(db, amb, "alvo", comConversaEm: Velho, responsavelId: vendedor.Id);
        var negociacao = await NegociacaoDeAsync(db, id);

        amb.Contexto.UsuarioId = vendedor.Id;
        amb.Contexto.Papel = "vendedor";

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).AplicarEtiquetaAsync(
                new EtiquetaEmLote([negociacao], etq), default));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.NegociacoesEtiquetas.IgnoreQueryFilters()
            .Where(x => x.EmpresaId == amb.Cenario.Id).ToListAsync());
    }

    /// <summary>⚠️ ETIQUETA DE OUTRA EMPRESA NAO COLA, e quem barra e o filtro global: ela nao volta
    /// da consulta de existencia, e o pedido inteiro e recusado. O cliente manda o id — nada impede
    /// de mandar um que ele viu noutro lugar.</summary>
    [Fact]
    public async Task ETIQUETA_DE_OUTRA_EMPRESA_NAO_COLA()
    {
        var (db, tx, amb) = await PrepararAsync("etq-tenant");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "meu", comConversaEm: Velho);
        var negociacao = await NegociacaoDeAsync(db, id);

        var vizinha = await Semeador.TenantAsync(db, "lpa-etq-vizinha");
        await ZerarAsync(db, vizinha.Id);
        var ambVizinha = amb with
        {
            Cenario = vizinha,
            Contexto = new ContextoMutavel
            {
                EmpresaId = vizinha.Id, UsuarioId = vizinha.Dono.Id, Papel = "dono"
            }
        };
        var dela = await EtiquetaAsync(db, ambVizinha, "dela");

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).AplicarEtiquetaAsync(new EtiquetaEmLote([negociacao], dela), default));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.NegociacoesEtiquetas.IgnoreQueryFilters()
            .Where(x => x.NegociacaoId == negociacao).ToListAsync());
    }

    /// <summary>E negociacao de outra empresa tambem nao: ela nao volta da consulta de alvos.</summary>
    [Fact]
    public async Task NEGOCIACAO_DE_OUTRA_EMPRESA_NAO_GANHA_ETIQUETA()
    {
        var (db, tx, amb) = await PrepararAsync("etq-tenant-neg");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");
        var meu = await LeadAsync(db, amb, "meu", comConversaEm: Velho);
        var minha = await NegociacaoDeAsync(db, meu);

        var vizinha = await Semeador.TenantAsync(db, "lpa-etq-neg-vizinha");
        await ZerarAsync(db, vizinha.Id);
        var ambVizinha = amb with
        {
            Cenario = vizinha,
            Contexto = new ContextoMutavel
            {
                EmpresaId = vizinha.Id, UsuarioId = vizinha.Dono.Id, Papel = "dono"
            }
        };
        var contatoDela = await LeadAsync(db, ambVizinha, "dela", comConversaEm: Velho);
        var dela = await NegociacaoDeAsync(db, contatoDela);

        var r = await Servico(amb).AplicarEtiquetaAsync(
            new EtiquetaEmLote([minha, dela], etq), default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.NegociacoesEtiquetas.IgnoreQueryFilters()
            .Where(x => x.NegociacaoId == dela).ToListAsync());
    }

    // ==================================================================== a metrica de reativados

    /// <summary>===================== VENDA CANCELADA NAO E REATIVACAO =====================
    ///
    /// ⚠️ ESTE E O DEFEITO MAIS FACIL DE COMETER AQUI, e eu o MEDI contra o `nexora_dev` antes de
    /// escrever: com 300 marcas, 5 ganhas depois da marca, 5 CANCELADAS e 5 ganhas antes, a
    /// consulta deu 5; sem `status <> 'cancelada'` deu 10; olhando so `ganha_em IS NOT NULL` deu
    /// 15. A metrica dobra e triplica, nessa ordem.
    ///
    /// A causa esta escrita em `ServicoVendas.CancelarAsync`: "o `ganha_em` fica, e quem tira do
    /// relatorio e o filtro do indice (`status <> 'cancelada'`), nao o carimbo em branco". Quem
    /// marcou venda por engano e desfez deixa o carimbo para tras de proposito.
    /// ============================================================================</summary>
    [Fact]
    public async Task VENDA_CANCELADA_NAO_CONTA_COMO_REATIVADA()
    {
        var (db, tx, amb) = await PrepararAsync("rea-cancelada");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");

        var ganhou = await LeadAsync(db, amb, "ganhou", comConversaEm: Velho);
        var desfez = await LeadAsync(db, amb, "desfez", comConversaEm: Velho);

        await MarcarNegociacaoAsync(db, amb, await NegociacaoDeAsync(db, ganhou), etq);
        await MarcarNegociacaoAsync(db, amb, await NegociacaoDeAsync(db, desfez), etq);

        await GanharDepoisDaMarcaAsync(db, ganhou);
        await GanharDepoisDaMarcaAsync(db, desfez);
        await CancelarAsync(db, desfez);

        var r = await Servico(amb).ReativacaoAsync(Rea(etq), default);

        Assert.Equal(2, r.Marcados);
        Assert.Equal(1, r.Ganhos);
        Assert.Equal(1000m, r.ValorGanho);
    }

    /// <summary>⚠️ GANHO ANTES DA MARCA NAO FOI REATIVADO POR ELA. Sem a comparacao
    /// `ganha_em > criado_em`, colar a etiqueta num negocio que ja estava fechado o contaria como
    /// sucesso da campanha — e marcar em lote cinquenta cards inflaria a metrica na hora.</summary>
    [Fact]
    public async Task GANHO_ANTES_DA_MARCA_NAO_CONTA()
    {
        var (db, tx, amb) = await PrepararAsync("rea-antes");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");
        var id = await LeadAsync(db, amb, "jaera", comConversaEm: Velho);
        var negociacao = await NegociacaoDeAsync(db, id);

        // Ganha ANTES: a marca vem depois, e nao causou nada.
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.Id == negociacao)
            .ExecuteUpdateAsync(u => u
                .SetProperty(n => n.Status, StatusNegociacao.Ganha)
                .SetProperty(n => n.Valor, 1000m)
                .SetProperty(n => n.GanhaEm, MarcaEm.AddDays(-5)));
        db.ChangeTracker.Clear();

        await MarcarNegociacaoAsync(db, amb, negociacao, etq);

        var r = await Servico(amb).ReativacaoAsync(Rea(etq), default);

        Assert.Equal(1, r.Marcados);
        Assert.Equal(0, r.Ganhos);
        Assert.Equal(0m, r.ValorGanho);
    }

    /// <summary>===================== A JANELA E SOBRE A MARCA, NAO SOBRE A VENDA =====================
    ///
    /// ⚠️ Filtrar por `ganha_em` responderia outra pergunta — "das vendas deste mes, quantas
    /// tinham sido marcadas" — e esconderia as reativacoes AINDA EM ANDAMENTO, que no primeiro
    /// mes de uma campanha sao quase tudo. Aqui a marca do mes passado com venda deste mes CONTA,
    /// e entra na janela do mes passado.
    /// ======================================================================================</summary>
    [Fact]
    public async Task A_JANELA_RECORTA_PELA_DATA_DA_MARCA_E_A_VENDA_PODE_SER_DEPOIS()
    {
        var (db, tx, amb) = await PrepararAsync("rea-janela");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");
        var dentro = await LeadAsync(db, amb, "dentro", comConversaEm: Velho);
        var fora = await LeadAsync(db, amb, "fora", comConversaEm: Velho);

        var negDentro = await NegociacaoDeAsync(db, dentro);
        var negFora = await NegociacaoDeAsync(db, fora);

        await MarcarNegociacaoAsync(db, amb, negDentro, etq);
        await MarcarNegociacaoAsync(db, amb, negFora, etq);

        // ⚠️ AS DUAS DATAS SAO FORCADAS. `criado_em` da marca vem do relogio REAL, e a primeira
        // versao deste teste usava a janela larga dos outros — onde as duas marcas caiam dentro, e
        // o teste dizia que a janela nao recortava.
        await db.NegociacoesEtiquetas.IgnoreQueryFilters()
            .Where(x => x.NegociacaoId == negDentro)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.CriadoEm, MarcaEm));
        await db.NegociacoesEtiquetas.IgnoreQueryFilters()
            .Where(x => x.NegociacaoId == negFora)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.CriadoEm, MarcaEm.AddDays(-70)));
        db.ChangeTracker.Clear();

        // A venda da que ficou e MUITO depois da janela: continua contando, na janela da MARCA.
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == dentro)
            .ExecuteUpdateAsync(u => u
                .SetProperty(n => n.Status, StatusNegociacao.Ganha)
                .SetProperty(n => n.Valor, 1000m)
                .SetProperty(n => n.GanhaEm, MarcaEm.AddDays(45)));
        db.ChangeTracker.Clear();

        var r = await Servico(amb).ReativacaoAsync(
            new FiltroReativacao(etq, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), null),
            default);

        Assert.Equal(1, r.Marcados);
        Assert.Equal(1, r.Ganhos);
    }

    /// <summary>A borda do ultimo dia: a janela fecha em `< meia-noite do dia seguinte`. Com
    /// `<= Ate` em timestamp, tudo que foi marcado DURANTE o ultimo dia ficaria de fora.</summary>
    [Fact]
    public async Task A_MARCA_DO_ULTIMO_DIA_DA_JANELA_ENTRA()
    {
        var (db, tx, amb) = await PrepararAsync("rea-borda");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");
        var id = await LeadAsync(db, amb, "borda", comConversaEm: Velho);
        var negociacao = await NegociacaoDeAsync(db, id);
        await MarcarNegociacaoAsync(db, amb, negociacao, etq);

        // 23h local do ultimo dia da janela.
        var ultimoDia = new DateOnly(2026, 8, 5);
        await db.NegociacoesEtiquetas.IgnoreQueryFilters()
            .Where(x => x.NegociacaoId == negociacao)
            .ExecuteUpdateAsync(u => u.SetProperty(
                x => x.CriadoEm, new DateTime(2026, 8, 6, 2, 0, 0, DateTimeKind.Utc)));
        db.ChangeTracker.Clear();

        var r = await Servico(amb).ReativacaoAsync(
            new FiltroReativacao(etq, ultimoDia.AddDays(-30), ultimoDia, null), default);

        Assert.Equal(1, r.Marcados);
    }

    /// <summary>⚠️ QUEM NAO VE OS NUMEROS DA EQUIPE RECEBE SO OS PROPRIOS, e o parametro do cliente
    /// e DESCARTADO — mesma disciplina de `ServicoRelatorios`. E util: o vendedor saber o que a
    /// reativacao DELE rendeu nao depende de permissao nova.</summary>
    [Fact]
    public async Task O_VENDEDOR_SO_VE_O_QUE_ELE_REATIVOU()
    {
        var (db, tx, amb) = await PrepararAsync("rea-recorte");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");
        var ana = await VendedorAsync(db, amb, "ana");
        var bruno = await VendedorAsync(db, amb, "bruno");

        // ⚠️ DOIS PARA A ANA E UM PARA O BRUNO, de proposito. A primeira versao deste teste dava
        // um para cada, e aí "a Ana ve 1" era verdade tanto com o recorte certo quanto com o
        // `responsavelId` do cliente mandando o id do Bruno — 1 == 1 pelos dois motivos, e a
        // sabotagem do recorte passava verde. Com numeros diferentes, o teste distingue.
        var daAna1 = await LeadAsync(db, amb, "a1", comConversaEm: Velho, responsavelId: ana.Id);
        var daAna2 = await LeadAsync(db, amb, "a2", comConversaEm: Velho, responsavelId: ana.Id);
        var doBruno = await LeadAsync(db, amb, "b", comConversaEm: Velho, responsavelId: bruno.Id);

        foreach (var c in new[] { daAna1, daAna2, doBruno })
        {
            await MarcarNegociacaoAsync(db, amb, await NegociacaoDeAsync(db, c), etq);
            await GanharDepoisDaMarcaAsync(db, c);
        }

        // O dono ve os tres.
        Assert.Equal(3, (await Servico(amb).ReativacaoAsync(Rea(etq), default)).Ganhos);

        // A Ana ve os DOIS dela, e o `responsavelId` que ela mandar e jogado fora.
        amb.Contexto.UsuarioId = ana.Id;
        amb.Contexto.Papel = "vendedor";

        var dela = await Servico(amb).ReativacaoAsync(
            new FiltroReativacao(etq, Janela.De, Janela.Ate, bruno.Id), default);

        Assert.Equal(2, dela.Marcados);
        Assert.Equal(2, dela.Ganhos);
    }

    /// <summary>===================== PEDIR A ETIQUETA DA VIZINHA DEVOLVE ZERO =====================
    ///
    /// ⚠️ ESTE TESTE PEDE O ID DA ETIQUETA DELA, e e o unico jeito de a sabotagem morder. A
    /// primeira versao dava a cada empresa a SUA etiqueta e conferia que os numeros nao se
    /// somavam — e isso passava verde ate sem `ne.empresa_id = $1`, porque marca com o
    /// `etiqueta_id` dela nunca casa com o `etiqueta_id` meu. O teste media o que a chave
    /// primaria ja garantia.
    ///
    /// ⚠️ A CHAVE COMPOSTA NAO FECHA ESTA PORTA. `fk_negociacoes_etiquetas_etiqueta` e
    /// `(etiqueta_id, empresa_id)`, o que garante que a LINHA e coerente — a marca da vizinha tem
    /// o `empresa_id` dela. Mas nada impede o cliente de mandar o `etiquetaId` da vizinha na query
    /// string, e `ReativacaoAsync` nao confere a dona da etiqueta (diferente de
    /// `AplicarEtiquetaAsync`, que recusa). Sem o `empresa_id` escrito a mao, a consulta devolveria
    /// as marcas DELA — medido contra o `nexora_dev`: `WHERE etiqueta_id = 1` sem empresa deu DUAS
    /// linhas de outra empresa; com empresa, zero.
    ///
    /// Devolver ZERO, e nao 400, e deliberado: a tela so oferece as etiquetas da propria empresa,
    /// entao o id estranho nao vem dela, e um erro novo seria um modo de falha a mais.
    /// ====================================================================================</summary>
    [Fact]
    public async Task PEDIR_A_ETIQUETA_DA_VIZINHA_DEVOLVE_ZERO()
    {
        var (db, tx, amb) = await PrepararAsync("rea-tenant");
        using var _ = db; using var __ = tx;

        var meu = await LeadAsync(db, amb, "meu", comConversaEm: Velho);

        var vizinha = await Semeador.TenantAsync(db, "lpa-rea-vizinha");
        await ZerarAsync(db, vizinha.Id);
        var ambVizinha = amb with
        {
            Cenario = vizinha,
            Contexto = new ContextoMutavel
            {
                EmpresaId = vizinha.Id, UsuarioId = vizinha.Dono.Id, Papel = "dono"
            }
        };

        // A vizinha reativa e ganha DUAS.
        var dela = await EtiquetaAsync(db, ambVizinha, "reativacao-dela");

        foreach (var marca in new[] { "d1", "d2" })
        {
            var c = await LeadAsync(db, ambVizinha, marca, comConversaEm: Velho);
            await MarcarNegociacaoAsync(db, ambVizinha, await NegociacaoDeAsync(db, c), dela);
            await GanharDepoisDaMarcaAsync(db, c);
        }

        // E eu pergunto pelo id DELA, que e o que o cliente pode mandar na query string.
        var r = await Servico(amb).ReativacaoAsync(Rea(dela), default);

        Assert.Equal(0, r.Marcados);
        Assert.Equal(0, r.Ganhos);
        Assert.Equal(0m, r.ValorGanho);

        // E a minha propria etiqueta continua respondendo o que e meu.
        var minha = await EtiquetaAsync(db, amb, "reativacao-minha");
        await MarcarNegociacaoAsync(db, amb, await NegociacaoDeAsync(db, meu), minha);
        await GanharDepoisDaMarcaAsync(db, meu);

        var meusNumeros = await Servico(amb).ReativacaoAsync(Rea(minha), default);

        Assert.Equal(1, meusNumeros.Marcados);
        Assert.Equal(1, meusNumeros.Ganhos);
    }

    [Fact]
    public async Task JANELA_INVERTIDA_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("rea-invertida");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "reativacao-out");

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).ReativacaoAsync(
                new FiltroReativacao(etq, Janela.Ate, Janela.De, null), default));
    }

    /// <summary>Etiqueta que ninguem usou devolve zero, nao erro: a tela abre com um numero, e um
    /// 400 aqui faria o bloco inteiro desaparecer por falta de dado.</summary>
    [Fact]
    public async Task ETIQUETA_SEM_USO_DEVOLVE_ZERO()
    {
        var (db, tx, amb) = await PrepararAsync("rea-zero");
        using var _ = db; using var __ = tx;

        var etq = await EtiquetaAsync(db, amb, "nunca-usada");

        var r = await Servico(amb).ReativacaoAsync(Rea(etq), default);

        Assert.Equal(0, r.Marcados);
        Assert.Equal(0, r.Ganhos);
        Assert.Equal(0m, r.ValorGanho);
    }

    // ==================================================================== a aba "Perdidos"

    /// <summary>===================== AS DUAS ABAS SAO DISJUNTAS =====================
    ///
    /// ⚠️ `Perdidos` EXIGE NENHUMA NEGOCIACAO ABERTA. `uq_negociacoes_card_por_funil` so conta
    /// `aberta` e `ganha`, entao a mesma pessoa pode ter uma perda em Vendas e um negocio aberto
    /// em Pos-venda — e sem esta regra ela apareceria nas DUAS abas. "Reabrir em lote" cairia
    /// sobre quem ja esta sendo trabalhado, e `AbrirNegociacaoAsync` responderia 409 para metade
    /// do lote.
    /// ====================================================================</summary>
    [Fact]
    public async Task QUEM_TEM_NEGOCIO_ABERTO_NAO_APARECE_EM_PERDIDOS()
    {
        var (db, tx, amb) = await PrepararAsync("perd-disjuntas");
        using var _ = db; using var __ = tx;

        // Só perdeu: é da aba Perdidos.
        var soPerdeu = await LeadAsync(db, amb, "perdeu", comConversaEm: Velho);
        await MudarStatusAsync(db, soPerdeu, StatusNegociacao.Perdida);

        // Perdeu em Vendas E tem um aberto em Pós-venda: está sendo trabalhado.
        var trabalhando = await LeadAsync(db, amb, "misto", comConversaEm: Velho);
        await MudarStatusAsync(db, trabalhando, StatusNegociacao.Perdida);
        await NegocioAbertoAsync(db, amb, trabalhando);

        var perdidos = await Servico(amb).ListarAsync(Filtro(aba: AbaDeLeads.Perdidos), default);

        Assert.Equal([soPerdeu], perdidos.Itens.Select(i => i.ContatoId).Distinct());

        // E ele aparece em Parados, que é onde o negócio aberto dele está esfriando.
        var parados = await Servico(amb).ListarAsync(Filtro(), default);

        Assert.Contains(trabalhando, parados.Itens.Select(i => i.ContatoId));
        Assert.DoesNotContain(soPerdeu, parados.Itens.Select(i => i.ContatoId));
    }

    /// <summary>===================== O EIXO DE TEMPO E OUTRO =====================
    ///
    /// ⚠️ `Parados` corta por `conversas.ultima_mensagem_em`; `Perdidos` corta por
    /// `negociacoes.perdida_em`. Uma perda de ontem numa conversa velha NAO e "perdido ha 30
    /// dias" — e so isso impede a aba de listar como frio quem acabou de ser decidido.
    /// ================================================================</summary>
    [Fact]
    public async Task A_ABA_PERDIDOS_CORTA_PELA_DATA_DA_PERDA_NAO_PELA_CONVERSA()
    {
        var (db, tx, amb) = await PrepararAsync("perd-eixo");
        using var _ = db; using var __ = tx;

        // Conversa VELHA, perda de ONTEM: fora da janela de 30 dias.
        var recemPerdido = await LeadAsync(db, amb, "ontem", comConversaEm: Velho);
        await MudarStatusAsync(db, recemPerdido, StatusNegociacao.Perdida);
        await PerdidaEmAsync(db, recemPerdido, Recente);

        // Conversa RECENTE, perda VELHA: dentro da janela.
        var velhaPerda = await LeadAsync(db, amb, "velha", comConversaEm: Recente);
        await MudarStatusAsync(db, velhaPerda, StatusNegociacao.Perdida);
        await PerdidaEmAsync(db, velhaPerda, Velho);

        var r = await Servico(amb).ListarAsync(Filtro(aba: AbaDeLeads.Perdidos), default);

        Assert.Equal([velhaPerda], r.Itens.Select(i => i.ContatoId));
    }

    /// <summary>⚠️ O MOTIVO DA PERDA VEM NA LINHA, e e a primeira informacao de quem vai reabrir:
    /// "perdemos por preco" e "perdemos por prazo" levam a abordagens diferentes, e reabrir sem
    /// ler isso e repetir a conversa que falhou.</summary>
    [Fact]
    public async Task A_LINHA_DE_PERDIDO_TRAZ_O_MOTIVO_E_A_DE_PARADO_NAO()
    {
        var (db, tx, amb) = await PrepararAsync("perd-motivo");
        using var _ = db; using var __ = tx;

        var perdido = await LeadAsync(db, amb, "perdido", comConversaEm: Velho);
        await MudarStatusAsync(db, perdido, StatusNegociacao.Perdida);
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == perdido)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.MotivoPerda, "achou caro"));
        db.ChangeTracker.Clear();

        var deperdidos = await Servico(amb).ListarAsync(Filtro(aba: AbaDeLeads.Perdidos), default);

        Assert.Equal("achou caro", deperdidos.Itens.Single().MotivoPerda);

        // E em Parados ninguem tem motivo: nao houve perda.
        var aberto = await LeadAsync(db, amb, "aberto", comConversaEm: Velho);
        var deparados = await Servico(amb).ListarAsync(Filtro(), default);

        Assert.All(deparados.Itens, i => Assert.Null(i.MotivoPerda));
        Assert.Contains(aberto, deparados.Itens.Select(i => i.ContatoId));
    }

    /// <summary>⚠️ VENDA CANCELADA NAO E PERDA, e a entrega 1 ja tinha essa regra do outro lado:
    /// `NEGOCIO_CANCELADO_DEVOLVE_O_CONTATO_A_LISTA` prova que ele volta para Parados. Aqui o
    /// espelho — ele nao aparece em Perdidos, porque cancelar e desfazer um registro, nao perder
    /// o cliente.
    ///
    /// ⚠️ MAS QUEM GUARDA ISSO E O BANCO, NAO O `status = 'perdida'` DA CONSULTA. Sabotei o filtro
    /// para `status IN ('perdida', 'cancelada')` e NADA CAIU — porque cancelada com `perdida_em`
    /// preenchido e estado inalcancavel: `CancelarAsync` exige `ganha_em IS NOT NULL`, e
    /// `ck_negociacoes_terminal` proibe `ganha_em` e `perdida_em` juntos. O `perdida_em < $1` ja
    /// basta.
    ///
    /// O filtro de status fica por OUTRA razao, essa sim medida: ele e o predicado do indice
    /// parcial. Ver `O_CORTE_DE_PERDIDOS_USA_O_INDICE_PARCIAL`.</summary>
    [Fact]
    public async Task CANCELADA_NAO_APARECE_EM_PERDIDOS()
    {
        var (db, tx, amb) = await PrepararAsync("perd-cancelada");
        using var _ = db; using var __ = tx;

        var cancelado = await LeadAsync(db, amb, "cancelou", comConversaEm: Velho);
        await MudarStatusAsync(db, cancelado, StatusNegociacao.Cancelada);

        var r = await Servico(amb).ListarAsync(Filtro(aba: AbaDeLeads.Perdidos), default);

        Assert.Empty(r.Itens);
    }

    /// <summary>Contato anonimizado sai das duas abas: a LGPD zerou a PII, e listar um nome em
    /// branco com telefone em branco nao e lead nenhum.</summary>
    [Fact]
    public async Task ANONIMIZADO_SAI_DE_PERDIDOS()
    {
        var (db, tx, amb) = await PrepararAsync("perd-anon");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "anon", comConversaEm: Velho);
        await MudarStatusAsync(db, id, StatusNegociacao.Perdida);
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.AnonimizadoEm, Velho));
        db.ChangeTracker.Clear();

        Assert.Empty((await Servico(amb).ListarAsync(Filtro(aba: AbaDeLeads.Perdidos), default)).Itens);
    }

    /// <summary>===================== O FILTRO DE STATUS PAGA PELO INDICE =====================
    ///
    /// ⚠️ ESTE TESTE EXISTE PORQUE A SABOTAGEM DO `status` NAO DERRUBAVA NADA. O filtro nao muda
    /// o RESULTADO — `perdida_em IS NOT NULL` ja implica `status = 'perdida'`, por causa do
    /// `ck_negociacoes_terminal` e da precondicao do cancelamento. Ele muda o PLANO: e o
    /// predicado de `ix_negociacoes_perdidas ... WHERE status = 'perdida'`, e sem ele o indice
    /// parcial nao se aplica.
    ///
    /// Medido contra o `nexora_dev` antes de escrever: com o status, `Index Only Scan` com as
    /// duas condicoes no `Index Cond`, custo 14.55; sem ele, `Seq Scan` com 1155 linhas
    /// descartadas, custo 37.97 — as mesmas 110 linhas de saida.
    ///
    /// ⚠️ A CARGA E NECESSARIA. Com uma duzia de linhas o planejador varre a tabela e esta certo,
    /// e o teste diria que o indice nao serve. Mesmo andaime do `SerieTemporalDbTests`, que mede
    /// plano pela mesma razao.
    ///
    /// ⚠️ MUITAS PERDAS DO MESMO CONTATO NO MESMO FUNIL SAO LEGAIS:
    /// `uq_negociacoes_card_por_funil` so cobre `aberta` e `ganha`. E `ck_negociacoes_valor`
    /// isenta `perdida` do `valor > 0`.
    /// ================================================================================</summary>
    [Fact]
    public async Task O_CORTE_DE_PERDIDOS_USA_O_INDICE_PARCIAL()
    {
        var (db, tx, amb) = await PrepararAsync("perd-plano");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "carga", comConversaEm: Velho);

        await CargaDePerdasAsync(db, amb, id, perdas: 400, enchimento: 3600);

        // Sem estatistica o planejador usa a estimativa do catalogo, que numa transacao nova nao
        // conhece as linhas recem-inseridas.
        await db.Database.ExecuteSqlRawAsync("ANALYZE negociacoes");

        var plano = await ExplicarPerdidasAsync(db, amb.Cenario.Id, Velho.AddDays(2));

        // O plano vai na mensagem: sem ele, "sub-string not found" nao diz o que o banco escolheu,
        // e foi exatamente o que me custou uma rodada aqui.
        Assert.Contains("ix_negociacoes_perdidas", plano);
        Assert.DoesNotContain("Seq Scan on negociacoes", plano);
    }

    // ==================================================================== reabrir em lote

    /// <summary>===================== REABRIR REVIVE A PERDA NA ETAPA ONDE ELA MORREU =====================
    ///
    /// ⚠️ E NAO ABRE UMA LINHA NOVA NA PRIMEIRA ETAPA. A etapa onde o negocio morreu e a unica
    /// informacao que reviver existe para preservar — quem perdeu na Proposta volta na Proposta,
    /// nao no comeco do funil. Essa regra esta em `AbrirNegociacaoAsync`, e este teste existe
    /// para o lote nao ganhar uma segunda implementacao que a esqueca.
    /// ========================================================================================</summary>
    [Fact]
    public async Task REABRIR_EM_LOTE_REVIVE_A_MESMA_NEGOCIACAO_NA_ETAPA_DELA()
    {
        var (db, tx, amb) = await PrepararAsync("reab-revive");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "perdido", comConversaEm: Velho);
        var negociacao = await NegociacaoDeAsync(db, id);
        var etapaOndeMorreu = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == negociacao).Select(n => n.EtapaId).SingleAsync();

        await MudarStatusAsync(db, id, StatusNegociacao.Perdida);

        var r = await Servico(amb).ReabrirAsync([id], default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(0, r.Pulados);

        db.ChangeTracker.Clear();
        var voltou = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.ContatoId == id).ToListAsync();

        // A MESMA linha, nao uma nova.
        var unica = Assert.Single(voltou);
        Assert.Equal(negociacao, unica.Id);
        Assert.Equal(StatusNegociacao.Aberta, unica.Status);
        Assert.Null(unica.PerdidaEm);
        Assert.Null(unica.MotivoPerda);
        Assert.Equal(etapaOndeMorreu, unica.EtapaId);
    }

    /// <summary>⚠️ CONFLITO E `Pulados`, E O LOTE SEGUE. Quem ja tem negocio em todos os funis
    /// volta 409 em `AbrirNegociacaoAsync`; abortar o lote por causa dele faria o operador perder
    /// os outros quarenta e nove, sem saber qual era o problematico.</summary>
    [Fact]
    public async Task QUEM_NAO_TEM_FUNIL_LIVRE_E_PULADO_E_O_LOTE_SEGUE()
    {
        var (db, tx, amb) = await PrepararAsync("reab-conflito");
        using var _ = db; using var __ = tx;

        // Este tem negócio ABERTO no único funil: não há para onde reabrir.
        var ocupado = await LeadAsync(db, amb, "ocupado", comConversaEm: Velho);

        var livre = await LeadAsync(db, amb, "livre", comConversaEm: Velho);
        await MudarStatusAsync(db, livre, StatusNegociacao.Perdida);

        var r = await Servico(amb).ReabrirAsync([ocupado, livre], default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Pulados);
        Assert.Equal(0, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.Equal(
            StatusNegociacao.Aberta,
            await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.ContatoId == livre).Select(n => n.Status).SingleAsync());
    }

    /// <summary>⚠️ DUAS LINHAS DA MESMA PESSOA VIRAM UMA REABERTURA. A aba mostra uma linha por
    /// PERDA, e quem perdeu em dois funis aparece duas vezes — mandar o id duas vezes nao pode
    /// abrir dois negocios nem contar dois.</summary>
    [Fact]
    public async Task O_MESMO_CONTATO_DUAS_VEZES_CONTA_UMA()
    {
        var (db, tx, amb) = await PrepararAsync("reab-dedupe");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "duas", comConversaEm: Velho);
        await MudarStatusAsync(db, id, StatusNegociacao.Perdida);

        var r = await Servico(amb).ReabrirAsync([id, id], default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(0, r.Pulados);
    }

    [Fact]
    public async Task SEM_O_GESTO_REABRIR_EM_LOTE_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("reab-sempermissao");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "zeca");
        var id = await LeadAsync(db, amb, "alvo", comConversaEm: Velho, responsavelId: vendedor.Id);
        await MudarStatusAsync(db, id, StatusNegociacao.Perdida);

        amb.Contexto.UsuarioId = vendedor.Id;
        amb.Contexto.Papel = "vendedor";

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).ReabrirAsync([id], default));

        db.ChangeTracker.Clear();
        Assert.Equal(
            StatusNegociacao.Perdida,
            await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.ContatoId == id).Select(n => n.Status).SingleAsync());
    }

    /// <summary>⚠️ O ANONIMIZADO FALHA, NAO "PULA" (revisao LPA-1). A recusa de dentro vem como
    /// conflito, e conflito aqui e "pulado" — que a tela explica como "ja tem negocio em todos os
    /// funis". O anonimizado nao tem negocio nenhum: ele nao pode mais ser reaberto.</summary>
    [Fact]
    public async Task REABRIR_CONTA_O_ANONIMIZADO_COMO_FALHA_E_NAO_COMO_PULADO()
    {
        var (db, tx, amb) = await PrepararAsync("reab-anonimo");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "anonimo", comConversaEm: Velho);
        await MudarStatusAsync(db, id, StatusNegociacao.Perdida);
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AnonimizadoEm, (DateTime?)Velho));
        db.ChangeTracker.Clear();

        var r = await Servico(amb).ReabrirAsync([id], default);

        Assert.Equal(new ResultadoEmLote(0, 0, 1), r);
    }

    /// <summary>⚠️ ERRO INESPERADO NUM ITEM NAO DERRUBA O LOTE (revisao LPA-1). Uma falha de gravacao
    /// devolvia 500 com os itens anteriores JA reabertos, e o operador ficava sem a contagem. Aqui a
    /// gravacao da negociacao falha de proposito, nos dois: o lote termina, e diz que os dois
    /// falharam.</summary>
    [Fact]
    public async Task UM_ITEM_QUE_ESTOURA_NAO_DERRUBA_O_REABRIR_EM_LOTE()
    {
        var falha = new FalhaNoComando("UPDATE negociacoes");
        var (db, tx, amb) = await PrepararAsync("reab-estoura", falha);
        using var _ = db; using var __ = tx;

        var um = await LeadAsync(db, amb, "um", comConversaEm: Velho);
        var dois = await LeadAsync(db, amb, "dois", comConversaEm: Velho);
        await MudarStatusAsync(db, um, StatusNegociacao.Perdida);
        await MudarStatusAsync(db, dois, StatusNegociacao.Perdida);

        falha.Armada = true;
        var r = await Servico(amb).ReabrirAsync([um, dois], default);
        falha.Armada = false;

        Assert.Equal(new ResultadoEmLote(0, 0, 2), r);
    }

    /// <summary>⚠️ E A FALHA DE UM NAO CONTAMINA O SEGUINTE. A entidade que nao gravou fica pendurada
    /// no rastreador, e sem limpa-lo o `SaveChanges` do item seguinte a gravaria junto: o primeiro
    /// seria REABERTO mesmo contado como falha. O primeiro falha, o segundo passa — e o primeiro
    /// continua perdido.</summary>
    [Fact]
    public async Task A_FALHA_DE_UM_ITEM_NAO_REABRE_ELE_JUNTO_COM_O_SEGUINTE()
    {
        var falha = new FalhaNoComando("UPDATE negociacoes") { Limite = 1 };
        var (db, tx, amb) = await PrepararAsync("reab-contamina", falha);
        using var _ = db; using var __ = tx;

        var um = await LeadAsync(db, amb, "um", comConversaEm: Velho);
        var dois = await LeadAsync(db, amb, "dois", comConversaEm: Velho);
        await MudarStatusAsync(db, um, StatusNegociacao.Perdida);
        await MudarStatusAsync(db, dois, StatusNegociacao.Perdida);

        falha.Armada = true;
        var r = await Servico(amb).ReabrirAsync([um, dois], default);
        falha.Armada = false;

        Assert.Equal(new ResultadoEmLote(1, 0, 1), r);

        db.ChangeTracker.Clear();
        Assert.False(await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(n => n.ContatoId == um && n.Status == StatusNegociacao.Aberta));
        Assert.True(await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(n => n.ContatoId == dois && n.Status == StatusNegociacao.Aberta));
    }

    /// <summary>⚠️ ID DE OUTRA EMPRESA ENTRA EM `Falhou`, nao reabre nada: `AbrirNegociacaoAsync`
    /// carrega o contato pelo filtro global, e ele nao aparece.</summary>
    [Fact]
    public async Task REABRIR_ID_DE_OUTRA_EMPRESA_FALHA_SEM_TOCAR_NELE()
    {
        var (db, tx, amb) = await PrepararAsync("reab-tenant");
        using var _ = db; using var __ = tx;

        var meu = await LeadAsync(db, amb, "meu", comConversaEm: Velho);
        await MudarStatusAsync(db, meu, StatusNegociacao.Perdida);

        var vizinha = await Semeador.TenantAsync(db, "lpa-reab-vizinha");
        await ZerarAsync(db, vizinha.Id);
        var ambVizinha = amb with
        {
            Cenario = vizinha,
            Contexto = new ContextoMutavel
            {
                EmpresaId = vizinha.Id, UsuarioId = vizinha.Dono.Id, Papel = "dono"
            }
        };
        var dela = await LeadAsync(db, ambVizinha, "dela", comConversaEm: Velho);
        await MudarStatusAsync(db, dela, StatusNegociacao.Perdida);

        var r = await Servico(amb).ReabrirAsync([meu, dela], default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.Equal(
            StatusNegociacao.Perdida,
            await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.ContatoId == dela).Select(n => n.Status).SingleAsync());
    }

    /// <summary>===================== REABRIR NAO DESFAZ A VENDA ANTERIOR =====================
    ///
    /// ⚠️ A TABELA `vendas` NAO EXISTE MAIS — a venda E a negociacao, e o comentario de
    /// `AbrirNegociacaoAsync` fala de uma tabela que foi fundida depois (o E4b reinseriu as linhas
    /// dela como negociacoes). Escrevi este teste contra `db.Vendas` primeiro e o compilador
    /// recusou; o invariante atual e outro, e e este.
    ///
    /// Reabrir e "o cliente voltou", e o que ja foi faturado continua faturado: a negociacao
    /// CONCLUIDA fica intacta, e a rodada nova e outra linha. Rebaixa-la para aberta faria o
    /// faturamento de um mes FECHADO mudar sozinho — o defeito que aquele comentario registra.
    ///
    /// `uq_negociacoes_card_por_funil` so conta `aberta` e `ganha`, entao a concluida nao ocupa o
    /// funil e a perda do mesmo funil pode ser revivida ao lado dela.
    /// ================================================================================</summary>
    [Fact]
    public async Task REABRIR_EM_LOTE_NAO_DESFAZ_A_VENDA_JA_CONCLUIDA()
    {
        var (db, tx, amb) = await PrepararAsync("reab-venda-feita");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "recorrente", comConversaEm: Velho);

        // A compra passada, concluida.
        var concluida = await NegociacaoDeAsync(db, id);
        await MudarStatusAsync(db, id, StatusNegociacao.Concluida);

        var comoEstava = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == concluida)
            .Select(n => new { n.Status, n.GanhaEm, n.ConcluidaEm, n.Valor }).SingleAsync();

        // E uma perda, no mesmo funil.
        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = id,
            PipelineId = amb.Cenario.Etapas[0].PipelineId,
            EtapaId = amb.Cenario.Etapas[0].Id,
            Status = StatusNegociacao.Perdida,
            PerdidaEm = Velho,
            MotivoPerda = "achou caro"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var r = await Servico(amb).ReabrirAsync([id], default);

        Assert.Equal(1, r.Criados);

        db.ChangeTracker.Clear();
        var agora = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == concluida)
            .Select(n => new { n.Status, n.GanhaEm, n.ConcluidaEm, n.Valor }).SingleAsync();

        Assert.Equal(comoEstava, agora);

        // E a perda e que voltou a ser aberta.
        Assert.Single(await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.ContatoId == id && n.Status == StatusNegociacao.Aberta).ToListAsync());
    }

    // ==================================================================== quem so ve o seu, so age sobre o seu

    /// <summary>O vendedor com o gesto DELEGADO e sem `VerNumerosDaEquipe` — o uso que `Permissoes`
    /// descreve para `AgirEmLote`. Ele e a Ana tem um lead cada; ele manda os ids dos DOIS.</summary>
    private static async Task<(Usuario Bruno, Usuario Ana, long LeadDoBruno, long LeadDaAna)>
        DoisVendedoresAsync(NexoraDbContext db, Ambiente amb, bool comNegocio = true)
    {
        var bruno = await VendedorAsync(db, amb, "bruno");
        var ana = await VendedorAsync(db, amb, "ana");

        var doBruno = await LeadAsync(db, amb, "dele", comConversaEm: Velho, responsavelId: bruno.Id,
            comNegocio: comNegocio);
        var daAna = await LeadAsync(db, amb, "dela", comConversaEm: Velho, responsavelId: ana.Id,
            comNegocio: comNegocio);

        amb.Contexto.UsuarioId = bruno.Id;
        amb.Contexto.Papel = "vendedor";
        amb.Contexto.ExcecoesDePermissao = new Dictionary<Permissao, bool>
        {
            [Permissao.AgirEmLote] = true
        };

        return (bruno, ana, doBruno, daAna);
    }

    /// <summary>===================== A CARTEIRA DA COLEGA NAO SE TOMA PELO ID =====================
    ///
    /// ⚠️ O CASO DA REVISAO: as acoes em lote so conferiam `AgirEmLote`, e os ids vem do cliente.
    /// O Bruno mandava ao `/leads-parados/responsavel` os ids da carteira da Ana e a tomava inteira.
    /// Agora ele so mexe no que a propria lista mostraria; o da Ana conta como nao encontrado.
    /// ========================================================================================</summary>
    [Fact]
    public async Task QUEM_SO_VE_O_SEU_SO_REDISTRIBUI_O_SEU()
    {
        var (db, tx, amb) = await PrepararAsync("so-seu-red");
        using var _ = db; using var __ = tx;

        var (bruno, ana, doBruno, daAna) = await DoisVendedoresAsync(db, amb);
        var carla = await VendedorAsync(db, amb, "carla");
        var negDoBruno = await NegociacaoDeAsync(db, doBruno);
        var negDaAna = await NegociacaoDeAsync(db, daAna);

        var r = await Servico(amb).RedistribuirAsync(
            new RedistribuicaoEmLote([negDoBruno, negDaAna], carla.Id), default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.Equal(ana.Id, await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == negDaAna).Select(n => n.ResponsavelId).SingleAsync());
        Assert.Equal(carla.Id, await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == negDoBruno).Select(n => n.ResponsavelId).SingleAsync());
    }

    [Fact]
    public async Task QUEM_SO_VE_O_SEU_SO_ETIQUETA_O_SEU()
    {
        var (db, tx, amb) = await PrepararAsync("so-seu-etq");
        using var _ = db; using var __ = tx;

        var (_, _, doBruno, daAna) = await DoisVendedoresAsync(db, amb);
        var etiqueta = await EtiquetaAsync(db, amb, "reativacao");
        var negDoBruno = await NegociacaoDeAsync(db, doBruno);
        var negDaAna = await NegociacaoDeAsync(db, daAna);

        var r = await Servico(amb).AplicarEtiquetaAsync(
            new EtiquetaEmLote([negDoBruno, negDaAna], etiqueta), default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Falhou);
    }

    /// <summary>O lembrete e por CONTATO, e vale a mesma regra de dono da lista — inclusive para o
    /// lead SEM negocio, cujo dono e o do contato.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QUEM_SO_VE_O_SEU_SO_CRIA_LEMBRETE_NO_SEU(bool comNegocio)
    {
        var (db, tx, amb) = await PrepararAsync($"so-seu-lem-{comNegocio}");
        using var _ = db; using var __ = tx;

        var (_, _, doBruno, daAna) = await DoisVendedoresAsync(db, amb, comNegocio);

        var r = await Servico(amb).CriarLembretesAsync(
            new LembreteEmLote([doBruno, daAna], Amanha, "Retomar", null), default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Falhou);
    }

    /// <summary>O lembrete criado da aba PERDIDOS. ⚠️ O CONTATO SEM DONO E O CASO REAL: a
    /// `LiberacaoDeCiclo` zera o dono do contato quando uma venda e CONCLUIDA, e o cliente que ja
    /// comprou e depois teve um negocio novo perdido fica com o contato sem dono — o dono que resta
    /// e o do negocio perdido, que e por onde a aba Perdidos dele o mostra.</summary>
    [Fact]
    public async Task QUEM_SO_VE_O_SEU_CRIA_LEMBRETE_NA_PERDA_DELE_MESMO_COM_O_CONTATO_SEM_DONO()
    {
        var (db, tx, amb) = await PrepararAsync("so-seu-lem-perda");
        using var _ = db; using var __ = tx;

        var (_, _, doBruno, daAna) = await DoisVendedoresAsync(db, amb);
        await MudarStatusAsync(db, doBruno, StatusNegociacao.Perdida);
        await MudarStatusAsync(db, daAna, StatusNegociacao.Perdida);
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == doBruno || c.Id == daAna)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, (long?)null));
        db.ChangeTracker.Clear();

        var r = await Servico(amb).CriarLembretesAsync(
            new LembreteEmLote([doBruno, daAna], Amanha, "Retomar", null), default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Falhou);
    }

    /// <summary>Na aba Perdidos o dono e o do negocio PERDIDO — e assim que a aba dele e montada.</summary>
    [Fact]
    public async Task QUEM_SO_VE_O_SEU_SO_REABRE_A_PERDA_DELE()
    {
        var (db, tx, amb) = await PrepararAsync("so-seu-reabre");
        using var _ = db; using var __ = tx;

        var (_, _, doBruno, daAna) = await DoisVendedoresAsync(db, amb);
        await MudarStatusAsync(db, doBruno, StatusNegociacao.Perdida);
        await MudarStatusAsync(db, daAna, StatusNegociacao.Perdida);

        var r = await Servico(amb).ReabrirAsync([doBruno, daAna], default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.False(await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(n => n.ContatoId == daAna && n.Status == StatusNegociacao.Aberta));
    }

    // ==================================================================== redistribuir

    /// <summary>===================== AS TRES COLUNAS DE DONO MUDAM JUNTAS =====================
    ///
    /// ⚠️ ESTE PROJETO TEM TRES, e cada uma alimenta telas diferentes:
    ///
    ///   `negociacoes.responsavel_id`  relatorios, atribuicao, leads parados
    ///   `contatos.responsavel_id`     lista de contatos, card do kanban, filtro e Meu Dia
    ///   `conversas.responsavel_id`    caixa de entrada
    ///
    /// Mexer so na primeira faria a lista dizer Ana e a caixa dizer Bruno, sem nada na interface
    /// explicando a diferenca. `ServicoConversas` ja registra esse defeito ao contrario — "as
    /// quatro telas diziam 'sem responsavel' para lead com dono ha semanas".
    /// ================================================================================</summary>
    [Fact]
    public async Task REDISTRIBUIR_MUDA_A_NEGOCIACAO_O_CONTATO_E_A_CONVERSA()
    {
        var (db, tx, amb) = await PrepararAsync("red-tres");
        using var _ = db; using var __ = tx;

        var bruno = await VendedorAsync(db, amb, "bruno");
        var ana = await VendedorAsync(db, amb, "ana");

        var id = await LeadAsync(db, amb, "lead", comConversaEm: Velho, responsavelId: bruno.Id);
        var negociacao = await NegociacaoDeAsync(db, id);

        var r = await Servico(amb).RedistribuirAsync(
            new RedistribuicaoEmLote([negociacao], ana.Id), default);

        Assert.Equal(1, r.Criados);

        db.ChangeTracker.Clear();

        Assert.Equal(ana.Id, await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == negociacao).Select(n => n.ResponsavelId).SingleAsync());

        Assert.Equal(ana.Id, await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.ResponsavelId).SingleAsync());

        var conversa = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.ContatoId == id).Select(v => new { v.ResponsavelId, v.AtribuidoEm })
            .SingleAsync();

        Assert.Equal(ana.Id, conversa.ResponsavelId);
        // ⚠️ A DATA ACOMPANHA O DONO. Dono sem data e um estado que o semeador documenta nao
        // existir, e a caixa usa a data para ordenar o que cada um assumiu.
        Assert.NotNull(conversa.AtribuidoEm);
    }

    /// <summary>===================== SO NEGOCIO ABERTO MUDA DE DONO =====================
    ///
    /// ⚠️ O DEFEITO DA REVISAO: a aba Perdidos manda o id do negocio PERDIDO, e "Mudar responsavel"
    /// ali reescrevia o dono da perda — os relatorios atribuem por essa coluna, entao as perdas da
    /// Ana viravam do Bruno. Pela API, o mesmo valia para venda ja concluida: o credito de um mes
    /// fechado mudava de pessoa.
    /// =====================================================================</summary>
    [Theory]
    [InlineData(StatusNegociacao.Perdida)]
    [InlineData(StatusNegociacao.Concluida)]
    public async Task REDISTRIBUIR_NAO_MEXE_EM_NEGOCIO_QUE_NAO_ESTA_ABERTO(StatusNegociacao status)
    {
        var (db, tx, amb) = await PrepararAsync($"red-{status}");
        using var _ = db; using var __ = tx;

        var bruno = await VendedorAsync(db, amb, "bruno");
        var ana = await VendedorAsync(db, amb, "ana");

        var id = await LeadAsync(db, amb, "lead", comConversaEm: Velho, responsavelId: bruno.Id);
        var negociacao = await NegociacaoDeAsync(db, id);
        await MudarStatusAsync(db, id, status);

        var r = await Servico(amb).RedistribuirAsync(
            new RedistribuicaoEmLote([negociacao], ana.Id), default);

        Assert.Equal(0, r.Criados);
        Assert.Equal(1, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.Equal(bruno.Id, await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == negociacao).Select(n => n.ResponsavelId).SingleAsync());
        Assert.NotEqual(ana.Id, await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.ResponsavelId).SingleAsync());
    }

    /// <summary>===================== TUDO OU NADA =====================
    ///
    /// ⚠️ O DEFEITO DA REVISAO: eram tres escritas com commit proprio — o `SaveChanges` das
    /// negociacoes e dois `ExecuteUpdate` —, e o metodo prometia "na mesma transacao" sem haver
    /// transacao. Uma falha na escrita das conversas deixava o negocio com a Ana e o contato e a
    /// caixa com o Bruno.
    ///
    /// Aqui a escrita das CONVERSAS falha de proposito (`FalhaNoComando`), e o negocio e o contato
    /// tem de continuar com o Bruno. Na versao antiga, a negociacao ja estava gravada quando a
    /// terceira escrita caia — e este teste caia junto.
    /// ==========================================================</summary>
    [Fact]
    public async Task REDISTRIBUIR_E_TUDO_OU_NADA_SE_A_CONVERSA_FALHA_NADA_MUDA()
    {
        var falha = new FalhaNoComando("UPDATE conversas");
        var (db, tx, amb) = await PrepararAsync("red-atomico", falha);
        using var _ = db; using var __ = tx;

        var bruno = await VendedorAsync(db, amb, "bruno");
        var ana = await VendedorAsync(db, amb, "ana");

        var id = await LeadAsync(db, amb, "lead", comConversaEm: Velho, responsavelId: bruno.Id);
        var negociacao = await NegociacaoDeAsync(db, id);

        falha.Armada = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Servico(amb).RedistribuirAsync(
            new RedistribuicaoEmLote([negociacao], ana.Id), default));
        falha.Armada = false;

        db.ChangeTracker.Clear();

        Assert.Equal(bruno.Id, await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == negociacao).Select(n => n.ResponsavelId).SingleAsync());
        Assert.Equal(bruno.Id, await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.ResponsavelId).SingleAsync());
    }

    /// <summary>⚠️ AQUI SOBRESCREVER E O CERTO, ao contrario de `AtribuirContatoSeVagoAsync`, que
    /// "so preenche o que esta vago" para o primeiro a responder nao roubar a carteira do colega.
    /// Aquele e efeito colateral de atender; este e gesto de gestao, explicito, com permissao
    /// propria — o proposito do botao E passar o lead para outra pessoa.</summary>
    [Fact]
    public async Task REDISTRIBUIR_SOBRESCREVE_O_DONO_QUE_JA_HAVIA()
    {
        var (db, tx, amb) = await PrepararAsync("red-sobrescreve");
        using var _ = db; using var __ = tx;

        var bruno = await VendedorAsync(db, amb, "bruno");
        var ana = await VendedorAsync(db, amb, "ana");

        var id = await LeadAsync(db, amb, "lead", comConversaEm: Velho, responsavelId: bruno.Id);

        // A conversa tambem ja e do Bruno.
        await db.Conversas.IgnoreQueryFilters().Where(v => v.ContatoId == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(v => v.ResponsavelId, bruno.Id)
                .SetProperty(v => v.AtribuidoEm, Velho));
        db.ChangeTracker.Clear();

        await Servico(amb).RedistribuirAsync(
            new RedistribuicaoEmLote([await NegociacaoDeAsync(db, id)], ana.Id), default);

        db.ChangeTracker.Clear();
        Assert.Equal(ana.Id, await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.ContatoId == id).Select(v => v.ResponsavelId).SingleAsync());
    }

    /// <summary>⚠️ NULO DEVOLVE O LEAD AO BOLO, e e metade do uso real: tirar o dono de quem saiu
    /// de ferias. A data de atribuicao sai junto — dono nulo com data seria um estado sem
    /// significado.</summary>
    [Fact]
    public async Task REDISTRIBUIR_PARA_NINGUEM_DEVOLVE_O_LEAD_AO_BOLO()
    {
        var (db, tx, amb) = await PrepararAsync("red-nulo");
        using var _ = db; using var __ = tx;

        var bruno = await VendedorAsync(db, amb, "bruno");
        var id = await LeadAsync(db, amb, "lead", comConversaEm: Velho, responsavelId: bruno.Id);

        await db.Conversas.IgnoreQueryFilters().Where(v => v.ContatoId == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(v => v.ResponsavelId, bruno.Id)
                .SetProperty(v => v.AtribuidoEm, Velho));
        db.ChangeTracker.Clear();

        var r = await Servico(amb).RedistribuirAsync(
            new RedistribuicaoEmLote([await NegociacaoDeAsync(db, id)], null), default);

        Assert.Equal(1, r.Criados);

        db.ChangeTracker.Clear();
        Assert.Null(await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.ContatoId == id).Select(n => n.ResponsavelId).SingleAsync());
        Assert.Null(await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.ResponsavelId).SingleAsync());

        var conversa = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.ContatoId == id).Select(v => new { v.ResponsavelId, v.AtribuidoEm })
            .SingleAsync();

        Assert.Null(conversa.ResponsavelId);
        Assert.Null(conversa.AtribuidoEm);
    }

    /// <summary>===================== INATIVO ESCONDE O LEAD DE TODO MUNDO =====================
    ///
    /// ⚠️ Quem foi desativado sai da lista de responsaveis que as telas oferecem. Atribuir a ele
    /// nao da erro em lugar nenhum — o lead simplesmente deixa de aparecer na carteira de qualquer
    /// pessoa, e o filtro por responsavel nao tem a opcao para encontra-lo de volta.
    /// ==============================================================================</summary>
    [Fact]
    public async Task ATRIBUIR_A_QUEM_ESTA_INATIVO_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("red-inativo");
        using var _ = db; using var __ = tx;

        var saiu = await VendedorAsync(db, amb, "saiu");
        await db.Usuarios.IgnoreQueryFilters().Where(u => u.Id == saiu.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, StatusUsuario.Inativo));
        db.ChangeTracker.Clear();

        var id = await LeadAsync(db, amb, "lead", comConversaEm: Velho);
        var negociacao = await NegociacaoDeAsync(db, id);

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).RedistribuirAsync(
                new RedistribuicaoEmLote([negociacao], saiu.Id), default));

        db.ChangeTracker.Clear();
        Assert.Null(await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == negociacao).Select(n => n.ResponsavelId).SingleAsync());
    }

    /// <summary>⚠️ QUEM JA E DO ALVO E PULADO, E O `AtribuidoEm` DELE NAO E REESCRITO. A primeira
    /// versao montava a lista de contatos depois do laco, filtrando por "ja e do alvo", e isso
    /// trazia tambem os pulados — reescrever a data mudaria a ordem da caixa de um lead que
    /// ninguem tocou.</summary>
    [Fact]
    public async Task QUEM_JA_E_DO_ALVO_E_PULADO_E_A_DATA_DELE_NAO_MUDA()
    {
        var (db, tx, amb) = await PrepararAsync("red-pulado");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb, "ana");
        var bruno = await VendedorAsync(db, amb, "bruno");

        var jaDela = await LeadAsync(db, amb, "dela", comConversaEm: Velho, responsavelId: ana.Id);
        var doBruno = await LeadAsync(db, amb, "dele", comConversaEm: Velho, responsavelId: bruno.Id);

        await db.Conversas.IgnoreQueryFilters().Where(v => v.ContatoId == jaDela)
            .ExecuteUpdateAsync(u => u
                .SetProperty(v => v.ResponsavelId, ana.Id)
                .SetProperty(v => v.AtribuidoEm, Velho));
        db.ChangeTracker.Clear();

        var r = await Servico(amb).RedistribuirAsync(
            new RedistribuicaoEmLote(
                [await NegociacaoDeAsync(db, jaDela), await NegociacaoDeAsync(db, doBruno)],
                ana.Id),
            default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Pulados);
        Assert.Equal(0, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.Equal(Velho, await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.ContatoId == jaDela).Select(v => v.AtribuidoEm).SingleAsync());
    }

    [Fact]
    public async Task SEM_O_GESTO_REDISTRIBUIR_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("red-sempermissao");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "zeca");
        var ana = await VendedorAsync(db, amb, "ana");
        var id = await LeadAsync(db, amb, "alvo", comConversaEm: Velho, responsavelId: vendedor.Id);
        var negociacao = await NegociacaoDeAsync(db, id);

        amb.Contexto.UsuarioId = vendedor.Id;
        amb.Contexto.Papel = "vendedor";

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).RedistribuirAsync(
                new RedistribuicaoEmLote([negociacao], ana.Id), default));

        db.ChangeTracker.Clear();
        Assert.Equal(vendedor.Id, await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == negociacao).Select(n => n.ResponsavelId).SingleAsync());
    }

    /// <summary>⚠️ RESPONSAVEL DA VIZINHA NAO E "ALGUEM DA EQUIPE". O filtro global recorta a
    /// consulta de status, entao o usuario dela nao volta como ativo — e o pedido inteiro e
    /// recusado, em vez de gravar uma FK para outra empresa.</summary>
    [Fact]
    public async Task ATRIBUIR_A_USUARIO_DE_OUTRA_EMPRESA_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("red-tenant-usuario");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "meu", comConversaEm: Velho);
        var negociacao = await NegociacaoDeAsync(db, id);

        var vizinha = await Semeador.TenantAsync(db, "lpa-red-vizinha");

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).RedistribuirAsync(
                new RedistribuicaoEmLote([negociacao], vizinha.Dono.Id), default));

        db.ChangeTracker.Clear();
        Assert.Null(await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == negociacao).Select(n => n.ResponsavelId).SingleAsync());
    }

    /// <summary>E negociacao da vizinha nao volta da consulta de alvos: entra em `Falhou`.</summary>
    [Fact]
    public async Task NEGOCIACAO_DE_OUTRA_EMPRESA_NAO_E_REDISTRIBUIDA()
    {
        var (db, tx, amb) = await PrepararAsync("red-tenant-neg");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb, "ana");
        var meu = await LeadAsync(db, amb, "meu", comConversaEm: Velho);
        var minha = await NegociacaoDeAsync(db, meu);

        var vizinha = await Semeador.TenantAsync(db, "lpa-red-neg-vizinha");
        await ZerarAsync(db, vizinha.Id);
        var ambVizinha = amb with
        {
            Cenario = vizinha,
            Contexto = new ContextoMutavel
            {
                EmpresaId = vizinha.Id, UsuarioId = vizinha.Dono.Id, Papel = "dono"
            }
        };
        var contatoDela = await LeadAsync(db, ambVizinha, "dela", comConversaEm: Velho);
        var dela = await NegociacaoDeAsync(db, contatoDela);

        var r = await Servico(amb).RedistribuirAsync(
            new RedistribuicaoEmLote([minha, dela], ana.Id), default);

        Assert.Equal(1, r.Criados);
        Assert.Equal(1, r.Falhou);

        db.ChangeTracker.Clear();
        Assert.NotEqual(ana.Id, await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == dela).Select(n => n.ResponsavelId).SingleAsync());
    }

    /// <summary>⚠️ A TRILHA GUARDA O DE/PARA. "Quem mexeu neste lead" sem o valor antigo e o novo
    /// nao responde a pergunta que se faz depois — para QUEM ele foi. Mesmo padrao do
    /// `ServicoVendas.CancelarAsync`, que poe o valor desfeito explicitamente.</summary>
    [Fact]
    public async Task A_TRILHA_REGISTRA_DE_QUEM_PARA_QUEM()
    {
        var (db, tx, amb) = await PrepararAsync("red-trilha");
        using var _ = db; using var __ = tx;

        var bruno = await VendedorAsync(db, amb, "bruno");
        var ana = await VendedorAsync(db, amb, "ana");
        var id = await LeadAsync(db, amb, "lead", comConversaEm: Velho, responsavelId: bruno.Id);

        await Servico(amb).RedistribuirAsync(
            new RedistribuicaoEmLote([await NegociacaoDeAsync(db, id)], ana.Id), default);

        db.ChangeTracker.Clear();

        // ⚠️ LE A TABELA, NAO O COLETOR. `ColetorAuditoria.Consumir()` ESVAZIA, e o interceptor
        // ja consumiu no `SaveChanges` — escrevi este teste contra o coletor primeiro e ele viria
        // vazio. A linha gravada e a prova.
        var evento = Assert.Single(await db.Auditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Entidade == EntidadeAuditada.Contato && a.EntidadeId == id)
            .ToListAsync());

        Assert.Equal(AcaoAuditoria.Atribuiu, evento.Acao);

        // `Alteracoes` e JSON em texto: o de/para tem de estar la dentro, senao "quem mexeu neste
        // lead" nao responde PARA QUEM ele foi.
        Assert.Contains("responsavel", evento.Alteracoes);
        Assert.Contains(bruno.Id.ToString(), evento.Alteracoes);
        Assert.Contains(ana.Id.ToString(), evento.Alteracoes);
    }

    // ==================================================================== o andaime

    /// <summary>⚠️ O `ServicoContatos` E O DE VERDADE, nao um dublê. Reabrir em lote DELEGA a
    /// `AbrirNegociacaoAsync`, e um dublê tornaria verdes exatamente os testes que importam: a
    /// precedencia de funil, a etapa preservada e a recusa por conflito moram la.</summary>
    private static IServicoLeadsParados Servico(Ambiente amb) =>
        new ServicoLeadsParados(
            amb.Db, amb.Contexto, amb.Relogio, amb.Trilha,
            new ServicoContatos(
                amb.Db, amb.Contexto,
                PublicadorDeTeste.Novo(amb.Db, amb.Relogio),
                PublicadorConversoesDeTeste.Novo(amb.Db, amb.Relogio),
                amb.Trilha, amb.Relogio),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServicoLeadsParados>.Instance);

    private sealed record Ambiente(
        NexoraDbContext Db, Cenario Cenario, ContextoMutavel Contexto, TimeProvider Relogio,
        ColetorAuditoria Trilha);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo, FalhaNoComando? falha = null)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(ContatosDbTests.Agora);
        var trilha = new ColetorAuditoria();

        var db = banco.NovoContexto(ctx, relogio, trilha, falha: falha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"lpa-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        await ZerarAsync(db, cenario.Id);

        return (db, tx, new Ambiente(db, cenario, ctx, relogio, trilha));
    }

    private static async Task ZerarAsync(NexoraDbContext db, long empresaId)
    {
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Lembretes.IgnoreQueryFilters().Where(l => l.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Auditoria.IgnoreQueryFilters().Where(a => a.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Contato com negociação aberta e, opcionalmente, uma conversa.
    ///
    /// `criado_em` e `ultima_mensagem_em` são impostos por UPDATE: o `InterceptorAuditoria`
    /// sobrescreve `CriadoEm` em todo INSERT, o que é certo em produção e trabalha contra um teste
    /// que precisa de datas espalhadas pelo calendário.</summary>
    private static async Task<long> LeadAsync(
        NexoraDbContext db, Ambiente amb, string marca,
        DateTime? comConversaEm, DateTime? criadoEm = null, long? responsavelId = null,
        bool comNegocio = true, OrigemLead origem = OrigemLead.Manual, decimal? valor = null)
    {
        var contato = new Contato
        {
            EmpresaId = amb.Cenario.Id,
            Nome = $"Contato {marca}",
            Telefone = $"5584{Random.Shared.NextInt64(900000000, 999999999)}",
            ResponsavelId = responsavelId,
            Origem = origem
        };
        db.Contatos.Add(contato);

        if (comNegocio)
        {
            var negocio = Semeador.Negocio(contato, amb.Cenario.Etapas[0]);
            negocio.ResponsavelId = responsavelId;
            negocio.Valor = valor;
            db.Negociacoes.Add(negocio);
        }

        if (comConversaEm is { } quando)
        {
            db.Conversas.Add(new Conversa
            {
                EmpresaId = amb.Cenario.Id,
                Contato = contato,
                ConexaoId = amb.Cenario.Conexao.Id,
                UltimaMensagemEm = quando
            });
        }

        await db.SaveChangesAsync();

        var nascimento = criadoEm ?? comConversaEm ?? ContatosDbTests.Agora.UtcDateTime;
        await db.Contatos.IgnoreQueryFilters().Where(x => x.Id == contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CriadoEm, nascimento));

        if (comConversaEm is { } c2)
        {
            await db.Conversas.IgnoreQueryFilters().Where(x => x.ContatoId == contato.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UltimaMensagemEm, c2));
        }

        db.ChangeTracker.Clear();
        return contato.Id;
    }

    /// <summary>⚠️ MUDAR O STATUS NAO BASTA, e o banco ensina isso na cara: `ck_negociacoes_valor`
    /// exige `valor > 0` em tudo que nao seja `aberta` ou `perdida`, e `ck_negociacoes_terminal`
    /// proibe `ganha_em` e `perdida_em` preenchidos ao mesmo tempo. Um `ExecuteUpdate` so do
    /// `status` estoura 23514 — foi assim que a primeira versao destes testes caiu.</summary>
    private static async Task MudarStatusAsync(
        NexoraDbContext db, long contatoId, StatusNegociacao status)
    {
        var quando = Velho.AddDays(1);
        var perdida = status == StatusNegociacao.Perdida;

        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contatoId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, status)
                .SetProperty(n => n.Valor, perdida ? null : 1000m)
                .SetProperty(n => n.GanhaEm, perdida ? null : quando)
                .SetProperty(n => n.PerdidaEm, perdida ? quando : null)
                .SetProperty(n => n.ConcluidaEm,
                    status == StatusNegociacao.Concluida ? quando : null)
                .SetProperty(n => n.CanceladaEm,
                    status == StatusNegociacao.Cancelada ? quando : null));

        db.ChangeTracker.Clear();
    }

    /// <summary>===================== O SEGUNDO FUNIL, CRIADO SE FALTAR =====================
    ///
    /// ⚠️ O SEMEADOR CRIA UM FUNIL SO, e dois ajudantes precisavam de um segundo. Cada um resolvia
    /// isso por conta, e cada um errou de um jeito diferente: `MudarFunilAsync` desistia calado (e
    /// o teste de filtro passava com os dois leads no mesmo funil), e `NegocioAbertoAsync` caia de
    /// volta no funil padrao — onde `uq_negociacoes_card_por_funil` recusa a segunda negociacao do
    /// mesmo contato com 23505. Um lugar so, para a correcao nao divergir uma terceira vez.
    /// ================================================================================</summary>
    private static async Task<(long PipelineId, long EtapaId)> SegundoFunilAsync(
        NexoraDbContext db, Ambiente amb)
    {
        var outro = await db.Pipelines.IgnoreQueryFilters()
            .Where(p => p.EmpresaId == amb.Cenario.Id && !p.Padrao)
            .FirstOrDefaultAsync();

        if (outro is null)
        {
            outro = new Pipeline { EmpresaId = amb.Cenario.Id, Nome = "Pos-venda", Ordem = 2 };
            db.Pipelines.Add(outro);
            await db.SaveChangesAsync();

            db.EtapasFunil.Add(new EtapaFunil
            {
                EmpresaId = amb.Cenario.Id, PipelineId = outro.Id,
                Nome = "Entrada", Ordem = 1, Cor = "#2E7A56"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        var etapaId = await db.EtapasFunil.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.PipelineId == outro.Id).Select(e => e.Id).FirstAsync();

        return (outro.Id, etapaId);
    }

    /// <summary>Um segundo negócio ABERTO para o mesmo contato, noutro funil — o caso do cliente
    /// recorrente. `uq_negociacoes_card_por_funil` permite um por funil.</summary>
    private static async Task NegocioAbertoAsync(NexoraDbContext db, Ambiente amb, long contatoId)
    {
        var (pipelineId, etapaId) = await SegundoFunilAsync(db, amb);

        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = contatoId,
            PipelineId = pipelineId,
            EtapaId = etapaId,
            Status = StatusNegociacao.Aberta
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Move a negociacao do contato para OUTRO funil, para o filtro de funil ter o que
    /// recortar. Cria o funil se a empresa so tiver o padrao.</summary>
    private static async Task MudarFunilAsync(NexoraDbContext db, Ambiente amb, long contatoId)
    {
        var (pipelineId, etapaId) = await SegundoFunilAsync(db, amb);

        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contatoId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.PipelineId, pipelineId)
                .SetProperty(n => n.EtapaId, etapaId));

        db.ChangeTracker.Clear();
    }

    /// <summary>O id da negociacao ABERTA do contato. `LeadAsync` cria uma por padrao, e os
    /// testes da etiqueta precisam do id dela — a acao e por negociacao, nao por contato.</summary>
    /// <summary>Ganha DEPOIS da marca. ⚠️ `ck_negociacoes_valor` exige `valor > 0` fora de
    /// `aberta`/`perdida`, e `ck_negociacoes_terminal` proibe `ganha_em` e `perdida_em` juntos —
    /// os dois ja derrubaram a primeira versao destes ajudantes.</summary>
    private static async Task GanharDepoisDaMarcaAsync(NexoraDbContext db, long contatoId)
    {
        // `CriadoEm` da marca e o relogio real, entao "depois" tem de ser depois DELE.
        var marcadaEm = await db.NegociacoesEtiquetas.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Negociacao.ContatoId == contatoId)
            .Select(x => x.CriadoEm).MaxAsync();

        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contatoId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(n => n.Status, StatusNegociacao.Ganha)
                .SetProperty(n => n.Valor, 1000m)
                .SetProperty(n => n.PerdidaEm, (DateTime?)null)
                .SetProperty(n => n.GanhaEm, marcadaEm.AddHours(1)));

        db.ChangeTracker.Clear();
    }

    /// <summary>⚠️ CANCELA SEM APAGAR O `ganha_em`, que e exatamente o que
    /// `ServicoVendas.CancelarAsync` faz de proposito. Zerar o carimbo aqui tornaria o teste da
    /// metrica verde com a regra errada.</summary>
    private static async Task CancelarAsync(NexoraDbContext db, long contatoId)
    {
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contatoId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(n => n.Status, StatusNegociacao.Cancelada)
                .SetProperty(n => n.CanceladaEm, MarcaEm.AddDays(10)));

        db.ChangeTracker.Clear();
    }

    /// <summary>===================== A CARGA TEM DE ESPELHAR A PROPORCAO REAL =====================
    ///
    /// ⚠️ A PRIMEIRA VERSAO INSERIA 4000 PERDAS E SO PERDAS, e o teste falhou com razao: quando
    /// quase toda linha da tabela casa com o filtro, varrer E o plano certo, e o indice parcial
    /// nao tem o que recortar. O teste estava medindo uma tabela que nao existe em lugar nenhum.
    ///
    /// No `nexora_dev` perda e 162 de 1265 — 13%. Aqui sao 400 perdas e 3600 concluidas, que e a
    /// mesma ordem. `concluida` serve de preenchimento porque NAO entra em
    /// `uq_negociacoes_card_por_funil` (so `aberta` e `ganha`), entao milhares delas do mesmo
    /// contato no mesmo funil sao legais — e `ck_negociacoes_valor` exige `valor > 0` nela.
    ///
    /// INSERT por `generate_series`: quatro mil INSERTs pelo EF levariam minutos e mediriam o EF,
    /// nao o banco. Mesmo andaime do `SerieTemporalDbTests`.
    /// ====================================================================================</summary>
    private static async Task CargaDePerdasAsync(
        NexoraDbContext db, Ambiente amb, long contatoId, int perdas, int enchimento)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO negociacoes (
                empresa_id, contato_id, pipeline_id, etapa_id, status,
                perdida_em, motivo_perda, ordem_kanban, criado_em)
            SELECT {0}, {1}, {2}, {3}, 'perdida'::status_negociacao_enum,
                   {4}::timestamptz - ((i) || ' hours')::interval,
                   'carga ' || i, i, {4}::timestamptz
              FROM generate_series(1, {5}) AS i
            """,
            amb.Cenario.Id, contatoId, amb.Cenario.Etapas[0].PipelineId, amb.Cenario.Etapas[0].Id,
            Velho, perdas);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO negociacoes (
                empresa_id, contato_id, pipeline_id, etapa_id, status,
                valor, ganha_em, concluida_em, ordem_kanban, criado_em)
            SELECT {0}, {1}, {2}, {3}, 'concluida'::status_negociacao_enum,
                   100, {4}::timestamptz, {4}::timestamptz, i, {4}::timestamptz
              FROM generate_series(1, {5}) AS i
            """,
            amb.Cenario.Id, contatoId, amb.Cenario.Etapas[0].PipelineId, amb.Cenario.Etapas[0].Id,
            Velho, enchimento);

        db.ChangeTracker.Clear();
    }

    /// <summary>EXPLAIN do MESMO corte que a consulta da aba faz sobre `negociacoes`.</summary>
    private static async Task<string> ExplicarPerdidasAsync(
        NexoraDbContext db, long empresaId, DateTime limite)
    {
        var conexao = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conexao.State != ConnectionState.Open) await conexao.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            EXPLAIN SELECT count(*) FROM negociacoes
             WHERE empresa_id = $1 AND status = 'perdida' AND perdida_em < $2
            """, conexao)
        {
            Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction()
        };
        cmd.Parameters.Add(new() { Value = empresaId });
        cmd.Parameters.Add(new() { Value = limite });

        var linhas = new List<string>();
        await using var leitor = await cmd.ExecuteReaderAsync();
        while (await leitor.ReadAsync()) linhas.Add(leitor.GetString(0));

        return string.Join('\n', linhas);
    }

    /// <summary>A data da perda, forcada. `MudarStatusAsync` carimba `Velho.AddDays(1)`, e os
    /// testes do EIXO de tempo precisam dissociar a data da perda da data da conversa.</summary>
    private static async Task PerdidaEmAsync(NexoraDbContext db, long contatoId, DateTime quando)
    {
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contatoId)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.PerdidaEm, quando));

        db.ChangeTracker.Clear();
    }

    private static async Task<long> NegociacaoDeAsync(NexoraDbContext db, long contatoId) =>
        await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.ContatoId == contatoId && n.Status == StatusNegociacao.Aberta)
            .Select(n => n.Id).SingleAsync();

    private static async Task<long> EtiquetaAsync(NexoraDbContext db, Ambiente amb, string nome)
    {
        var e = new Etiqueta { EmpresaId = amb.Cenario.Id, Nome = nome, Cor = "#2E7A56" };
        db.Etiquetas.Add(e);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return e.Id;
    }

    /// <summary>⚠️ RECEBE A NEGOCIACAO, NAO O CONTATO. Antes pegava a primeira negociacao do
    /// contato, e isso servia enquanto cada lead tinha uma — mas a etiqueta em lote existe
    /// justamente para marcar UM dos dois negocios da mesma pessoa, e o ajudante antigo nao tinha
    /// como dizer qual.</summary>
    private static async Task MarcarNegociacaoAsync(
        NexoraDbContext db, Ambiente amb, long negociacaoId, long etiquetaId)
    {
        db.NegociacoesEtiquetas.Add(new NegociacaoEtiqueta
        {
            EmpresaId = amb.Cenario.Id, NegociacaoId = negociacaoId, EtiquetaId = etiquetaId
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>A MESMA etiqueta, mas no CONTATO. Serve para provar que a consulta olha a tabela
    /// certa: marcar aqui nao pode fazer o lead entrar no filtro de etiqueta de negociacao.</summary>
    private static async Task MarcarContatoAsync(
        NexoraDbContext db, Ambiente amb, long contatoId, long etiquetaId)
    {
        db.ContatosEtiquetas.Add(new ContatoEtiqueta
        {
            EmpresaId = amb.Cenario.Id, ContatoId = contatoId, EtiquetaId = etiquetaId
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<Usuario> VendedorAsync(NexoraDbContext db, Ambiente amb, string marca)
    {
        var u = new Usuario
        {
            EmpresaId = amb.Cenario.Id,
            Nome = $"Vendedor {marca}",
            Email = $"{marca}-{Guid.NewGuid():N}@exemplo.com",
            SenhaHash = Nexora.Core.Seguranca.HashSenha.Gerar("senha-de-teste-123"),
            Papel = PapelUsuario.Vendedor,
            Status = StatusUsuario.Ativo
        };
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return u;
    }
}
