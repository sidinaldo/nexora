using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== NPS-1 ETAPA 3b — O RELATORIO DA PESQUISA =====================
///
/// O eixo e `data_envio` (a coorte). Cada teste aqui guarda uma regra que, quebrada, produziria
/// um numero PLAUSIVEL e errado — o tipo de defeito que ninguem percebe olhando a tela.
///
/// Relogio em 07/10: setembro e um mes FECHADO, e o periodo anterior dele e agosto inteiro.
/// ======================================================================================</summary>
[Collection("banco")]
public class RelatorioNpsDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 7, 13, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly SetembroDe = new(2026, 9, 1);
    private static readonly DateOnly SetembroAte = new(2026, 9, 30);

    /// <summary>Meio-dia de um dia de setembro, em UTC — longe das bordas do fuso.</summary>
    private static DateTime Setembro(int dia) => new(2026, 9, dia, 15, 0, 0, DateTimeKind.Utc);
    private static DateTime Agosto(int dia) => new(2026, 8, dia, 15, 0, 0, DateTimeKind.Utc);

    private static FiltroRelatorio Filtro(DateOnly de, DateOnly ate) =>
        new(de, ate, AgrupamentoSerie.Dia);

    // ==================================================================== a conta

    /// <summary>===================== NPS SOBRE AS RESPONDIDAS, TAXA SOBRE AS ENVIADAS =====================
    ///
    /// Sete enviadas: 9 e 10 (promotores), 7 (neutro), 3 (detrator), uma expirada, uma aberta e uma
    /// cancelada depois do envio.
    ///
    ///   NPS  = (2 − 1) / 4 respondidas = 25,0   ⚠️ e NAO (2 − 1) / 7 = 14,3
    ///   taxa = 4 / 7 = 57,1
    ///
    /// Dividir o NPS pelas enviadas daria um numero que CAI quando mais gente e perguntada — e ele
    /// pareceria certo.
    /// ==========================================================================================</summary>
    [Fact]
    public async Task NPS_E_SOBRE_AS_RESPONDIDAS_E_A_TAXA_SOBRE_AS_ENVIADAS()
    {
        var (db, tx, amb) = await PrepararAsync("conta");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9, Setembro(2));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(3));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 7, Setembro(4));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 3, Setembro(5));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Expirada, null, Setembro(6));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Enviada, null, Setembro(7));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Cancelada, null, Setembro(8));

        var r = await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default);
        var t = r.Totais;

        Assert.Equal(7, t.Enviadas);
        Assert.Equal(4, t.Respondidas);
        Assert.Equal(1, t.Expiradas);
        Assert.Equal(1, t.Canceladas);
        Assert.Equal(1, t.AindaAbertas);
        Assert.Equal(2, t.Promotores);
        Assert.Equal(1, t.Neutros);
        Assert.Equal(1, t.Detratores);

        Assert.Equal(25.0, t.Nps);
        Assert.Equal(57.1, t.TaxaDeResposta);
    }

    /// <summary>⚠️ AS BORDAS DAS FAIXAS: 6 e detrator, 7 e neutro, 8 e neutro, 9 e promotor. Errar
    /// um `<` por `<=` move uma nota inteira de faixa, e o NPS muda sem nada parecer errado.</summary>
    [Fact]
    public async Task AS_BORDAS_DAS_FAIXAS_6_7_8_9()
    {
        var (db, tx, amb) = await PrepararAsync("bordas");
        using var _ = db; using var __ = tx;

        foreach (short nota in new short[] { 6, 7, 8, 9 })
            await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, nota, Setembro(10));

        var t = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(1, t.Detratores);
        Assert.Equal(2, t.Neutros);
        Assert.Equal(1, t.Promotores);
    }

    /// <summary>===================== `PossivelNota` E SUSPEITA, NAO RESULTADO =====================
    ///
    /// A nota esta preenchida, e ninguem a confirmou. Ela conta como ENVIADA e como AINDA ABERTA
    /// (o vendedor ainda vai decidir), e em mais nada: um 0 suspeito virando detrator derrubaria o
    /// NPS por uma mensagem que podia ser "0 problemas, obrigado".
    /// ======================================================================================</summary>
    [Fact]
    public async Task POSSIVEL_NOTA_NAO_ENTRA_NO_NPS_NEM_NA_DISTRIBUICAO()
    {
        var (db, tx, amb) = await PrepararAsync("possivel");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.PossivelNota, 0, Setembro(2));

        var r = await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default);

        Assert.Equal(1, r.Totais.Enviadas);
        Assert.Equal(1, r.Totais.AindaAbertas);
        Assert.Equal(0, r.Totais.Respondidas);
        Assert.Equal(0, r.Totais.Detratores);
        Assert.Null(r.Totais.Nps);
        Assert.All(r.Distribuicao, f => Assert.Equal(0, f.Quantas));
    }

    /// <summary>Pesquisa `agendada` ainda nao saiu e nao tem `data_envio` — e a coorte e do que
    /// CHEGOU ao cliente. Contá-la como enviada derrubaria a taxa de resposta com perguntas que
    /// ninguem recebeu.</summary>
    [Fact]
    public async Task PESQUISA_QUE_NAO_SAIU_NAO_ENTRA_EM_NADA()
    {
        var (db, tx, amb) = await PrepararAsync("agendada");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Agendada, null, envio: null);
        await PesquisaAsync(db, amb, StatusPesquisaNps.Cancelada, null, envio: null);

        var t = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(0, t.Enviadas);
        Assert.Equal(0, t.Canceladas);
        Assert.Null(t.TaxaDeResposta);
    }

    /// <summary>Zero e um NPS REAL — tantos promotores quanto detratores. "Sem resposta" e outra
    /// coisa, e a tela escreve outra frase.</summary>
    [Fact]
    public async Task SEM_RESPOSTA_O_NPS_E_NULO_E_NAO_ZERO()
    {
        var (db, tx, amb) = await PrepararAsync("nulo");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Expirada, null, Setembro(2));

        var t = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(1, t.Enviadas);
        Assert.Null(t.Nps);
        Assert.Equal(0.0, t.TaxaDeResposta);
    }

    // ==================================================================== o corte

    /// <summary>===================== O MES COMECA NO FUSO DA EMPRESA =====================
    ///
    /// 01/09 02:00 UTC e 31/08 23:00 em Brasilia — AGOSTO. 01/09 03:00 UTC e meia-noite em
    /// Brasilia — SETEMBRO. Cortar em UTC poria tres horas de agosto em setembro todo mes.
    /// ====================================================================</summary>
    [Fact]
    public async Task O_CORTE_E_NA_MEIA_NOITE_DA_EMPRESA_E_NAO_DO_SERVIDOR()
    {
        var (db, tx, amb) = await PrepararAsync("fuso");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10,
            new DateTime(2026, 9, 1, 2, 0, 0, DateTimeKind.Utc));   // 31/08 23h local
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 0,
            new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc));   // 01/09 00h local

        var t = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(1, t.Enviadas);
        Assert.Equal(1, t.Detratores);
        Assert.Equal(0, t.Promotores);
    }

    /// <summary>O ultimo dia entra INTEIRO: 30/09 23:59 local e 01/10 02:59 UTC. Corte com `<=` no
    /// fim perderia o dia; corte sem o `+1d` tambem.</summary>
    [Fact]
    public async Task O_ULTIMO_DIA_DO_PERIODO_ENTRA_INTEIRO()
    {
        var (db, tx, amb) = await PrepararAsync("ultimo-dia");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9,
            new DateTime(2026, 10, 1, 2, 59, 0, DateTimeKind.Utc));   // 30/09 23:59 local
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9,
            new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc));    // 01/10 00:00 local

        var t = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(1, t.Enviadas);
    }

    // ==================================================================== a distribuicao

    /// <summary>As ONZE barras, sempre, de 0 a 10 e nesta ordem — inclusive as vazias. Omitir a
    /// nota que ninguem deu faria as barras mudarem de lugar entre dois periodos.</summary>
    [Fact]
    public async Task A_DISTRIBUICAO_TEM_AS_ONZE_NOTAS_INCLUSIVE_AS_VAZIAS()
    {
        var (db, tx, amb) = await PrepararAsync("distribuicao");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(2));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(3));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 4, Setembro(4));

        var d = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Distribuicao;

        Assert.Equal(Enumerable.Range(0, 11).Select(n => (short)n), d.Select(f => f.Nota));
        Assert.Equal(2, d[10].Quantas);
        Assert.Equal(1, d[4].Quantas);
        Assert.Equal(3, d.Sum(f => f.Quantas));
    }

    // ==================================================================== quem ve o que

    /// <summary>===================== O VENDEDOR VE O NPS DAS VENDAS DELE =====================
    ///
    /// ⚠️ E O FILTRO FORJADO E DESCARTADO: o vendedor pede o responsavel da Ana e recebe os dele.
    /// O gestor, com o MESMO pedido sem filtro, ve os dois — senao o teste passaria com uma regra
    /// que nao devolve nada para ninguem.
    /// ==================================================================</summary>
    [Fact]
    public async Task VENDEDOR_SO_VE_AS_PESQUISAS_DAS_VENDAS_DELE_nem_forjando_o_filtro()
    {
        var (db, tx, amb) = await PrepararAsync("vendedor");
        using var _ = db; using var __ = tx;

        var ana = amb.Cenario.Dono;
        var bruno = await VendedorAsync(db, amb, "bruno");

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(2), ana.Id);
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(3), ana.Id);
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 2, Setembro(4), bruno.Id);

        amb.Contexto.UsuarioId = bruno.Id;
        amb.Contexto.Papel = "vendedor";

        var forjado = Filtro(SetembroDe, SetembroAte) with { ResponsavelId = ana.Id };
        var doBruno = (await amb.Servico.LerAsync(forjado, default)).Totais;

        Assert.Equal(1, doBruno.Enviadas);
        Assert.Equal(1, doBruno.Detratores);
        Assert.Equal(0, doBruno.Promotores);

        amb.Contexto.UsuarioId = ana.Id;
        amb.Contexto.Papel = "gestor";
        var doGestor = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(3, doGestor.Enviadas);

        // E o gestor PODE pedir um so.
        var soDoBruno = (await amb.Servico.LerAsync(
            Filtro(SetembroDe, SetembroAte) with { ResponsavelId = bruno.Id }, default)).Totais;
        Assert.Equal(1, soDoBruno.Enviadas);
    }

    /// <summary>⚠️ SQL CRU NAO TEM FILTRO GLOBAL. O `empresa_id = $1` foi escrito a mao, e e so ele
    /// que segura a pesquisa da outra empresa fora deste numero.</summary>
    [Fact]
    public async Task PESQUISA_DE_OUTRA_EMPRESA_NAO_ENTRA()
    {
        var (db, tx, amb) = await PrepararAsync("isolamento");
        using var _ = db; using var __ = tx;

        var outra = await Semeador.TenantAsync(db, "nps-rel-outra");
        await PesquisaDeAsync(db, outra, StatusPesquisaNps.Respondida, 0, Setembro(2), outra.Dono.Id);

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(2));

        var r = await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default);

        Assert.Equal(1, r.Totais.Enviadas);
        Assert.Equal(0, r.Totais.Detratores);
        Assert.Equal(0, r.Distribuicao[0].Quantas);
    }

    // ==================================================================== a comparacao

    /// <summary>Setembro fechado compara com AGOSTO INTEIRO — a regra e do `PeriodoAnterior`, a
    /// mesma do relatorio de vendas. Agosto: 1 promotor e 1 detrator (NPS 0). Setembro: 2
    /// promotores (NPS 100).
    ///
    /// ⚠️ O NPS ZERO DE AGOSTO E UM ZERO DE VERDADE, e a variacao absoluta e 100 pontos.</summary>
    [Fact]
    public async Task COMPARA_COM_O_MES_ANTERIOR_INTEIRO()
    {
        var (db, tx, amb) = await PrepararAsync("comparacao");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Agosto(1));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 0, Agosto(31));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9, Setembro(10));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(20));

        var r = await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default);
        var c = Assert.IsType<ComparativoNps>(r.Comparativo);

        Assert.Equal(new DateOnly(2026, 8, 1), c.Nps.AnteriorDe);
        Assert.Equal(new DateOnly(2026, 8, 31), c.Nps.AnteriorAte);
        Assert.False(c.EmAndamento);

        Assert.Equal(100m, c.Nps.Atual);
        Assert.Equal(0m, c.Nps.Anterior);
        Assert.Equal(100m, c.Nps.VariacaoAbsoluta);

        Assert.Equal(2m, c.Respondidas.Atual);
        Assert.Equal(2m, c.Respondidas.Anterior);
        Assert.Equal(2m, c.Promotores.Atual);
        Assert.Equal(1m, c.Promotores.Anterior);

        // A distribuicao e SO do periodo atual: o 0 de agosto nao aparece.
        Assert.Equal(0, r.Distribuicao[0].Quantas);
    }

    [Fact]
    public async Task DATA_FINAL_ANTES_DA_INICIAL_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("invertida");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Servico.LerAsync(Filtro(SetembroAte, SetembroDe), default));
    }

    // ==================================================================== a lista (3.3)

    private static FiltroRespostasNps Todas => new();

    /// <summary>===================== A LISTA BATE COM O CARTAO =====================
    ///
    /// E o criterio de aceite "relatorio e lista batem com os dados", escrito como teste: mesmo
    /// periodo, mesmo recorte — a lista sem faixa tem `Respondidas` linhas, e cada faixa tem o
    /// numero do cartao dela. Pesquisa nao respondida (expirada, aberta, em duvida) nao e resposta
    /// e nao entra.
    /// =================================================================</summary>
    /// <summary>A página ALÉM DO FIM traz o total certo, e não zero (AUD-XX, B5). O total vinha de
    /// `COUNT(*) OVER ()`, lido de dentro das linhas — e essa página não tem linha nenhuma.</summary>
    [Fact]
    public async Task AS_RESPOSTAS_NA_PAGINA_ALEM_DO_FIM_TRAZEM_O_TOTAL_CERTO()
    {
        var (db, tx, amb) = await PrepararAsync("respostas-alem");
        using var _ = db; using var __ = tx;

        foreach (short nota in new short[] { 10, 7, 2 })
            await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, nota, Setembro(5));

        var alemDoFim = await amb.Servico.RespostasAsync(
            Filtro(SetembroDe, SetembroAte), new FiltroRespostasNps(null), 5, 1, default);

        Assert.Empty(alemDoFim.Itens);
        Assert.Equal(3, alemDoFim.Total);
    }

    [Fact]
    public async Task A_LISTA_BATE_COM_O_CARTAO_EM_CADA_FAIXA()
    {
        var (db, tx, amb) = await PrepararAsync("lista-bate");
        using var _ = db; using var __ = tx;

        foreach (short nota in new short[] { 10, 9, 9, 8, 7, 6, 2, 0 })
            await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, nota, Setembro(5));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Expirada, null, Setembro(6));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Enviada, null, Setembro(7));
        await PesquisaAsync(db, amb, StatusPesquisaNps.PossivelNota, 3, Setembro(8));
        // Fora do periodo: respondida em agosto nao entra em setembro, nem no cartao nem na lista.
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Agosto(20));

        var periodo = Filtro(SetembroDe, SetembroAte);
        var t = (await amb.Servico.LerAsync(periodo, default)).Totais;

        async Task<int> Quantas(FaixaNps? faixa) =>
            (await amb.Servico.RespostasAsync(periodo, new FiltroRespostasNps(faixa), 1, 50, default)).Total;

        Assert.Equal(t.Respondidas, await Quantas(null));
        Assert.Equal(t.Promotores, await Quantas(FaixaNps.Promotor));
        Assert.Equal(t.Neutros, await Quantas(FaixaNps.Neutro));
        Assert.Equal(t.Detratores, await Quantas(FaixaNps.Detrator));

        // E os numeros nao sao zero por acidente: 3 promotores, 2 neutros, 3 detratores.
        Assert.Equal((8, 3, 2, 3), (t.Respondidas, t.Promotores, t.Neutros, t.Detratores));
    }

    [Fact]
    public async Task A_LINHA_TRAZ_CLIENTE_NOTA_COMENTARIO_E_RESPONSAVEL()
    {
        var (db, tx, amb) = await PrepararAsync("lista-linha");
        using var _ = db; using var __ = tx;

        var (cliente, _) = await ClienteAsync(db, amb, "maria");
        var id = await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 4, Setembro(3), contato: cliente);
        await db.PesquisasNps.IgnoreQueryFilters().Where(p => p.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Comentario, "demorou"));

        var linha = Assert.Single((await amb.Servico.RespostasAsync(
            Filtro(SetembroDe, SetembroAte), Todas, 1, 20, default)).Itens);

        Assert.Equal(id, linha.PesquisaId);
        Assert.Equal(cliente.Id, linha.ContatoId);
        Assert.Equal("Cliente maria", linha.Cliente);
        Assert.Equal((short)4, linha.Nota);
        Assert.Equal(Setembro(3).AddHours(2), linha.DataResposta);
        Assert.Equal("demorou", linha.Comentario);
        Assert.Equal(amb.Cenario.Dono.Id, linha.ResponsavelId);
        Assert.Equal(amb.Cenario.Dono.Nome, linha.Responsavel);
        Assert.Equal(CompraPadrao, linha.UltimaCompraEm);
        Assert.Null(linha.ComprouDeNovoEm);
    }

    /// <summary>===================== "COMPROU DE NOVO" E A PRIMEIRA COMPRA DEPOIS =====================
    ///
    /// ⚠️ A CANCELADA NAO CONTA, e e o caso que importa: ela guarda o `ganha_em`. Cliente avaliou a
    /// compra de 20/07, fechou outra em 10/08 e cancelou, e fechou de verdade em 15/09. "Comprou de
    /// novo" e 15/09 — nao 10/08.
    /// ==========================================================================================</summary>
    [Fact]
    public async Task COMPROU_DE_NOVO_E_A_PRIMEIRA_COMPRA_DEPOIS_E_A_CANCELADA_NAO_CONTA()
    {
        var (db, tx, amb) = await PrepararAsync("recompra");
        using var _ = db; using var __ = tx;

        var (voltou, _) = await ClienteAsync(db, amb, "voltou");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(2), contato: voltou);
        await CompraAsync(db, amb, voltou, new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc),
            StatusNegociacao.Cancelada);
        var setembro15 = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        await CompraAsync(db, amb, voltou, setembro15, StatusNegociacao.Concluida);

        var (desistiu, _) = await ClienteAsync(db, amb, "desistiu");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(3), contato: desistiu);
        await CompraAsync(db, amb, desistiu, new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc),
            StatusNegociacao.Cancelada);

        var periodo = Filtro(SetembroDe, SetembroAte);
        var linhas = (await amb.Servico.RespostasAsync(periodo, Todas, 1, 20, default)).Itens;

        var doVoltou = linhas.Single(l => l.ContatoId == voltou.Id);
        Assert.Equal(setembro15, doVoltou.ComprouDeNovoEm);
        Assert.Equal(setembro15, doVoltou.UltimaCompraEm);

        var doDesistiu = linhas.Single(l => l.ContatoId == desistiu.Id);
        Assert.Null(doDesistiu.ComprouDeNovoEm);
        Assert.Equal(CompraPadrao, doDesistiu.UltimaCompraEm);

        // O filtro "comprou de novo" separa os dois.
        var sim = await amb.Servico.RespostasAsync(periodo, new FiltroRespostasNps(ComprouDeNovo: true), 1, 20, default);
        var nao = await amb.Servico.RespostasAsync(periodo, new FiltroRespostasNps(ComprouDeNovo: false), 1, 20, default);
        Assert.Equal(voltou.Id, Assert.Single(sim.Itens).ContatoId);
        Assert.Equal(desistiu.Id, Assert.Single(nao.Itens).ContatoId);
    }

    // ==================================================================== os atalhos (3.4)

    /// <summary>===================== PROMOTORES QUE NAO VOLTARAM =====================
    ///
    /// Relogio em 07/10, corte de 60 dias: compra antes de 08/08.
    ///
    ///   sumido          nota 10, comprou em 20/07, nunca mais                ENTRA
    ///   voltou_e_sumiu  nota 9,  comprou em 01/06, de novo em 15/07, e so    ENTRA
    ///   voltou          nota 9,  comprou em 20/07 e de novo em 20/09         fora (17 dias)
    ///   recente         nota 10, comprou em 01/09                            fora (36 dias)
    ///   neutro          nota 8,  comprou em 20/07, nunca mais                fora (nao e promotor)
    ///
    /// ⚠️ E O "SUMIDO" RESPONDEU EM JULHO, fora do periodo da barra (setembro): o atalho ignora a
    /// barra. Sem isso ele seria vazio por construcao.
    ///
    /// ⚠️ O "VOLTOU_E_SUMIU" E O CASO QUE UMA VERSAO ANTERIOR EXCLUIA, exigindo "nunca comprou de
    /// novo". O pedido diz "sem nova venda ha X dias", e ele nao compra ha 84.
    /// =================================================================</summary>
    [Fact]
    public async Task PROMOTORES_QUE_NAO_VOLTARAM_IGNORAM_O_PERIODO_E_EXIGEM_O_SUMICO()
    {
        var (db, tx, amb) = await PrepararAsync("promotores");
        using var _ = db; using var __ = tx;

        var (sumido, _) = await ClienteAsync(db, amb, "sumido");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10,
            new DateTime(2026, 7, 29, 15, 0, 0, DateTimeKind.Utc), contato: sumido);

        var (voltou, _) = await ClienteAsync(db, amb, "voltou");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9, Setembro(2), contato: voltou);
        await CompraAsync(db, amb, voltou, new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc),
            StatusNegociacao.Concluida);

        var (recente, _) = await ClienteAsync(db, amb, "recente");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(12), contato: recente,
            ganhaEm: new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));

        var (neutro, _) = await ClienteAsync(db, amb, "neutro");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 8, Setembro(2), contato: neutro);

        var (voltouESumiu, _) = await ClienteAsync(db, amb, "voltou-e-sumiu");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9,
            new DateTime(2026, 6, 10, 15, 0, 0, DateTimeKind.Utc), contato: voltouESumiu,
            ganhaEm: new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        await CompraAsync(db, amb, voltouESumiu, new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
            StatusNegociacao.Concluida);

        var esperados = new[] { sumido.Id, voltouESumiu.Id }.Order();

        var r = await amb.Servico.RespostasAsync(
            Filtro(SetembroDe, SetembroAte),
            new FiltroRespostasNps(Atalho: AtalhoRespostas.PromotoresQueNaoVoltaram), 1, 20, default);

        Assert.Equal(esperados, r.Itens.Select(l => l.ContatoId).Order());

        // ⚠️ O ATALHO VENCE O "COMPROU DE NOVO" QUE A TELA MANDAR: com "nao", o voltou_e_sumiu
        // sairia por ter voltado uma vez — e ele e da lista.
        var comFiltro = await amb.Servico.RespostasAsync(
            Filtro(SetembroDe, SetembroAte),
            new FiltroRespostasNps(ComprouDeNovo: false, Atalho: AtalhoRespostas.PromotoresQueNaoVoltaram),
            1, 20, default);
        Assert.Equal(esperados, comFiltro.Itens.Select(l => l.ContatoId).Order());

        // O corte de dias e do cliente: com 30, o "recente" (36 dias) entra tambem.
        var com30 = await amb.Servico.RespostasAsync(
            Filtro(SetembroDe, SetembroAte),
            new FiltroRespostasNps(Atalho: AtalhoRespostas.PromotoresQueNaoVoltaram, DiasSemCompra: 30),
            1, 20, default);
        Assert.Equal(new[] { recente.Id, sumido.Id, voltouESumiu.Id }.Order(),
            com30.Itens.Select(l => l.ContatoId).Order());
    }

    /// <summary>===================== DETRATORES SEM RETORNO =====================
    ///
    ///   esquecido   nota 2, ninguem escreveu depois                        ENTRA
    ///   atendido    nota 3, o vendedor escreveu depois da nota             fora
    ///   so_robo     nota 1, so o agradecimento AUTOMATICO depois da nota   ENTRA
    ///   antes       nota 0, o vendedor escreveu ANTES da nota, nao depois  ENTRA
    ///   promotor    nota 9, ninguem escreveu                               fora (nao e detrator)
    ///
    /// ⚠️ O "SO_ROBO" E A RAZAO DO `origem = 'humana'`: o robo agradecer nao e alguem ter falado com
    /// o cliente insatisfeito.
    /// ===============================================================</summary>
    [Fact]
    public async Task DETRATORES_SEM_RETORNO_SO_SAEM_COM_MENSAGEM_HUMANA_DEPOIS_DA_NOTA()
    {
        var (db, tx, amb) = await PrepararAsync("detratores");
        using var _ = db; using var __ = tx;

        // A resposta chega duas horas depois do envio: 05/09 17h UTC.
        var envio = Setembro(5);
        var resposta = envio.AddHours(2);

        var (esquecido, _) = await ClienteAsync(db, amb, "esquecido");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 2, envio, contato: esquecido);

        var (atendido, cAtendido) = await ClienteAsync(db, amb, "atendido");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 3, envio, contato: atendido);
        await SaidaAsync(db, amb, cAtendido, resposta.AddHours(1), OrigemMensagem.Humana);

        var (soRobo, cSoRobo) = await ClienteAsync(db, amb, "so-robo");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 1, envio, contato: soRobo);
        await SaidaAsync(db, amb, cSoRobo, resposta.AddMinutes(1), OrigemMensagem.Automatica);

        var (antes, cAntes) = await ClienteAsync(db, amb, "antes");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 0, envio, contato: antes);
        await SaidaAsync(db, amb, cAntes, resposta.AddHours(-1), OrigemMensagem.Humana);

        var (promotor, _) = await ClienteAsync(db, amb, "promotor");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9, envio, contato: promotor);

        var esperados = new[] { esquecido.Id, soRobo.Id, antes.Id }.Order();

        var r = await amb.Servico.RespostasAsync(
            Filtro(SetembroDe, SetembroAte),
            new FiltroRespostasNps(Atalho: AtalhoRespostas.DetratoresSemRetorno), 1, 20, default);

        Assert.Equal(esperados, r.Itens.Select(l => l.ContatoId).Order());

        // O atalho vence o "comprou de novo" da tela: nenhum deles voltou, e "sim" nao os esconde.
        var comFiltro = await amb.Servico.RespostasAsync(
            Filtro(SetembroDe, SetembroAte),
            new FiltroRespostasNps(ComprouDeNovo: true, Atalho: AtalhoRespostas.DetratoresSemRetorno),
            1, 20, default);
        Assert.Equal(esperados, comFiltro.Itens.Select(l => l.ContatoId).Order());
    }

    /// <summary>O atalho VENCE o filtro: escolher "detratores sem retorno" com "faixa: promotor" nao
    /// devolve vazio sem explicar — devolve os detratores.</summary>
    [Fact]
    public async Task O_ATALHO_VENCE_A_FAIXA_ESCOLHIDA()
    {
        var (db, tx, amb) = await PrepararAsync("atalho-vence");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 2, Setembro(5));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(5));

        var r = await amb.Servico.RespostasAsync(
            Filtro(SetembroDe, SetembroAte),
            new FiltroRespostasNps(FaixaNps.Promotor, Atalho: AtalhoRespostas.DetratoresSemRetorno),
            1, 20, default);

        Assert.Equal((short)2, Assert.Single(r.Itens).Nota);
    }

    // ==================================================================== quem ve o que, na lista

    [Fact]
    public async Task NA_LISTA_O_VENDEDOR_TAMBEM_SO_VE_AS_DELE_E_OUTRA_EMPRESA_NAO_ENTRA()
    {
        var (db, tx, amb) = await PrepararAsync("lista-recorte");
        using var _ = db; using var __ = tx;

        var bruno = await VendedorAsync(db, amb, "bruno");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(2), amb.Cenario.Dono.Id);
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 3, Setembro(3), bruno.Id);

        var outra = await Semeador.TenantAsync(db, "nps-lista-outra");
        await PesquisaDeAsync(db, outra, StatusPesquisaNps.Respondida, 0, Setembro(2), outra.Dono.Id);

        // O dono ve as duas da empresa dele, e nenhuma da outra.
        var doDono = await amb.Servico.RespostasAsync(Filtro(SetembroDe, SetembroAte), Todas, 1, 20, default);
        Assert.Equal(2, doDono.Total);

        amb.Contexto.UsuarioId = bruno.Id;
        amb.Contexto.Papel = "vendedor";

        var forjado = Filtro(SetembroDe, SetembroAte) with { ResponsavelId = amb.Cenario.Dono.Id };
        var doBruno = await amb.Servico.RespostasAsync(forjado, Todas, 1, 20, default);

        Assert.Equal((short)3, Assert.Single(doBruno.Itens).Nota);
    }

    [Fact]
    public async Task A_LISTA_PAGINA_E_TRAZ_O_TOTAL_DA_CONSULTA_INTEIRA()
    {
        var (db, tx, amb) = await PrepararAsync("lista-pagina");
        using var _ = db; using var __ = tx;

        for (var dia = 1; dia <= 5; dia++)
            await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(dia));

        var p2 = await amb.Servico.RespostasAsync(Filtro(SetembroDe, SetembroAte), Todas, 2, 2, default);

        Assert.Equal(5, p2.Total);
        Assert.Equal(2, p2.Itens.Count);
        // Mais recente primeiro: a pagina 2 tem os dias 3 e 2.
        Assert.Equal(new[] { Setembro(3).AddHours(2), Setembro(2).AddHours(2) },
            p2.Itens.Select(l => l.DataResposta));
    }

    [Fact]
    public async Task DIAS_SEM_COMPRA_FORA_DA_FAIXA_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("dias-invalidos");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.RespostasAsync(
            Filtro(SetembroDe, SetembroAte), new FiltroRespostasNps(DiasSemCompra: 0), 1, 20, default));
    }

    // ==================================================================== infraestrutura

    private sealed record Ambiente(Cenario Cenario, ContextoMutavel Contexto, ServicoRelatorioNps Servico);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(Agora);
        var db = banco.NovoContexto(ctx, relogio, new Nexora.Core.Auditoria.ColetorAuditoria());
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"nps-rel-{sufixo}");

        // O FUSO EXPLICITO: o teste do corte depende dele, e nao do padrao de quem semeou.
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.FusoHorario, "America/Sao_Paulo"));

        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new Ambiente(cenario, ctx, new ServicoRelatorioNps(db, ctx, relogio)));
    }

    private static Task<long> PesquisaAsync(
        NexoraDbContext db, Ambiente amb, StatusPesquisaNps status, short? nota, DateTime? envio,
        long? responsavelId = null, Contato? contato = null, DateTime? ganhaEm = null) =>
        PesquisaDeAsync(db, amb.Cenario, status, nota, envio, responsavelId ?? amb.Cenario.Dono.Id,
            contato, ganhaEm);

    /// <summary>A compra avaliada, quando o teste nao diz outra: 20/07.</summary>
    private static readonly DateTime CompraPadrao = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Uma pesquisa = uma venda concluida propria (`uq_pesquisas_nps_negociacao`). O
    /// responsavel mora na NEGOCIACAO, que e onde o relatorio o le. A resposta chega DUAS HORAS
    /// depois do envio — o teste de "sem retorno" conta com isso.</summary>
    private static async Task<long> PesquisaDeAsync(
        NexoraDbContext db, Cenario cenario, StatusPesquisaNps status, short? nota, DateTime? envio,
        long responsavelId, Contato? contato = null, DateTime? ganhaEm = null)
    {
        var etapa = cenario.Etapas[0];
        var quem = contato ?? cenario.Contato;
        var ganha = ganhaEm ?? CompraPadrao;

        var negocio = new Negociacao
        {
            EmpresaId = cenario.Id,
            ContatoId = quem.Id,
            PipelineId = etapa.PipelineId,
            EtapaId = etapa.Id,
            Status = StatusNegociacao.Concluida,
            Valor = 1000m,
            ResponsavelId = responsavelId,
            GanhaEm = ganha,
            ConcluidaEm = ganha.AddDays(5)
        };
        db.Negociacoes.Add(negocio);
        await db.SaveChangesAsync();

        var respondida = status == StatusPesquisaNps.Respondida;

        var pesquisa = new PesquisaNps
        {
            EmpresaId = cenario.Id,
            NegociacaoId = negocio.Id,
            ContatoId = quem.Id,
            Status = status,
            Nota = nota,
            DataAgendada = DateOnly.FromDateTime(ganha.AddDays(8)),
            DataLimite = new DateOnly(2026, 12, 31),
            DataEnvio = envio,
            DataResposta = respondida ? envio?.AddHours(2) : null
        };
        db.PesquisasNps.Add(pesquisa);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return pesquisa.Id;
    }

    /// <summary>Um cliente proprio, com a conversa dele — a lista olha compras e mensagens POR
    /// CONTATO, e dividir o contato do cenario entre os casos misturaria uns com os outros.
    ///
    /// ⚠️ A negociacao `ganha` em aberto que nasce com o contato fica de fora: so `uq_negociacoes_
    /// card_por_funil` impede duas no mesmo funil, e aqui o contato nasce SEM card.</summary>
    private static async Task<(Contato Contato, Conversa Conversa)> ClienteAsync(
        NexoraDbContext db, Ambiente amb, string marca)
    {
        var contato = new Contato
        {
            EmpresaId = amb.Cenario.Id,
            Nome = $"Cliente {marca}",
            Telefone = $"5584{Random.Shared.NextInt64(900000000, 999999999)}",
            Origem = OrigemLead.Manual
        };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();

        var conversa = new Conversa
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = contato.Id,
            ConexaoId = amb.Cenario.Conexao.Id,
            UltimaMensagemEm = Agora.UtcDateTime
        };
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return (contato, conversa);
    }

    /// <summary>Outra compra do mesmo cliente, em outro funil-livre: `concluida` direto, ou no
    /// status que o teste pedir (a cancelada e o caso que importa).</summary>
    private static async Task CompraAsync(
        NexoraDbContext db, Ambiente amb, Contato contato, DateTime ganhaEm, StatusNegociacao status)
    {
        var etapa = amb.Cenario.Etapas[0];

        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = contato.Id,
            PipelineId = etapa.PipelineId,
            EtapaId = etapa.Id,
            Status = status,
            Valor = 500m,
            ResponsavelId = amb.Cenario.Dono.Id,
            GanhaEm = ganhaEm,
            ConcluidaEm = status == StatusNegociacao.Concluida ? ganhaEm.AddDays(1) : null
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Uma SAIDA na conversa, humana ou automatica, num instante dado.</summary>
    private static async Task SaidaAsync(
        NexoraDbContext db, Ambiente amb, Conversa conversa, DateTime quando, OrigemMensagem origem)
    {
        var msg = new Mensagem
        {
            EmpresaId = amb.Cenario.Id,
            ConversaId = conversa.Id,
            ContatoId = conversa.ContatoId,
            ConexaoId = amb.Cenario.Conexao.Id,
            InstanceName = amb.Cenario.Conexao.InstanceName,
            Direcao = DirecaoMensagem.Saida,
            Origem = origem,
            TipoAutomacao = origem == OrigemMensagem.Automatica ? TipoAutomacao.Nps : null,
            Texto = "opa",
            DataDisparo = DateOnly.FromDateTime(quando),
            EnviadoPor = origem == OrigemMensagem.Humana ? amb.Cenario.Dono.Id : null,
            EnviadaEm = quando
        };
        db.Mensagens.Add(msg);
        await db.SaveChangesAsync();

        // `criado_em` e carimbado pelo banco; o teste precisa dele no instante escolhido.
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.Id == msg.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CriadoEm, quando));
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
