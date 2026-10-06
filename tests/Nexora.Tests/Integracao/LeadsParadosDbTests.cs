using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
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

    private static FiltroLeadsParados Filtro(int dias = 30, long? responsavel = null) =>
        new(dias, responsavel, 1, 50);

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

    // ==================================================================== o andaime

    private static IServicoLeadsParados Servico(Ambiente amb) =>
        new ServicoLeadsParados(amb.Db, amb.Contexto, amb.Relogio, amb.Trilha);

    private sealed record Ambiente(
        NexoraDbContext Db, Cenario Cenario, ContextoMutavel Contexto, TimeProvider Relogio,
        ColetorAuditoria Trilha);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(ContatosDbTests.Agora);
        var trilha = new ColetorAuditoria();

        var db = banco.NovoContexto(ctx, relogio, trilha);
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
