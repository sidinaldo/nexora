using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== OS RELATÓRIOS (BLOCO 14) =====================
///
/// O dashboard responde "como está agora". Relatório responde "o que aconteceu no período" — e é
/// onde o modelo de vendas, que mudou duas vezes (histórico no NEG-1, estados no NEG-2), tem que
/// provar que ficou certo.
///
/// O fuso de negócio é UTC-3, então TODA data de teste é montada em hora de PAREDE de Brasília e
/// convertida na hora de escrever. Uma venda fechada às 22h de Brasília é 01h UTC do dia SEGUINTE:
/// gravar hora UTC direto jogaria metade dos pontos no dia errado, e o erro pareceria do serviço.
/// ======================================================================</summary>
[Collection("banco")]
public class RelatoriosDbTests(BancoTeste banco)
{
    /// <summary>06/08/2026 é quinta. Os testes usam a semana dela.</summary>
    private static readonly DateOnly Quinta = new(2026, 8, 6);

    private static readonly TimeSpan OffsetBrasil = TimeSpan.FromHours(-3);

    /// <summary>Instante UTC a partir de uma hora de PAREDE de Brasília.</summary>
    private static DateTime Local(DateOnly dia, int hora, int minuto = 0) =>
        new DateTimeOffset(dia.ToDateTime(new TimeOnly(hora, minuto)), OffsetBrasil).UtcDateTime;

    // ============================================================ o teste que prova o bloco
    /// <summary>===================== ESCRITO PRIMEIRO =====================
    ///
    /// O prompt pedia devolvida × cancelada. Devolução foi cortada para a V2, então a prova é o
    /// par equivalente que o modelo suporta — e é a MESMA armadilha:
    ///
    ///   concluída  = o pedido acabou. Sai da coluna do kanban; o relatório do mês NÃO MUDA.
    ///   cancelada  = nunca deveria ter sido registrada. Sai RETROATIVAMENTE, e o mês corrige.
    ///
    /// Se as duas produzirem o mesmo efeito, o modelo está errado — e o relatório é onde isso
    /// aparece. AS DUAS NO MESMO TESTE, sobre o mesmo período, é o que o torna difícil de passar
    /// por acidente: uma implementação que trate concluir como cancelar derruba o faturamento
    /// para 0, e uma que trate cancelar como concluir mantém os 1.700.
    /// ============================================================</summary>
    [Fact]
    public async Task CONCLUIDA_NAO_MUDA_O_MES_MAS_CANCELADA_MUDA()
    {
        var (db, tx, amb) = await PrepararAsync("prova");
        using var _ = db; using var __ = tx;

        // Duas vendas no MESMO dia, de valores distintos para dar para saber qual saiu.
        var (_, vConcluir) = await VendaAsync(db, amb, "Vai concluir", Local(Quinta, 10), 700m);
        var (_, vCancelar) = await VendaAsync(db, amb, "Vai cancelar", Local(Quinta, 11), 1000m);

        var filtro = FiltroDe(Quinta, Quinta);

        var antes = await amb.Relatorios.VendasPorPeriodoAsync(filtro, default);
        Assert.Equal(2, antes.Totais.Vendas);
        Assert.Equal(1700m, antes.Totais.Faturamento);

        // ===== 1. CONCLUIR: o pedido acabou. O relatório do mês NÃO muda. =====
        Assert.Equal(1, await amb.Vendas.ConcluirAsync([vConcluir], default));
        db.ChangeTracker.Clear();

        var depoisDeConcluir = await amb.Relatorios.VendasPorPeriodoAsync(filtro, default);
        Assert.Equal(2, depoisDeConcluir.Totais.Vendas);
        Assert.Equal(1700m, depoisDeConcluir.Totais.Faturamento);

        // E aparece SEPARADA, porque "vendido" e "concluído" são grandezas diferentes: quem lê
        // precisa saber quanto do faturamento já é pedido entregue.
        Assert.Equal(1, depoisDeConcluir.Totais.Concluidas);
        Assert.Equal(700m, depoisDeConcluir.Totais.ValorConcluido);

        // ===== 2. CANCELAR: aquilo não aconteceu. Sai retroativamente. =====
        await amb.Vendas.CancelarAsync(vCancelar, null, default);
        db.ChangeTracker.Clear();

        var depoisDeCancelar = await amb.Relatorios.VendasPorPeriodoAsync(filtro, default);
        Assert.Equal(1, depoisDeCancelar.Totais.Vendas);
        Assert.Equal(700m, depoisDeCancelar.Totais.Faturamento);

        // A cancelada não some do relatório — ela aparece na COLUNA DELA. Sumir sem rastro é pior
        // que estar errada: quem confere o mês depois não teria como saber que existiu.
        Assert.Equal(1, depoisDeCancelar.Totais.Canceladas);
        Assert.Equal(1000m, depoisDeCancelar.Totais.ValorCancelado);

        // E o ponto do dia acompanha o total — a série não pode divergir do rodapé.
        var doDia = Assert.Single(depoisDeCancelar.Pontos);
        Assert.Equal(Quinta, doDia.Periodo);
        Assert.Equal(1, doDia.Vendas);
        Assert.Equal(700m, doDia.Faturamento);
    }

    // ============================================================ CMP-1 · o período anterior
    /// <summary>===================== OS DOIS PERÍODOS SÃO CALCULADOS NA HORA =====================
    ///
    /// ⚠️ É O TESTE QUE PROVA A REGRA CENTRAL DO CMP-1. Cancelar uma venda de JUNHO muda o número de
    /// junho que o relatório de JULHO mostra como comparação — retroativamente, hoje.
    ///
    /// Um valor guardado (snapshot do fechamento do mês) não corrigiria: junho continuaria dizendo
    /// R$ 800 para sempre, e a tela mostraria uma queda que não existe contra um passado que foi
    /// desfeito. A comparação precisa refletir a mesma verdade que o relatório do próprio mês.
    ///
    /// E as datas do anterior vêm no indicador: 01–30/06, não 01–31/06. Com o dia do fim copiado do
    /// período atual, julho (31 dias) compararia com junho até o dia 31 — que não existe.
    /// ==============================================================================</summary>
    [Fact]
    public async Task CANCELAR_UMA_VENDA_MUDA_O_NUMERO_DO_PERIODO_ANTERIOR()
    {
        var (db, tx, amb) = await PrepararAsync("cmp-retroativo");
        using var _ = db; using var __ = tx;

        var julho = new DateOnly(2026, 7, 10);
        var junho = new DateOnly(2026, 6, 10);

        await VendaAsync(db, amb, "De julho", Local(julho, 10), 1000m);
        var (_, deJunho) = await VendaAsync(db, amb, "De junho", Local(junho, 10), 800m);

        var filtro = FiltroDe(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        var antes = await amb.Relatorios.VendasPorPeriodoAsync(filtro, default);
        var cAntes = Assert.IsType<ComparativoVendas>(antes.Comparativo);

        // O mês anterior é junho INTEIRO — e junho tem 30 dias.
        Assert.Equal(new DateOnly(2026, 6, 1), cAntes.Faturamento.AnteriorDe);
        Assert.Equal(new DateOnly(2026, 6, 30), cAntes.Faturamento.AnteriorAte);
        Assert.False(cAntes.EmAndamento);

        Assert.Equal(1000m, cAntes.Faturamento.Atual);
        Assert.Equal(800m, cAntes.Faturamento.Anterior);
        Assert.Equal(200m, cAntes.Faturamento.VariacaoAbsoluta);
        Assert.Equal(25m, cAntes.Faturamento.VariacaoPercentual);
        Assert.Equal("subiu", cAntes.Faturamento.Tendencia);
        Assert.Equal("melhor", cAntes.Faturamento.Avaliacao);

        // ===== Cancela a venda de JUNHO, hoje. =====
        await amb.Vendas.CancelarAsync(deJunho, null, default);
        db.ChangeTracker.Clear();

        var depois = await amb.Relatorios.VendasPorPeriodoAsync(filtro, default);
        var cDepois = Assert.IsType<ComparativoVendas>(depois.Comparativo);

        // Junho passou a ser zero: não há com o que comparar, e o percentual não se inventa.
        Assert.Equal(0m, cDepois.Faturamento.Anterior);
        Assert.Null(cDepois.Faturamento.VariacaoPercentual);
        Assert.Equal(1000m, cDepois.Faturamento.Atual);

        // E a cancelada aparece na coluna dela, no período ANTERIOR — igual ao relatório do mês:
        // faturamento que some sem rastro é pior que faturamento errado.
        Assert.Equal(1m, cDepois.Canceladas.Anterior);
        Assert.Equal(800m, cDepois.ValorCancelado.Anterior);

        // ⚠️ E "cancelado" é `SentidoBom.Desce`: junho ganhou R$ 800 de cancelamento e julho tem
        // zero, então a variação CAIU e isso é MELHOR. Se a cor seguisse a seta, a tela pintaria
        // de vermelho a melhor notícia da comparação.
        Assert.Equal("caiu", cDepois.ValorCancelado.Tendencia);
        Assert.Equal("melhor", cDepois.ValorCancelado.Avaliacao);
    }

    /// <summary>⚠️ A FRONTEIRA DE FUSO, NO PERÍODO ANTERIOR. Uma venda às 23h30 de 30/06 em Brasília
    /// é 02h30 UTC de 01/07: lida em UTC, ela cairia em JULHO e sumiria da comparação.
    ///
    /// O recorte do anterior usa a MESMA conversão do período atual (`EmUtc`) — escrita duas vezes,
    /// ela divergiria no corte, e o anterior perderia o último dia com os dois números plausíveis
    /// na tela.</summary>
    [Fact]
    public async Task A_VENDA_DAS_23H30_DO_ULTIMO_DIA_FICA_NO_PERIODO_ANTERIOR()
    {
        var (db, tx, amb) = await PrepararAsync("cmp-fuso");
        using var _ = db; using var __ = tx;

        // 23h30 de 30/06 em Brasília = 02h30 UTC de 01/07.
        await VendaAsync(db, amb, "Virada", Local(new DateOnly(2026, 6, 30), 23, 30), 500m);

        var julho = await amb.Relatorios.VendasPorPeriodoAsync(
            FiltroDe(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)), default);

        var c = Assert.IsType<ComparativoVendas>(julho.Comparativo);

        Assert.Equal(0m, c.Faturamento.Atual);
        Assert.Equal(500m, c.Faturamento.Anterior);
    }

    // ============================================================ 1 · vendas por período
    [Fact]
    public async Task VENDAS_POR_PERIODO_bate_com_a_contagem_manual()
    {
        var (db, tx, amb) = await PrepararAsync("r1-manual");
        using var _ = db; using var __ = tx;

        // Quinta: duas vendas (100 + 250). Sexta: uma (50). Sábado: nenhuma.
        await VendaAsync(db, amb, "a", Local(Quinta, 9), 100m);
        await VendaAsync(db, amb, "b", Local(Quinta, 17), 250m);
        await VendaAsync(db, amb, "c", Local(Quinta.AddDays(1), 10), 50m);

        var r = await amb.Relatorios.VendasPorPeriodoAsync(
            FiltroDe(Quinta, Quinta.AddDays(2)), default);

        Assert.Equal(3, r.Pontos.Count);
        Assert.Equal(2, r.Pontos[0].Vendas);
        Assert.Equal(350m, r.Pontos[0].Faturamento);
        Assert.Equal(1, r.Pontos[1].Vendas);
        Assert.Equal(50m, r.Pontos[1].Faturamento);

        Assert.Equal(3, r.Totais.Vendas);
        Assert.Equal(400m, r.Totais.Faturamento);
    }

    /// <summary>⚠️ O FILTRO DE STATUS DA TELA CONTRA O ENUM DO BANCO (E4d), e nenhum teste cobria.
    ///
    /// A tela manda `StatusVenda`, o banco guarda `StatusNegociacao`. Os dois coincidem em
    /// `concluida` e `cancelada` e divergem justamente no estado mais comum: `fechada` virou
    /// `ganha`. Sem a tradução em `StatusNoBanco`, escolher "Fechada" no filtro devolveria ZERO
    /// linhas — sem erro, sem aviso, e com o gráfico ao lado mostrando faturamento.
    ///
    /// Este é o tipo de defeito que passa em revisão: o `$10::text` continua lá, a consulta
    /// continua rodando, e só o resultado está errado.</summary>
    [Fact]
    public async Task FILTRAR_POR_FECHADA_ENCONTRA_A_VENDA_EM_ABERTO()
    {
        var (db, tx, amb) = await PrepararAsync("r1-status");
        using var _ = db; using var __ = tx;

        await VendaAsync(db, amb, "viva", Local(Quinta, 9), 100m);

        var concluida = await VendaAsync(db, amb, "concluida", Local(Quinta, 10), 250m);
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == concluida.Contato.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.Status, StatusNegociacao.Concluida));
        db.ChangeTracker.Clear();

        var periodo = FiltroDe(Quinta, Quinta.AddDays(2));

        // ⚠️ ERA `StatusVenda.Fechada` E PRECISAVA DE TRADUÇÃO (E4e/5). O contrato público
        // falava `fechada` e o banco falava `ganha`; sem traduzir, o filtro vinha zerado com o
        // gráfico ao lado mostrando faturamento. Agora os dois falam a mesma língua.
        var soAbertas = await amb.Relatorios.VendasPorPeriodoAsync(
            periodo with { Status = StatusNegociacao.Ganha }, default);
        Assert.Equal(1, soAbertas.Totais.Vendas);
        Assert.Equal(100m, soAbertas.Totais.Faturamento);

        // E os dois valores que NÃO mudaram de nome continuam funcionando.
        var soConcluidas = await amb.Relatorios.VendasPorPeriodoAsync(
            periodo with { Status = StatusNegociacao.Concluida }, default);
        Assert.Equal(1, soConcluidas.Totais.Vendas);
        Assert.Equal(250m, soConcluidas.Totais.Faturamento);

        // Sem filtro, as duas.
        Assert.Equal(2, (await amb.Relatorios.VendasPorPeriodoAsync(periodo, default)).Totais.Vendas);
    }

    /// <summary>Gráfico com buraco mente sobre a tendência, e mente para melhor: o traço liga o
    /// ponto anterior no seguinte e desenha uma reta onde houve um dia parado.</summary>
    [Fact]
    public async Task DIA_SEM_VENDA_VOLTA_COM_ZERO_NAO_AUSENTE()
    {
        var (db, tx, amb) = await PrepararAsync("r1-buraco");
        using var _ = db; using var __ = tx;

        // Quinta e sábado têm venda; SEXTA não tem.
        await VendaAsync(db, amb, "a", Local(Quinta, 9), 100m);
        await VendaAsync(db, amb, "b", Local(Quinta.AddDays(2), 9), 200m);

        var r = await amb.Relatorios.VendasPorPeriodoAsync(
            FiltroDe(Quinta, Quinta.AddDays(2)), default);

        Assert.Equal(3, r.Pontos.Count);

        var sexta = r.Pontos[1];
        Assert.Equal(Quinta.AddDays(1), sexta.Periodo);
        Assert.Equal(0, sexta.Vendas);
        Assert.Equal(0m, sexta.Faturamento);
    }

    [Fact]
    public async Task PERIODO_INVERTIDO_e_recusado()
    {
        var (db, tx, amb) = await PrepararAsync("r1-invertido");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Relatorios.VendasPorPeriodoAsync(
                FiltroDe(Quinta.AddDays(3), Quinta), default));
    }

    /// <summary>A faixa de valor é do relatório, não global: aqui ela vale sobre `vendas.valor`,
    /// que é o que fechou — e não sobre `contatos.valor`, que é estimativa em aberto.</summary>
    [Fact]
    public async Task FAIXA_DE_VALOR_corta_sobre_o_valor_da_VENDA()
    {
        var (db, tx, amb) = await PrepararAsync("r1-faixa");
        using var _ = db; using var __ = tx;

        await VendaAsync(db, amb, "barata", Local(Quinta, 9), 50m);
        await VendaAsync(db, amb, "media", Local(Quinta, 10), 500m);
        await VendaAsync(db, amb, "cara", Local(Quinta, 11), 5000m);

        var r = await amb.Relatorios.VendasPorPeriodoAsync(
            FiltroDe(Quinta, Quinta) with { ValorMin = 100m, ValorMax = 1000m }, default);

        Assert.Equal(1, r.Totais.Vendas);
        Assert.Equal(500m, r.Totais.Faturamento);
    }

    // ============================================================ 2 · desempenho por vendedor
    [Fact]
    public async Task DESEMPENHO_POR_VENDEDOR_bate_com_a_contagem_manual()
    {
        var (db, tx, amb) = await PrepararAsync("r2");
        using var _ = db; using var __ = tx;

        var ana = amb.Cenario.Dono;
        var bruno = await VendedorAsync(db, amb, "bruno");

        // Ana: 2 vendas (300 + 500) e 1 perdido -> conversão 2/3.
        await VendaAsync(db, amb, "a1", Local(Quinta, 9), 300m, ana.Id);
        await VendaAsync(db, amb, "a2", Local(Quinta, 10), 500m, ana.Id);
        await PerdidoAsync(db, amb, "a3", Local(Quinta, 11), ana.Id, "preço");

        // Bruno: 1 venda (100), nenhuma perda -> conversão 1/1.
        await VendaAsync(db, amb, "b1", Local(Quinta, 12), 100m, bruno.Id);

        var linhas = await amb.Relatorios.DesempenhoVendedoresAsync(
            FiltroDe(Quinta, Quinta), default);

        var daAna = linhas.Single(l => l.UsuarioId == ana.Id);
        Assert.Equal(2, daAna.Vendas);
        Assert.Equal(800m, daAna.Valor);
        Assert.Equal(400m, daAna.TicketMedio);
        Assert.Equal(2d / 3d, daAna.Conversao, 4);

        var doBruno = linhas.Single(l => l.UsuarioId == bruno.Id);
        Assert.Equal(1, doBruno.Vendas);
        Assert.Equal(100m, doBruno.Valor);
        Assert.Equal(1d, doBruno.Conversao, 4);
    }

    /// <summary>===================== O CORTE DE PAPEL VIVE NA API =====================
    ///
    /// A tela esconder o seletor não é proteção nenhuma: o vendedor troca o parâmetro na
    /// requisição e vê o número do colega. Este teste chama o SERVIÇO direto, passando o id de
    /// outro vendedor — que é exatamente o que uma requisição forjada faria.
    /// ======================================================================</summary>
    [Fact]
    public async Task VENDEDOR_NAO_VE_NUMERO_DE_OUTRO_VENDEDOR_nem_pela_API_direta()
    {
        var (db, tx, amb) = await PrepararAsync("r2-papel");
        using var _ = db; using var __ = tx;

        var ana = amb.Cenario.Dono;
        var bruno = await VendedorAsync(db, amb, "bruno");

        await VendaAsync(db, amb, "da-ana", Local(Quinta, 9), 9000m, ana.Id);
        await VendaAsync(db, amb, "do-bruno", Local(Quinta, 10), 100m, bruno.Id);

        // Bruno entra, e PEDE explicitamente os números da Ana.
        amb.Contexto.UsuarioId = bruno.Id;
        amb.Contexto.Papel = "vendedor";

        var filtroForjado = FiltroDe(Quinta, Quinta) with { ResponsavelId = ana.Id };

        var linhas = await amb.Relatorios.DesempenhoVendedoresAsync(filtroForjado, default);
        var linha = Assert.Single(linhas);
        Assert.Equal(bruno.Id, linha.UsuarioId);
        Assert.Equal(100m, linha.Valor);

        // E o mesmo vale para o relatório de vendas: o total dele é o DELE.
        var vendas = await amb.Relatorios.VendasPorPeriodoAsync(filtroForjado, default);
        Assert.Equal(100m, vendas.Totais.Faturamento);

        // Gestor, com o MESMO filtro, vê os dois — senão o teste passaria com uma regra que
        // simplesmente não devolve nada para ninguém.
        amb.Contexto.UsuarioId = ana.Id;
        amb.Contexto.Papel = "gestor";
        var doGestor = await amb.Relatorios.VendasPorPeriodoAsync(
            FiltroDe(Quinta, Quinta), default);
        Assert.Equal(9100m, doGestor.Totais.Faturamento);
    }

    // ============================================================ 3 · origem dos leads
    [Fact]
    public async Task ORIGEM_DOS_LEADS_traz_volume_conversao_E_VALOR()
    {
        var (db, tx, amb) = await PrepararAsync("r3");
        using var _ = db; using var __ = tx;

        // Instagram: 2 leads, 1 vendeu por 400.
        await VendaAsync(db, amb, "i1", Local(Quinta, 9), 400m, origem: OrigemLead.Instagram);
        await LeadAsync(db, amb, "i2", Local(Quinta, 9), origem: OrigemLead.Instagram);

        // Indicação: 1 lead, vendeu por 1000.
        await VendaAsync(db, amb, "r1", Local(Quinta, 10), 1000m, origem: OrigemLead.Indicacao);

        var linhas = await amb.Relatorios.OrigemLeadsAsync(FiltroDe(Quinta, Quinta), default);

        var insta = linhas.Single(l => l.Origem == "instagram");
        Assert.Equal(2, insta.Leads);
        Assert.Equal(1, insta.Vendas);
        Assert.Equal(400m, insta.Valor);
        Assert.Equal(0.5d, insta.Conversao, 4);

        // O VALOR é o que responde "qual canal traz dinheiro" — indicação tem metade do volume
        // do Instagram e mais que o dobro do valor.
        var indicacao = linhas.Single(l => l.Origem == "indicacao");
        Assert.Equal(1000m, indicacao.Valor);
    }

    // ==================================================================== 3b · vendas por canal
    /// <summary>===================== QUAL CAMPANHA TROUXE DINHEIRO (NEG-3) =====================
    ///
    /// O relatório de cima conta LEADS por tipo de origem. Este conta DINHEIRO por campanha — e a
    /// linha sem canal identificado aparece junto, porque escondê-la faria a fatia atribuída
    /// parecer o total do mês.
    /// ==================================================================================</summary>
    [Fact]
    public async Task VENDAS_POR_CANAL_SOMA_POR_CAMPANHA_E_MOSTRA_O_QUE_NAO_TEM_CANAL()
    {
        var (db, tx, amb) = await PrepararAsync("canal");
        using var _ = db; using var __ = tx;

        var panfleto = await CanalDeTesteAsync(db, amb, "Panfleto Julho");
        var vitrine = await CanalDeTesteAsync(db, amb, "Vitrine");

        var a1 = await ContatoSimplesAsync(amb, "Comprou pelo panfleto");
        var a2 = await ContatoSimplesAsync(amb, "Também pelo panfleto");
        var b1 = await ContatoSimplesAsync(amb, "Comprou pela vitrine");
        var s1 = await ContatoSimplesAsync(amb, "Sem rastro");

        await amb.Contatos.MarcarGanhoAsync(a1, 1000m, panfleto, null, default);
        await amb.Contatos.MarcarGanhoAsync(a2, 500m, panfleto, null, default);
        await amb.Contatos.MarcarGanhoAsync(b1, 300m, vitrine, null, default);
        await amb.Contatos.MarcarGanhoAsync(s1, 700m, null, null, default);

        var hoje = DateOnly.FromDateTime(ContatosDbTests.Agora.UtcDateTime);
        var linhas = await amb.Relatorios.VendasPorCanalAsync(FiltroDe(hoje, hoje), default);

        // Ordenado por valor: panfleto (1500), sem canal (700), vitrine (300).
        Assert.Equal(3, linhas.Count);
        Assert.Equal("Panfleto Julho", linhas[0].Canal);
        Assert.Equal(2, linhas[0].Vendas);
        Assert.Equal(1500m, linhas[0].Valor);

        Assert.Null(linhas[1].Canal);                  // a linha do que não tem campanha
        Assert.Equal(700m, linhas[1].Valor);

        Assert.Equal("Vitrine", linhas[2].Canal);
        Assert.Equal(300m, linhas[2].Valor);

        // ⚠️ E a soma bate com o faturamento do relatório 1. É o que o LEFT JOIN garante — com
        // JOIN, os 700 sumiriam e ninguém saberia por quê.
        Assert.Equal(2500m, linhas.Sum(l => l.Valor));
    }

    /// <summary>Venda cancelada não entra, como em todo o resto do módulo.</summary>
    [Fact]
    public async Task VENDA_CANCELADA_NAO_ENTRA_NO_RELATORIO_POR_CANAL()
    {
        var (db, tx, amb) = await PrepararAsync("canal-cancel");
        using var _ = db; using var __ = tx;

        var canal = await CanalDeTesteAsync(db, amb, "Campanha");
        var c = await ContatoSimplesAsync(amb, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c, 900m, canal, null, default);

        db.ChangeTracker.Clear();
        var venda = await db.Negociacoes.AsNoTracking().SingleAsync(v => v.ContatoId == c);
        await amb.Vendas.CancelarAsync(venda.Id, null, default);

        var hoje = DateOnly.FromDateTime(ContatosDbTests.Agora.UtcDateTime);
        Assert.Empty(await amb.Relatorios.VendasPorCanalAsync(FiltroDe(hoje, hoje), default));
    }

    // ============================================================ 4 · funil no período
    /// <summary>===================== A OPÇÃO B, E POR QUE ELA CABE =====================
    ///
    /// "Quantos entraram em Proposta este mês" precisa de histórico de movimentação, que não
    /// existe como tabela. Mas o `InterceptorTrilha` grava `etapaId: {antes, depois}` no jsonb de
    /// QUALQUER evento que mude a etapa — não só do arrastar. Então `Moveu`, `Ganhou`, `Reabriu` e
    /// a criação do contato todos entram, de graça.
    ///
    /// Este teste prova as duas portas: o ARRASTO e o REGISTRO DE VENDA. Se a consulta filtrasse
    /// por `acao = 'Moveu'` — o caminho óbvio — a segunda sumiria, e "entraram em Venda" viria
    /// sempre zero.
    /// ======================================================================</summary>
    [Fact]
    public async Task FUNIL_NO_PERIODO_conta_entradas_por_ARRASTO_e_por_REGISTRO_DE_VENDA()
    {
        var (db, tx, amb) = await PrepararAsync("r4");
        using var _ = db; using var __ = tx;

        var proposta = amb.Cenario.Etapas[1];
        var etapaGanho = await db.EtapasFunil.AsNoTracking().FirstAsync(e => e.EGanho);

        // Porta 1: arrastar para Proposta.
        var arrastado = await amb.Contatos.CriarAsync(
            new NovoContato("Arrastado", $"5584{Random.Shared.NextInt64(900000000, 999999999)}"), default);
        await amb.Funil.MoverAsync(
            await ContatosDbTests.CardDoContatoAsync(db, arrastado),
            new MoverContato(proposta.Id, null), default);

        // Porta 2: registrar venda — move para a etapa de ganho sem passar pelo `MoverAsync`.
        var vendido = await amb.Contatos.CriarAsync(
            new NovoContato("Vendido", $"5584{Random.Shared.NextInt64(900000000, 999999999)}"), default);
        await amb.Contatos.MarcarGanhoAsync(vendido, 500m, null, null, default);

        db.ChangeTracker.Clear();
        var hoje = DateOnly.FromDateTime(ContatosDbTests.Agora.UtcDateTime);
        var r = await amb.Relatorios.FunilNoPeriodoAsync(FiltroDe(hoje, hoje), default);

        Assert.Equal(1, r.Entradas.Single(e => e.EtapaId == proposta.Id).Entradas);
        Assert.Equal(1, r.Entradas.Single(e => e.EtapaId == etapaGanho.Id).Entradas);

        // E a FOTO vem junto, rotulada separadamente — "entrou no período" e "está agora" são
        // perguntas diferentes, e misturá-las é o que o prompt proíbe.
        Assert.Equal(1, r.Agora.Single(e => e.EtapaId == proposta.Id).Contatos);
    }

    /// <summary>⚠️ AS ENTRADAS IGNORAVAM O FILTRO DE PESSOA E O DE ORIGEM (AUD-1), e a foto do
    /// mesmo cartão aplicava os dois. O vendedor — que recebe o filtro com o próprio id — via as
    /// entradas da empresa inteira ao lado da foto só dele.</summary>
    [Fact]
    public async Task AS_ENTRADAS_DO_FUNIL_RESPEITAM_PESSOA_E_ORIGEM()
    {
        var (db, tx, amb) = await PrepararAsync("r4-recorte");
        using var _ = db; using var __ = tx;

        var proposta = amb.Cenario.Etapas[1];
        var bruno = await VendedorAsync(db, amb, "bruno-entradas");

        var doBruno = await amb.Contatos.CriarAsync(
            new NovoContato("Do Bruno", $"5584{Random.Shared.NextInt64(900000000, 999999999)}"), default);
        var doDono = await amb.Contatos.CriarAsync(
            new NovoContato("Do dono", $"5584{Random.Shared.NextInt64(900000000, 999999999)}"), default);

        foreach (var contato in new[] { doBruno, doDono })
            await amb.Funil.MoverAsync(
                await ContatosDbTests.CardDoContatoAsync(db, contato),
                new MoverContato(proposta.Id, null), default);

        await db.Negociacoes.Where(n => n.ContatoId == doBruno)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.ResponsavelId, bruno.Id));
        await db.Contatos.Where(c => c.Id == doBruno)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Origem, OrigemLead.Instagram));
        db.ChangeTracker.Clear();

        var hoje = DateOnly.FromDateTime(ContatosDbTests.Agora.UtcDateTime);
        int EntradasEmProposta(RelatorioFunil r) => r.Entradas.Single(e => e.EtapaId == proposta.Id).Entradas;

        var todos = await amb.Relatorios.FunilNoPeriodoAsync(FiltroDe(hoje, hoje), default);
        Assert.Equal(2, EntradasEmProposta(todos));

        var dele = await amb.Relatorios.FunilNoPeriodoAsync(
            FiltroDe(hoje, hoje) with { ResponsavelId = bruno.Id }, default);
        Assert.Equal(1, EntradasEmProposta(dele));

        var doInstagram = await amb.Relatorios.FunilNoPeriodoAsync(
            FiltroDe(hoje, hoje) with { Origem = OrigemLead.Instagram }, default);
        Assert.Equal(1, EntradasEmProposta(doInstagram));
    }

    // ============================================================ 5 · tempo de resposta
    /// <summary>===================== O BACKFILL TEM UMA PORTA SO, E E ISSO QUE SE AFIRMA =====================
    ///
    /// A migration infere o passado por `lembrete_id IS NOT NULL`. So.
    ///
    /// ⚠️ HAVIA UMA SEGUNDA REGRA — `saida + enviado_por IS NULL + sem lembrete` — e ela foi
    /// RODADA contra o banco de desenvolvimento antes de este teste existir: marcou 459 mensagens,
    /// e as 459 tinham `payload_raw`, ou seja, vieram do webhook. Sao mensagens que o vendedor
    /// mandou DO CELULAR. O INSERT do webhook nao grava `enviado_por`, entao a coluna fica nula
    /// exatamente como numa automatica.
    ///
    /// Este teste e a rede: uma saida sem autor e sem lembrete tem de continuar HUMANA.
    /// ==============================================================</summary>
    [Fact]
    public async Task O_BACKFILL_NAO_MARCA_SAIDA_DO_CELULAR_COMO_AUTOMATICA()
    {
        var (db, tx, amb) = await PrepararAsync("backfill");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb, "historico", Local(Quinta, 9), amb.Cenario.Dono.Id);
        var conversa = await ConversaAsync(db, amb, contato);

        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Entrada, Local(Quinta, 9));
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Saida, Local(Quinta, 10));

        // `enviado_por` nulo nas duas — o estado de quem mandou do celular e de quem recebeu.
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.ConversaId == conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.EnviadoPor, (long?)null));

        // O predicado da migration, como ele ficou: uma porta so.
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE mensagens
               SET origem = 'automatica', tipo_automacao = 'lembrete'
             WHERE lembrete_id IS NOT NULL;
            """);

        db.ChangeTracker.Clear();
        var linhas = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ConversaId == conversa.Id).OrderBy(m => m.Id).ToListAsync();

        Assert.All(linhas, l => Assert.Equal(OrigemMensagem.Humana, l.Origem));
    }

    /// <summary>===================== A AUTOMATICA NAO E RESPOSTA (NPS-1) =====================
    ///
    /// ⚠️ ESTE RELATORIO TINHA UMA LINHA "Automático", e ela media um DEFEITO. O comentario que a
    /// defendia dizia "e resposta que o cliente recebeu" — e o caso que ele imaginava, o follow-up
    /// respondendo, nao acontece: `DadosFollowUp.ConversasInativasAsync` exige
    /// `ultima_mensagem_direcao = saida`, ou seja, o follow-up so dispara quando a ultima palavra
    /// JA foi nossa.
    ///
    /// Quem caia ali era o LEMBRETE com mensagem, que dispara por `data_alvo &lt;= hoje` sem olhar
    /// a conversa. Cliente escreve de manha, lembrete sai a tarde, e o relatorio registrava
    /// "resposta em 4 horas" para algo que nao respondeu nada.
    ///
    /// ⚠️ E NAO HAVIA TESTE NENHUM sobre aquela linha: removi e a suite de 1530 ficou verde. Este
    /// teste e a rede que faltava.
    /// ==============================================================</summary>
    [Fact]
    public async Task TEMPO_DE_RESPOSTA_IGNORA_MENSAGEM_AUTOMATICA()
    {
        var (db, tx, amb) = await PrepararAsync("r5-auto");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb, "aguardando", Local(Quinta, 9), amb.Cenario.Dono.Id);
        var conversa = await ConversaAsync(db, amb, contato);

        // O cliente escreve as 9h e ninguem responde. As 13h um lembrete automatico dispara.
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Entrada, Local(Quinta, 9));
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Saida, Local(Quinta, 13),
            OrigemMensagem.Automatica, TipoAutomacao.Lembrete);

        var linhas = await amb.Relatorios.TempoRespostaAsync(FiltroDe(Quinta, Quinta), default);

        // Ninguem respondeu: nao ha linha com resposta, e nao ha linha "Automático".
        Assert.DoesNotContain(linhas, l => l.Nome.Contains("Autom"));
        Assert.All(linhas, l => Assert.Equal(0, l.Respostas));
    }

    /// <summary>O par. Sem ele, uma versao que ignorasse TODA saida passaria no teste de cima.</summary>
    [Fact]
    public async Task TEMPO_DE_RESPOSTA_CONTA_A_RESPOSTA_HUMANA_DEPOIS_DA_AUTOMATICA()
    {
        var (db, tx, amb) = await PrepararAsync("r5-auto2");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb, "respondido", Local(Quinta, 9), amb.Cenario.Dono.Id);
        var conversa = await ConversaAsync(db, amb, contato);

        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Entrada, Local(Quinta, 9));
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Saida, Local(Quinta, 13),
            OrigemMensagem.Automatica, TipoAutomacao.Lembrete);
        // O vendedor responde as 14h. Sao 5 horas de espera, nao 4 — a automatica nao conta.
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Saida, Local(Quinta, 14));

        var linha = (await amb.Relatorios.TempoRespostaAsync(FiltroDe(Quinta, Quinta), default))
            .Single(l => l.UsuarioId == amb.Cenario.Dono.Id);

        Assert.Equal(1, linha.Respostas);
        Assert.Equal(300d, linha.MediaMinutos, 1);
    }

    /// <summary>===================== A NOTA DA PESQUISA NAO ESPERA RESPOSTA =====================
    ///
    /// ⚠️ O CASO DA REVISAO: o "10" do cliente nao e pergunta, e por isso nao acende o semaforo —
    /// mas ficava na linha do tempo daqui. A saida seguinte do vendedor, dias depois, era pareada
    /// com ele.
    ///
    /// O cliente responde a pesquisa com "10" as 9h e as 10h pergunta outra coisa; o vendedor
    /// responde as 11h. A espera e de UMA hora, desde a pergunta. Contando a nota como primeira
    /// entrada da rajada, daria duas.
    /// ======================================================================================</summary>
    [Fact]
    public async Task TEMPO_DE_RESPOSTA_IGNORA_A_NOTA_QUE_A_PESQUISA_CONSUMIU()
    {
        var (db, tx, amb) = await PrepararAsync("r5-nota-nps");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb, "nps", Local(Quinta, 9), amb.Cenario.Dono.Id);
        var conversa = await ConversaAsync(db, amb, contato);

        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Entrada, Local(Quinta, 9), tratada: true);
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Entrada, Local(Quinta, 10));
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Saida, Local(Quinta, 11));

        var linha = (await amb.Relatorios.TempoRespostaAsync(FiltroDe(Quinta, Quinta), default))
            .Single(l => l.UsuarioId == amb.Cenario.Dono.Id);

        Assert.Equal(1, linha.Respostas);
        Assert.Equal(60d, linha.MediaMinutos, 1);
    }

    /// <summary>===================== A RESPOSTA PELO CELULAR CONTA =====================
    ///
    /// ⚠️ O DEFEITO QUE A REVISAO ACHOU, e que eu causei no NPS-1: ao tirar a linha "Automático",
    /// a resposta que o vendedor manda do PROPRIO WhatsApp — que chega pelo webhook, humana e sem
    /// `enviado_por` — ficou sem lugar no `LEFT JOIN` e sumiu do relatorio. O vendedor que responde
    /// na rua aparecia como quem nao respondeu.
    /// ================================================================</summary>
    [Fact]
    public async Task TEMPO_DE_RESPOSTA_CONTA_A_RESPOSTA_DADA_PELO_CELULAR()
    {
        var (db, tx, amb) = await PrepararAsync("r5-celular");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb, "celular", Local(Quinta, 9), amb.Cenario.Dono.Id);
        var conversa = await ConversaAsync(db, amb, contato);

        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Entrada, Local(Quinta, 9));
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Saida, Local(Quinta, 10), peloCelular: true);

        var linhas = await amb.Relatorios.TempoRespostaAsync(FiltroDe(Quinta, Quinta), default);

        var celular = Assert.Single(linhas, l => l.UsuarioId == null);
        Assert.Equal("Pelo celular", celular.Nome);
        Assert.Equal(1, celular.Respostas);
        Assert.Equal(60d, celular.MediaMinutos, 1);
    }

    /// <summary>A linha "Pelo celular" so aparece com resposta — a antiga "Automático" aparecia
    /// zerada para toda empresa, inclusive para quem nunca respondeu de fora do painel.</summary>
    [Fact]
    public async Task SEM_RESPOSTA_PELO_CELULAR_A_LINHA_NAO_APARECE()
    {
        var (db, tx, amb) = await PrepararAsync("r5-sem-celular");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb, "painel", Local(Quinta, 9), amb.Cenario.Dono.Id);
        var conversa = await ConversaAsync(db, amb, contato);

        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Entrada, Local(Quinta, 9));
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Saida, Local(Quinta, 10));

        var linhas = await amb.Relatorios.TempoRespostaAsync(FiltroDe(Quinta, Quinta), default);

        Assert.DoesNotContain(linhas, l => l.UsuarioId == null);
    }

    /// <summary>Com recorte por pessoa, a resposta sem autor nao entra: nao ha como atribui-la ao
    /// vendedor filtrado.</summary>
    [Fact]
    public async Task COM_FILTRO_DE_RESPONSAVEL_A_LINHA_DO_CELULAR_NAO_ENTRA()
    {
        var (db, tx, amb) = await PrepararAsync("r5-celular-filtro");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb, "celular-f", Local(Quinta, 9), amb.Cenario.Dono.Id);
        var conversa = await ConversaAsync(db, amb, contato);

        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Entrada, Local(Quinta, 9));
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Saida, Local(Quinta, 10), peloCelular: true);

        var linhas = await amb.Relatorios.TempoRespostaAsync(
            FiltroDe(Quinta, Quinta) with { ResponsavelId = amb.Cenario.Dono.Id }, default);

        Assert.DoesNotContain(linhas, l => l.UsuarioId == null);
    }

    /// <summary>Sem descontar o fora-de-janela o número é inútil: mensagem que chega às 22h e é
    /// respondida às 8h05 mostraria 10 horas, quando o vendedor respondeu em 5 minutos de
    /// expediente.</summary>
    [Fact]
    public async Task TEMPO_DE_RESPOSTA_desconta_hora_fora_da_janela()
    {
        var (db, tx, amb) = await PrepararAsync("r5-janela");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb, "noturno", Local(Quinta, 9), amb.Cenario.Dono.Id);
        var conversa = await ConversaAsync(db, amb, contato);

        // 22h de quinta -> 8h05 de sexta. Relógio de parede: 10h05. Úteis: 5 minutos.
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Entrada, Local(Quinta, 22));
        await MensagemAsync(db, amb, conversa, DirecaoMensagem.Saida, Local(Quinta.AddDays(1), 8, 5));

        var linhas = await amb.Relatorios.TempoRespostaAsync(
            FiltroDe(Quinta, Quinta.AddDays(1)), default);

        var linha = linhas.Single(l => l.UsuarioId == amb.Cenario.Dono.Id);
        Assert.Equal(1, linha.Respostas);
        Assert.Equal(5d, linha.MediaMinutos, 1);
        Assert.Equal(5d, linha.MedianaMinutos, 1);
    }

    [Fact]
    public async Task TEMPO_DE_RESPOSTA_traz_media_E_mediana()
    {
        var (db, tx, amb) = await PrepararAsync("r5-mediana");
        using var _ = db; using var __ = tx;

        var dono = amb.Cenario.Dono.Id;

        // Três respostas dentro da janela: 10, 20 e 120 minutos.
        // Média = 50; mediana = 20. O par existe porque UM atendimento esquecido puxa a média e
        // não mexe na mediana — e quem lê precisa ver os dois para saber qual é o caso.
        foreach (var (inicio, fim) in new[] { (9, 10), (11, 20), (13, 120) })
        {
            var c = await LeadAsync(db, amb, $"m{inicio}", Local(Quinta, 8), dono);
            var conv = await ConversaAsync(db, amb, c);
            await MensagemAsync(db, amb, conv, DirecaoMensagem.Entrada, Local(Quinta, inicio));
            await MensagemAsync(db, amb, conv, DirecaoMensagem.Saida, Local(Quinta, inicio).AddMinutes(fim));
        }

        var linha = (await amb.Relatorios.TempoRespostaAsync(FiltroDe(Quinta, Quinta), default))
            .Single(l => l.UsuarioId == dono);

        Assert.Equal(3, linha.Respostas);
        Assert.Equal(50d, linha.MediaMinutos, 1);
        Assert.Equal(20d, linha.MedianaMinutos, 1);
    }

    // ============================================================ 6 · motivos de perda
    [Fact]
    public async Task MOTIVOS_DE_PERDA_trazem_contagem_e_valor_perdido()
    {
        var (db, tx, amb) = await PrepararAsync("r6");
        using var _ = db; using var __ = tx;

        await PerdidoAsync(db, amb, "p1", Local(Quinta, 9), null, "preço", 300m);
        await PerdidoAsync(db, amb, "p2", Local(Quinta, 10), null, "preço", 200m);
        await PerdidoAsync(db, amb, "p3", Local(Quinta, 11), null, "prazo", 1000m);

        var linhas = await amb.Relatorios.MotivosPerdaAsync(FiltroDe(Quinta, Quinta), default);

        var preco = linhas.Single(l => l.Motivo == "preço");
        Assert.Equal(2, preco.Contatos);
        Assert.Equal(500m, preco.ValorPerdido);

        // Ordenado pelo que mais dói: prazo perde menos gente e mais dinheiro.
        Assert.Equal("prazo", linhas[0].Motivo);
        Assert.Equal(1000m, linhas[0].ValorPerdido);
    }

    // ============================================================ CAN-1 · a venda desfeita
    /// <summary>===================== A UNICA PERDA QUE VEM COM DINHEIRO =====================
    ///
    /// O negocio perdido comum quase nunca tem valor: ele nunca virou venda, entao nao ha compra
    /// de onde herdar um numero. O relatorio se ordena pelo VALOR e vivia mostrando R$ 0,00 — a
    /// ordenacao prometia uma leitura que o produto nao tinha como entregar.
    ///
    /// A venda cancelada por desistencia tem. Ela existiu, foi contada, e o valor e o dela.
    ///
    /// ⚠️ A ORDENACAO E A AFIRMACAO FORTE. Checar so que a linha aparece passaria numa versao que
    /// trouxesse a cancelada com valor zero — ela estaria la, embaixo, e o relatorio continuaria
    /// dizendo que o maior problema e o motivo que perdeu mais GENTE. Aqui o cancelamento tem de
    /// vir em PRIMEIRO, com os 1.000 na frente dos 300 de "preco".
    /// ==============================================================================</summary>
    [Fact]
    public async Task A_VENDA_CANCELADA_COMO_PERDA_ENTRA_NO_RELATORIO_COM_O_VALOR_DELA()
    {
        var (db, tx, amb) = await PrepararAsync("can1-perda");
        using var _ = db; using var __ = tx;

        // Um perdido comum, do jeito que a maioria e: sem valor nenhum.
        await PerdidoAsync(db, amb, "comum", Local(Quinta, 9), null, "preço", 300m);
        await PerdidoAsync(db, amb, "sem-valor", Local(Quinta, 9), null, "sumiu");

        // E uma venda de verdade, desfeita porque o cliente desistiu.
        var (_, venda) = await VendaAsync(db, amb, "desistiu", Local(Quinta, 10), 1000m);
        await amb.Vendas.CancelarAsync(venda, "Achou caro", default);
        db.ChangeTracker.Clear();

        var linhas = await amb.Relatorios.MotivosPerdaAsync(FiltroDe(Quinta, Quinta), default);

        var desistencia = linhas.Single(l => l.Motivo == "Achou caro");
        Assert.Equal(1, desistencia.Contatos);
        Assert.Equal(1000m, desistencia.ValorPerdido);

        // ⚠️ AS DUAS FONTES NA MESMA LISTA, e e por isso que elas convivem num relatorio so: a
        //    pergunta "onde estou perdendo" nao distingue negocio que nao fechou de venda que
        //    voltou atras — as duas sao dinheiro que nao ficou.
        Assert.Equal(3, linhas.Count);
        Assert.Contains(linhas, l => l.Motivo == "preço");
        Assert.Contains(linhas, l => l.Motivo == "sumiu");

        // E a ordenacao por valor passa a significar alguma coisa.
        Assert.Equal("Achou caro", linhas[0].Motivo);
    }

    /// <summary>===================== A PERDA CONTA QUANDO ELA ACONTECEU =====================
    ///
    /// Venda fechada numa quinta, cancelada uma semana depois. A perda e da SEMANA SEGUINTE.
    ///
    /// Perda e um evento, e ele aconteceu no dia do cancelamento — e assim que o "Perdido" comum
    /// ja funciona, por `perdida_em`. Usar `ganha_em` poria a perda num mes JA FECHADO, e o mesmo
    /// fato apareceria em dois meses diferentes: o relatorio de Vendas mostra a cancelada pela
    /// data da VENDA, este mostraria pela mesma data, e nenhum dos dois diria quando o cliente
    /// desistiu.
    ///
    /// ⚠️ OS DOIS RECORTES, E E O PAR QUE PROVA. So afirmar que ela aparece na semana do
    /// cancelamento passaria numa versao sem filtro de data nenhum.
    /// ==============================================================================</summary>
    [Fact]
    public async Task A_PERDA_CONTA_NA_DATA_DO_CANCELAMENTO_E_NAO_NA_DA_VENDA()
    {
        var (db, tx, amb) = await PrepararAsync("can1-data");
        using var _ = db; using var __ = tx;

        var (_, venda) = await VendaAsync(db, amb, "desistiu", Local(Quinta, 10), 1000m);

        await amb.Vendas.CancelarAsync(venda, "Achou caro", default);

        // ⚠️ A DATA E IMPOSTA, nao herdada do relogio. `ContatosDbTests.Agora` e a PROPRIA quinta
        //    do cenario (06/08/2026), entao sem isto a venda e o cancelamento cairiam no mesmo dia
        //    e o teste passaria sem distinguir coisa nenhuma — que e exatamente o defeito que ele
        //    existe para pegar.
        var umaSemanaDepois = Quinta.AddDays(7);

        // ⚠️ O INSTANTE SAI PARA UM LOCAL. `Local(dia, hora)` tem parametro opcional, e argumento
        //    opcional dentro de arvore de expressao nao compila (CS0854).
        var quandoCancelou = Local(umaSemanaDepois, 10);
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.Id == venda)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.CanceladaEm, quandoCancelou));
        db.ChangeTracker.Clear();

        // No dia da VENDA: nao esta la.
        Assert.DoesNotContain(
            await amb.Relatorios.MotivosPerdaAsync(FiltroDe(Quinta, Quinta), default),
            l => l.Motivo == "Achou caro");

        // No dia do CANCELAMENTO: esta.
        Assert.Contains(
            await amb.Relatorios.MotivosPerdaAsync(
                FiltroDe(umaSemanaDepois, umaSemanaDepois), default),
            l => l.Motivo == "Achou caro");
    }

    /// <summary>"Registrei errado" NAO e perda, e nao pode entrar.
    ///
    /// ⚠️ E a metade do recorte que ninguem lembraria de testar: a tabela encheria de linhas
    /// "Sem motivo informado" com o valor de toda venda que alguem lancou errado, e o dono
    /// concluiria que esta perdendo dinheiro que nunca saiu de lugar nenhum.</summary>
    [Fact]
    public async Task CANCELAMENTO_SEM_MOTIVO_NAO_ENTRA_NO_RELATORIO_DE_PERDAS()
    {
        var (db, tx, amb) = await PrepararAsync("can1-engano");
        using var _ = db; using var __ = tx;

        var (_, venda) = await VendaAsync(db, amb, "engano", Local(Quinta, 10), 1000m);
        await amb.Vendas.CancelarAsync(venda, null, default);
        db.ChangeTracker.Clear();

        var umaSemanaDepois = Quinta.AddDays(7);

        // ⚠️ O INSTANTE SAI PARA UM LOCAL. `Local(dia, hora)` tem parametro opcional, e argumento
        //    opcional dentro de arvore de expressao nao compila (CS0854).
        var quandoCancelou = Local(umaSemanaDepois, 10);
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.Id == venda)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.CanceladaEm, quandoCancelou));
        db.ChangeTracker.Clear();

        // Nem no dia da venda, nem no do cancelamento: engano nao e perda de ninguem.
        Assert.Empty(await amb.Relatorios.MotivosPerdaAsync(FiltroDe(Quinta, Quinta), default));
        Assert.Empty(await amb.Relatorios.MotivosPerdaAsync(
            FiltroDe(umaSemanaDepois, umaSemanaDepois), default));
    }

    // ============================================================ 7 · clientes recorrentes
    /// <summary>Só existe por causa do NEG-1 — antes dele a segunda compra sobrescrevia a
    /// primeira, e a pergunta "quem compra de novo" não tinha resposta no banco.</summary>
    [Fact]
    public async Task CLIENTE_COM_DUAS_COMPRAS_aparece_UMA_VEZ_com_as_duas_somadas()
    {
        var (db, tx, amb) = await PrepararAsync("r7");
        using var _ = db; using var __ = tx;

        // João compra, reabre e compra de novo — o caminho exato do NEG-1.
        var joao = await amb.Contatos.CriarAsync(
            new NovoContato("João Recorrente", $"5584{Random.Shared.NextInt64(900000000, 999999999)}"), default);
        await amb.Contatos.MarcarGanhoAsync(joao, 5000m, null, null, default);
        // A regra e um card por funil: concluir o pedido libera o lugar.
        await ContatosDbTests.ConcluirGanhaAsync(db, amb.Vendas, joao);
await amb.Contatos.AbrirNegociacaoAsync(joao, null, default);
        await amb.Contatos.MarcarGanhoAsync(joao, 3000m, null, null, default);

        // Maria compra uma vez só — e NÃO pode aparecer.
        var maria = await amb.Contatos.CriarAsync(
            new NovoContato("Maria Única", $"5584{Random.Shared.NextInt64(900000000, 999999999)}"), default);
        await amb.Contatos.MarcarGanhoAsync(maria, 900m, null, null, default);

        db.ChangeTracker.Clear();
        var hoje = DateOnly.FromDateTime(ContatosDbTests.Agora.UtcDateTime);
        var pagina = await amb.Relatorios.ClientesRecorrentesAsync(FiltroDe(hoje, hoje), 1, 20, default);

        var linha = Assert.Single(pagina.Itens);
        Assert.Equal(joao, linha.ContatoId);
        Assert.Equal(2, linha.Compras);
        Assert.Equal(8000m, linha.Total);   // as DUAS, não só a última
    }

    // ============================================================ agregação
    /// <summary>===================== A REGRA QUE NÃO SE QUEBRA =====================
    ///
    /// Duas coisas, lidas do SQL de verdade e não de uma promessa em comentário:
    ///
    /// 1. NENHUMA FUNÇÃO SOBRE COLUNA EM FILTRO. `WHERE date_trunc('month', fechada_em) = $1`
    ///    devolve o mesmo resultado e descarta o índice — o planejador passa a calcular a
    ///    expressão linha a linha. `date_trunc` é legítimo no SELECT e no GROUP BY, onde roda
    ///    sobre o conjunto JÁ recortado.
    ///
    /// 2. NENHUMA AGREGAÇÃO EM MEMÓRIA. Se a consulta agrega, o `GROUP BY`/`SUM` tem que estar no
    ///    SQL. O `ServicoInbox` do Recupera materializa linhas antes de contar, e o próprio
    ///    comentário de lá admite que aquilo cresce.
    /// ======================================================================</summary>
    [Fact]
    public void NENHUMA_FUNCAO_SOBRE_COLUNA_EM_FILTRO()
    {
        var infracoes = new List<string>();

        // ⚠️ A CONSULTA DO OPERADOR ENTRA AQUI (OPE-1). Ela e a unica leitura do sistema que
        // atravessa todas as empresas, e ficar de fora desta regra a deixaria isenta do unico
        // controle que o repositorio aplica mecanicamente -- e a isencao seria invisivel.
        var consultas = ServicoRelatorios.ConsultasParaAuditoria
            .Concat(ServicoOperador.ConsultasParaAuditoria)
            .Concat(ServicoEvolucao.ConsultasParaAuditoria)
            .Concat(ServicoLeadsParados.ConsultasParaAuditoria)
            .Concat(ServicoRelatorioNps.ConsultasParaAuditoria);

        foreach (var (nome, sql) in consultas)
        {
            foreach (var trecho in ClausulasWhere(sql))
            {
                foreach (var funcao in new[] { "date_trunc(", "lower(", "upper(", "cast(", "::date" })
                {
                    if (trecho.Contains(funcao, StringComparison.OrdinalIgnoreCase))
                        infracoes.Add($"{nome}: `{funcao}` dentro de um WHERE -> {Resumo(trecho)}");
                }
            }
        }

        Assert.True(infracoes.Count == 0, string.Join("\n", infracoes));
    }

    [Fact]
    public void TODA_CONSULTA_QUE_AGREGA_AGREGA_NO_SQL()
    {
        var consultas = ServicoRelatorios.ConsultasParaAuditoria
            .Concat(ServicoOperador.ConsultasParaAuditoria)
            .Concat(ServicoEvolucao.ConsultasParaAuditoria)
            .Concat(ServicoLeadsParados.ConsultasParaAuditoria)
            .Concat(ServicoRelatorioNps.ConsultasParaAuditoria);

        foreach (var (nome, sql) in consultas)
        {
            var agrega = sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)
                      || sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase)
                      || sql.Contains("AVG(", StringComparison.OrdinalIgnoreCase);

            Assert.True(agrega, $"{nome}: relatório que não agrega no SQL agrega em memória.");
        }
    }

    /// <summary>As cláusulas WHERE do SQL, cada uma até o próximo marco (GROUP/ORDER/uma CTE
    /// nova). Grosseiro de propósito: um analisador de SQL de verdade não cabe num teste, e o
    /// recorte só precisa ser bom o bastante para pegar `date_trunc` no lugar errado.</summary>
    private static IEnumerable<string> ClausulasWhere(string sql)
    {
        var limpo = string.Join('\n', sql.Split('\n')
            .Select(l => l.Contains("--") ? l[..l.IndexOf("--", StringComparison.Ordinal)] : l));

        var i = 0;
        while ((i = limpo.IndexOf("WHERE", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var fim = new[] { "GROUP BY", "ORDER BY", "HAVING", "LIMIT", ")\n", "UNION" }
                .Select(m => limpo.IndexOf(m, i, StringComparison.OrdinalIgnoreCase))
                .Where(p => p > 0)
                .DefaultIfEmpty(limpo.Length)
                .Min();

            yield return limpo[i..Math.Min(fim, limpo.Length)];
            i += 5;
        }
    }

    private static string Resumo(string trecho) =>
        trecho.Replace('\n', ' ').Trim() is var t && t.Length > 120 ? t[..120] + "…" : t;

    // ==================================================================== apoio
    private static async Task<long> ContatoSimplesAsync(Ambiente amb, string nome) =>
        await amb.Contatos.CriarAsync(
            new NovoContato(nome, $"5584{Random.Shared.NextInt64(900000000, 999999999)}"), default);

    private static async Task<long> CanalDeTesteAsync(NexoraDbContext db, Ambiente amb, string nome)
    {
        var canal = new CanalCaptacao
        {
            EmpresaId = amb.Cenario.Id,
            Nome = nome,
            Codigo = Nexora.Core.Captacao.CodigoCanal.Gerar(),
            ConexaoId = amb.Cenario.Conexao.Id,
            Origem = OrigemLead.Qrcode
        };
        db.CanaisCaptacao.Add(canal);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return canal.Id;
    }

    /// <summary>===================== A GRANULARIDADE QUE MUDOU DE CASA (FUN-1) =====================
    ///
    /// `FunilDbTests` comparava o quadro com o painel COLUNA A COLUNA, e o comentário dele dizia
    /// por quê: um total agregado igual pode esconder duas diferenças que se cancelam entre
    /// colunas. O painel deixou de desenhar etapas, e essa conferência não tinha mais onde morar.
    ///
    /// ⚠️ ELA NÃO PODIA SIMPLESMENTE SUMIR. A "foto" do relatório é a QUINTA cópia do recorte do
    /// quadro, e a única escrita em SQL cru — ela não quebra quando as outras mudam, ela DIVERGE
    /// em silêncio. Foi o que aconteceu no POS-1: o card vendido que avança para a pós-venda
    /// aparecia no quadro e não no relatório, e ninguém soube por três semanas.
    ///
    /// Então a conferência mudou de casa junto com o dado: quadro contra `agora`, etapa por etapa.
    /// =========================================================================================</summary>
    [Fact]
    public async Task A_FOTO_DO_RELATORIO_BATE_COM_O_QUADRO_COLUNA_A_COLUNA()
    {
        var (db, tx, amb) = await PrepararAsync("foto-x-quadro");
        using var _ = db; using var __ = tx;

        // Dois cards em etapas diferentes, com valores distintos: um total agregado igual não
        // prova nada se as duas colunas puderem trocar de lugar entre si.
        var um = await LeadAsync(db, amb, "card-um", Local(Quinta, 9));
        var dois = await LeadAsync(db, amb, "card-dois", Local(Quinta, 9));

        // ⚠️ NÃO SE INSERE NEGOCIAÇÃO AQUI. `LeadAsync` já deixa uma aberta na primeira etapa, e o
        //    índice `uq_negociacoes_card_por_funil` é exatamente isto: UM card por pessoa por
        //    funil. Inserir a segunda levaria 23505, e o teste acusaria o produto por um erro da
        //    fixture.
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == um.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Valor, 100m));

        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == dois.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Valor, 250m)
                .SetProperty(n => n.EtapaId, amb.Cenario.Etapas[1].Id));

        db.ChangeTracker.Clear();

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var funil = await amb.Relatorios.FunilNoPeriodoAsync(FiltroDe(Quinta, Quinta), default);

        foreach (var coluna in quadro.Colunas)
        {
            var naFoto = funil.Agora.Single(e => e.EtapaId == coluna.EtapaId);

            Assert.True(coluna.Total == naFoto.Contatos,
                $"'{coluna.Nome}': quadro {coluna.Total}, relatório {naFoto.Contatos}.");
            Assert.True(coluna.ValorTotal == naFoto.Valor,
                $"'{coluna.Nome}': quadro {coluna.ValorTotal:C}, relatório {naFoto.Valor:C}.");
        }

        // E os números são os ESPERADOS, não apenas iguais: dois serviços igualmente errados
        // passariam no laço acima.
        Assert.Equal(100m, funil.Agora.Single(e => e.EtapaId == amb.Cenario.Etapas[0].Id).Valor);
        Assert.Equal(250m, funil.Agora.Single(e => e.EtapaId == amb.Cenario.Etapas[1].Id).Valor);
    }

    // ============================================================ FUN-1 · o funil agrupado
    /// <summary>===================== DUAS "PROPOSTA", DOIS PROCESSOS =====================
    ///
    /// O gráfico e a tabela do funil desenhavam as etapas de TODOS os funis em fila. Com dois
    /// funis saíam duas colunas "Proposta" vizinhas e nada dizia de quem era qual.
    ///
    /// ⚠️ E A SEQUÊNCIA VINHA INTERCALADA, que é o pior dos dois: `ordem` é única POR PIPELINE
    /// (`uq_etapas_ordem`), então ordenando só por ela as duas etapas de ordem 1 saem juntas, as de
    /// ordem 2 juntas, e a curva do funil deixa de ser curva. Nenhum agrupamento na tela conserta
    /// isso — o grupo só existe se as etapas de um funil vierem EM BLOCO.
    ///
    /// Este teste afirma as três coisas que o conserto precisa ter: o funil em cada linha, o funil
    /// CERTO, e os blocos sem intercalação.
    /// =============================================================================</summary>
    [Fact]
    public async Task O_FUNIL_DO_RELATORIO_DIZ_DE_QUAL_FUNIL_E_CADA_ETAPA()
    {
        var (db, tx, amb) = await PrepararAsync("funil-agrupado");
        using var _ = db; using var __ = tx;

        var (atacado, etapasAtacado) = await Semeador.SegundoFunilAsync(db, amb.Cenario);

        // ⚠️ CONTAGENS DIFERENTES NAS DUAS "PROPOSTA", de propósito: com o mesmo número, uma versão
        //    que trocasse os dois grupos de lugar passaria sem ninguém notar.
        var a = await LeadAsync(db, amb, "vendas-1", Local(Quinta, 9));
        var b = await LeadAsync(db, amb, "vendas-2", Local(Quinta, 9));
        var c = await LeadAsync(db, amb, "atacado-1", Local(Quinta, 9));

        await MoverParaAsync(db, a.Id, amb.Cenario.Etapas[1]);   // Proposta de Vendas
        await MoverParaAsync(db, b.Id, amb.Cenario.Etapas[1]);   // Proposta de Vendas
        await MoverParaAsync(db, c.Id, etapasAtacado[1]);        // Proposta de Atacado

        var funil = await amb.Relatorios.FunilNoPeriodoAsync(FiltroDe(Quinta, Quinta), default);

        // ===== 1. o funil vem em cada linha, e é o DONO da etapa =====
        // Comparar com o banco, e não com uma lista escrita aqui: um `Select` trocado devolveria
        // sempre o mesmo nome e uma verificação por amostragem não veria.
        foreach (var etapa in funil.Agora)
        {
            var dona = await db.EtapasFunil.AsNoTracking().SingleAsync(x => x.Id == etapa.EtapaId);
            Assert.Equal(dona.PipelineId, etapa.PipelineId);
        }
        foreach (var etapa in funil.Entradas)
        {
            var dona = await db.EtapasFunil.AsNoTracking().SingleAsync(x => x.Id == etapa.EtapaId);
            Assert.Equal(dona.PipelineId, etapa.PipelineId);
        }

        // ===== 2. as duas "Proposta" são distinguíveis, e não se misturaram =====
        var propostas = funil.Agora.Where(e => e.Nome == "Proposta")
            .OrderBy(e => e.PipelineNome).ToList();

        Assert.Equal(2, propostas.Count);
        Assert.Equal(["Atacado", "Vendas"], propostas.Select(e => e.PipelineNome).ToArray());
        Assert.Equal(1, propostas[0].Contatos);   // Atacado
        Assert.Equal(2, propostas[1].Contatos);   // Vendas

        Assert.Contains(funil.Agora, e => e.PipelineId == atacado.Id);

        // ===== 3. cada funil num bloco só, nas DUAS listas =====
        ExigirEmBlocos(funil.Agora.Select(e => e.PipelineId));
        ExigirEmBlocos(funil.Entradas.Select(e => e.PipelineId));
    }

    /// <summary>Nenhum funil reaparece depois de ter sido deixado para trás.
    ///
    /// ⚠️ NÃO AFIRMA UMA SEQUÊNCIA LITERAL. Prender o teste a qual funil vem primeiro o faria
    /// quebrar numa decisão de produto — ordem de exibição — em vez de num defeito. A propriedade
    /// é o que importa, e é exatamente o que a falta de desempate destrói.</summary>
    private static void ExigirEmBlocos(IEnumerable<long> pipelineIds)
    {
        var vistos = new List<long>();
        long? atual = null;

        foreach (var id in pipelineIds)
        {
            if (id == atual) continue;
            Assert.DoesNotContain(id, vistos);
            vistos.Add(id);
            atual = id;
        }
    }

    /// <summary>Empurra a negociação do contato para outra etapa — e para o FUNIL dela.
    ///
    /// ⚠️ `pipeline_id` JUNTO, SEMPRE. A negociação guarda o funil redundantemente (o quadro e o
    /// relatório não fazem join a cada consulta), e mover a etapa sem mover o funil deixaria a
    /// linha mentindo sobre onde está — com a `fk_negociacoes_etapa` composta reclamando, se
    /// tivermos sorte, ou um card fantasma no funil errado, se não.</summary>
    private static async Task MoverParaAsync(NexoraDbContext db, long contatoId, EtapaFunil destino)
    {
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contatoId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.EtapaId, destino.Id)
                .SetProperty(n => n.PipelineId, destino.PipelineId));
        db.ChangeTracker.Clear();
    }

    // ============================================================ FUN-1 · o filtro "Etapa"
    /// <summary>===================== O SELETOR QUE TROCA A RESPOSTA =====================
    ///
    /// Os outros tres lugares do FUN-1 confundem a LEITURA: duas barras com o mesmo nome, e o dono
    /// percebe que tem algo errado. Este aqui nao da sinal nenhum — a etapa escolhida recorta o
    /// relatorio INTEIRO, e pegar a do funil errado devolve numeros de outro processo, plausiveis
    /// demais para alguem conferir.
    ///
    /// ⚠️ SEM `SegundoFunilAsync` ESTE TESTE PASSARIA COM O DEFEITO NO LUGAR. O cenario padrao tem
    /// uma pipeline so: sem nome repetido, nao ha o que distinguir.
    /// =========================================================================</summary>
    [Fact]
    public async Task O_FILTRO_DE_ETAPA_DIZ_O_FUNIL_DE_CADA_UMA()
    {
        var (db, tx, amb) = await PrepararAsync("filtro-etapa");
        using var _ = db; using var __ = tx;

        var (atacado, _) = await Semeador.SegundoFunilAsync(db, amb.Cenario);

        var opcoes = await amb.Relatorios.OpcoesAsync(default);

        // Duas "Proposta" — uma em cada funil. O cadastro permite, e isso nao e descuido.
        var propostas = opcoes.Etapas.Where(e => e.Nome == "Proposta").ToList();
        Assert.Equal(2, propostas.Count);

        // ⚠️ A AFIRMACAO QUE IMPORTA: sao distinguiveis. Sem isto o dono escolhe no escuro.
        Assert.Equal(
            new[] { "Atacado", "Vendas" },
            propostas.Select(e => e.PipelineNome).OrderBy(n => n).ToArray());

        // E o funil declarado e mesmo o DONO da etapa, nao um rotulo que veio junto por acaso —
        // um `Select` trocado devolveria sempre o mesmo nome e o teste acima ainda passaria.
        foreach (var opcao in opcoes.Etapas)
        {
            var dona = await db.EtapasFunil.AsNoTracking().SingleAsync(x => x.Id == opcao.Id);
            Assert.Equal(dona.PipelineId, opcao.PipelineId);
        }

        Assert.Contains(opcoes.Etapas, e => e.PipelineId == atacado.Id);
    }

    /// <summary>===================== A ORDEM SEM DESEMPATE INTERCALA =====================
    ///
    /// `Ordem` e unica POR PIPELINE (`uq_etapas_ordem`). Ordenando so por ela, as duas etapas de
    /// ordem 1 saem juntas, as de ordem 2 juntas — e a lista vira "Novo Lead, Novo lead, Proposta,
    /// Proposta, Venda, Fechado". Nenhum `<optgroup>` conserta isso: o grupo so existe se as
    /// etapas de um funil vierem EM BLOCO.
    ///
    /// ⚠️ NAO AFIRMA UMA SEQUENCIA LITERAL. Afirmar "esta lista exata" prenderia o teste a qual
    /// funil vem primeiro, que e decisao de produto e pode mudar. Afirma a PROPRIEDADE: nenhum
    /// funil reaparece depois de ter sido deixado para tras.
    /// ==========================================================================</summary>
    [Fact]
    public async Task AS_ETAPAS_DE_UM_FUNIL_VEM_JUNTAS_E_NUNCA_INTERCALADAS()
    {
        var (db, tx, amb) = await PrepararAsync("etapas-em-bloco");
        using var _ = db; using var __ = tx;

        await Semeador.SegundoFunilAsync(db, amb.Cenario);

        var opcoes = await amb.Relatorios.OpcoesAsync(default);

        var blocos = new List<long>();
        long? atual = null;

        foreach (var etapa in opcoes.Etapas)
        {
            if (etapa.PipelineId == atual) continue;

            // Voltar a um funil ja encerrado E a intercalacao.
            Assert.DoesNotContain(etapa.PipelineId, blocos);
            blocos.Add(etapa.PipelineId);
            atual = etapa.PipelineId;
        }

        Assert.Equal(2, blocos.Count);

        // E dentro do bloco, na ordem do proprio funil — nao na ordem que o banco quiser.
        foreach (var grupo in opcoes.Etapas.GroupBy(e => e.PipelineId))
        {
            var esperado = await db.EtapasFunil.AsNoTracking()
                .Where(x => x.PipelineId == grupo.Key)
                .OrderBy(x => x.Ordem)
                .Select(x => x.Id)
                .ToListAsync();

            Assert.Equal(esperado, grupo.Select(e => e.Id).ToList());
        }
    }

    private static FiltroRelatorio FiltroDe(DateOnly de, DateOnly ate) =>
        new(de, ate, AgrupamentoSerie.Dia);

    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto,
        IServicoRelatorios Relatorios, IServicoVendas Vendas,
        IServicoContatos Contatos, IServicoFunil Funil);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(ContatosDbTests.Agora);
        var trilha = new ColetorAuditoria();

        var db = banco.NovoContexto(ctx, relogio, trilha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"rel-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        // O Semeador deixa um contato, uma conversa e uma mensagem. Todo teste aqui CONTA coisas,
        // então esse resto entraria na conta e faria os números baterem por acaso.
        await ZerarAsync(db, cenario.Id);

        return (db, tx, new Ambiente(
            cenario, ctx,
            new ServicoRelatorios(db, ctx, relogio),
            new ServicoVendas(db, ctx, trilha, relogio),
            new ServicoContatos(db, ctx, PublicadorDeTeste.Novo(db, relogio), PublicadorConversoesDeTeste.Novo(db, relogio), trilha, relogio),
            new ServicoFunil(db, PublicadorDeTeste.Novo(db, relogio), trilha)));
    }

    /// <summary>Ordem das exclusões: vendas antes de contatos (a FK é RESTRICT — apagar contato
    /// não pode levar faturamento junto), e mensagens antes de lembretes e conversas.</summary>
    private static async Task ZerarAsync(NexoraDbContext db, long empresaId)
    {
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Lembretes.IgnoreQueryFilters().Where(l => l.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Negociacoes.IgnoreQueryFilters().Where(v => v.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Auditoria.IgnoreQueryFilters().Where(a => a.EmpresaId == empresaId).ExecuteDeleteAsync();
        // A negociacao sai ANTES do contato: `fk_negociacoes_contato` e `Restrict`, porque a
        // negociacao e o registro do negocio e o contato nao pode leva-la junto ao sumir.
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Contato com `criado_em` FIXADO por UPDATE.
    ///
    /// O `InterceptorAuditoria` sobrescreve `CriadoEm` com o relógio em todo INSERT — é o que
    /// impede um caminho de escrita de esquecer a coluna. Aqui isso trabalha contra o teste, que
    /// precisa de datas espalhadas, então o valor é imposto depois.</summary>
    private static async Task<Contato> LeadAsync(
        NexoraDbContext db, Ambiente amb, string marca, DateTime criadoEm,
        long? responsavelId = null, OrigemLead origem = OrigemLead.Manual)
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

        // ⚠️ A NEGOCIACAO NASCE JUNTO (E4d). Os relatorios passaram a ler `negociacoes`; um
        // contato sem ela nao aparece em numero nenhum, e o teste falha dizendo "esperado 3, veio
        // 0" sem nenhuma pista de que o problema e a fixture.
        db.Negociacoes.Add(Semeador.Negocio(contato, amb.Cenario.Etapas[0]));

        await db.SaveChangesAsync();

        await db.Contatos.IgnoreQueryFilters().Where(x => x.Id == contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CriadoEm, criadoEm));

        db.ChangeTracker.Clear();
        return contato;
    }

    /// <summary>Contato COM venda, com `fechada_em` fixada. Devolve o contato e o id da venda.
    ///
    /// Grava a linha de `vendas` direto em vez de chamar `MarcarGanhoAsync`: o serviço usa o
    /// relógio, que está congelado num instante só, e estes testes precisam de vendas espalhadas
    /// no calendário.</summary>
    private static async Task<(Contato Contato, long VendaId)> VendaAsync(
        NexoraDbContext db, Ambiente amb, string marca, DateTime fechadaEm, decimal valor,
        long? responsavelId = null, OrigemLead origem = OrigemLead.Manual)
    {
        var contato = await LeadAsync(db, amb, marca, fechadaEm, responsavelId, origem);
        var etapaGanho = await db.EtapasFunil.AsNoTracking().FirstAsync(e => e.EGanho);

        // ⚠️ UMA LINHA SO (E4e). Antes esta fixture gravava a `Venda` E atualizava a negociacao
        // espelho — as duas metades do mesmo fato. Agora a negociacao aberta que `LeadAsync`
        // criou simplesmente vira ganha, que e o que `MarcarGanhoAsync` faz.
        var etapaGanhoId = etapaGanho.Id;
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contato.Id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(n => n.Status, StatusNegociacao.Ganha)
                .SetProperty(n => n.GanhaEm, fechadaEm)
                .SetProperty(n => n.Valor, valor)
                .SetProperty(n => n.EtapaId, etapaGanhoId)
                .SetProperty(n => n.ResponsavelId, responsavelId));

        var negociacaoId = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.ContatoId == contato.Id).Select(n => n.Id).FirstAsync();

        db.ChangeTracker.Clear();
        return (contato, negociacaoId);
    }

    private static async Task<Contato> PerdidoAsync(
        NexoraDbContext db, Ambiente amb, string marca, DateTime perdidoEm,
        long? responsavelId, string motivo, decimal? valor = null)
    {
        var contato = await LeadAsync(db, amb, marca, perdidoEm, responsavelId);

        // O relatório de motivos conta NEGÓCIO perdido, e é dele que o motivo e o valor saem.
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contato.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, StatusNegociacao.Perdida)
                .SetProperty(n => n.PerdidaEm, perdidoEm)
                .SetProperty(n => n.MotivoPerda, motivo)
                .SetProperty(n => n.Valor, valor));

        db.ChangeTracker.Clear();
        return contato;
    }

    /// <summary>Um segundo vendedor. O `Cenario` do semeador traz só o dono, e os testes de
    /// papel precisam de alguém para NÃO ver.</summary>
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

    private static async Task<Conversa> ConversaAsync(NexoraDbContext db, Ambiente amb, Contato contato)
    {
        var conversa = new Conversa
        {
            EmpresaId = amb.Cenario.Id, ContatoId = contato.Id, ConexaoId = amb.Cenario.Conexao.Id,
            UltimaMensagemEm = ContatosDbTests.Agora.UtcDateTime
        };
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return conversa;
    }

    /// <param name="peloCelular">A saida chega pelo WEBHOOK, mandada do proprio WhatsApp do
    /// vendedor: humana e SEM `enviado_por`, porque o webhook nao sabe qual usuario do painel
    /// seria.</param>
    private static async Task MensagemAsync(
        NexoraDbContext db, Ambiente amb, Conversa conversa, DirecaoMensagem direcao, DateTime quando,
        OrigemMensagem origem = OrigemMensagem.Humana, TipoAutomacao? automacao = null,
        bool peloCelular = false, bool tratada = false)
    {
        var entrada = direcao == DirecaoMensagem.Entrada;
        var msg = new Mensagem
        {
            EmpresaId = amb.Cenario.Id,
            Origem = origem,
            TipoAutomacao = automacao,
            // A entrada que a pesquisa de NPS consumiu como nota (`LeituraDaResposta`).
            TratadaPorAutomacao = tratada,
            ConversaId = conversa.Id,
            ContatoId = conversa.ContatoId,
            // `instance_name` é NOT NULL: a mensagem pertence ao número que a enviou, e sem isso
            // o reenvio não saberia por qual conexão sair.
            ConexaoId = amb.Cenario.Conexao.Id,
            InstanceName = amb.Cenario.Conexao.InstanceName,
            Direcao = direcao,
            Texto = entrada ? "oi" : "opa",
            // `ck_msg_data_disparo` exige a data em toda SAÍDA: ela é a chave do teto diário de
            // disparos, e mensagem que sai sem ela escaparia do teto.
            DataDisparo = entrada ? null : DateOnly.FromDateTime(quando),
            // Quem RESPONDEU. É o que o relatório 5 agrupa — a coluna chama `enviado_por`, e é
            // NULA em entrada e em disparo automático.
            EnviadoPor = entrada || peloCelular ? null : amb.Contexto.UsuarioId,
            RecebidaEm = entrada ? quando : null,
            EnviadaEm = entrada ? null : quando
        };
        db.Mensagens.Add(msg);
        await db.SaveChangesAsync();

        await db.Mensagens.IgnoreQueryFilters().Where(m => m.Id == msg.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CriadoEm, quando));

        db.ChangeTracker.Clear();
    }
}
