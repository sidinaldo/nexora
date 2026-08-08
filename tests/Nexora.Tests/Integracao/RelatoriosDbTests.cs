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
        await amb.Vendas.CancelarAsync(vCancelar, default);
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
        await amb.Funil.MoverAsync(arrastado, new MoverContato(proposta.Id, null), default);

        // Porta 2: registrar venda — move para a etapa de ganho sem passar pelo `MoverAsync`.
        var vendido = await amb.Contatos.CriarAsync(
            new NovoContato("Vendido", $"5584{Random.Shared.NextInt64(900000000, 999999999)}"), default);
        await amb.Contatos.MarcarGanhoAsync(vendido, 500m, default);

        db.ChangeTracker.Clear();
        var hoje = DateOnly.FromDateTime(ContatosDbTests.Agora.UtcDateTime);
        var r = await amb.Relatorios.FunilNoPeriodoAsync(FiltroDe(hoje, hoje), default);

        Assert.Equal(1, r.Entradas.Single(e => e.EtapaId == proposta.Id).Entradas);
        Assert.Equal(1, r.Entradas.Single(e => e.EtapaId == etapaGanho.Id).Entradas);

        // E a FOTO vem junto, rotulada separadamente — "entrou no período" e "está agora" são
        // perguntas diferentes, e misturá-las é o que o prompt proíbe.
        Assert.Equal(1, r.Agora.Single(e => e.EtapaId == proposta.Id).Contatos);
    }

    // ============================================================ 5 · tempo de resposta
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
        await amb.Contatos.MarcarGanhoAsync(joao, 5000m, default);
        await amb.Contatos.ReabrirAsync(joao, default);
        await amb.Contatos.MarcarGanhoAsync(joao, 3000m, default);

        // Maria compra uma vez só — e NÃO pode aparecer.
        var maria = await amb.Contatos.CriarAsync(
            new NovoContato("Maria Única", $"5584{Random.Shared.NextInt64(900000000, 999999999)}"), default);
        await amb.Contatos.MarcarGanhoAsync(maria, 900m, default);

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

        foreach (var (nome, sql) in ServicoRelatorios.ConsultasParaAuditoria)
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
        foreach (var (nome, sql) in ServicoRelatorios.ConsultasParaAuditoria)
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
            new ServicoRelatorios(db, ctx),
            new ServicoVendas(db, ctx, trilha, relogio),
            new ServicoContatos(db, ctx, PublicadorDeTeste.Novo(db, relogio), trilha, relogio),
            new ServicoFunil(db, PublicadorDeTeste.Novo(db, relogio), trilha)));
    }

    /// <summary>Ordem das exclusões: vendas antes de contatos (a FK é RESTRICT — apagar contato
    /// não pode levar faturamento junto), e mensagens antes de lembretes e conversas.</summary>
    private static async Task ZerarAsync(NexoraDbContext db, long empresaId)
    {
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Lembretes.IgnoreQueryFilters().Where(l => l.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Vendas.IgnoreQueryFilters().Where(v => v.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Auditoria.IgnoreQueryFilters().Where(a => a.EmpresaId == empresaId).ExecuteDeleteAsync();
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
            EtapaId = amb.Cenario.Etapas[0].Id,
            ResponsavelId = responsavelId,
            Origem = origem,
            OrdemKanban = 1000m
        };
        db.Contatos.Add(contato);
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

        var venda = new Venda
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = contato.Id,
            Valor = valor,
            FechadaEm = fechadaEm,
            ResponsavelId = responsavelId,
            EtapaId = etapaGanho.Id
        };
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();

        // O carimbo do contato acompanha a linha — é o par que o NEG-1 mantém junto.
        await db.Contatos.IgnoreQueryFilters().Where(x => x.Id == contato.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.GanhoEm, fechadaEm)
                .SetProperty(x => x.Valor, valor)
                .SetProperty(x => x.EtapaId, etapaGanho.Id));

        db.ChangeTracker.Clear();
        return (contato, venda.Id);
    }

    private static async Task<Contato> PerdidoAsync(
        NexoraDbContext db, Ambiente amb, string marca, DateTime perdidoEm,
        long? responsavelId, string motivo, decimal? valor = null)
    {
        var contato = await LeadAsync(db, amb, marca, perdidoEm, responsavelId);

        await db.Contatos.IgnoreQueryFilters().Where(x => x.Id == contato.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.PerdidoEm, perdidoEm)
                .SetProperty(x => x.MotivoPerda, motivo)
                .SetProperty(x => x.Valor, valor));

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

    private static async Task MensagemAsync(
        NexoraDbContext db, Ambiente amb, Conversa conversa, DirecaoMensagem direcao, DateTime quando)
    {
        var entrada = direcao == DirecaoMensagem.Entrada;
        var msg = new Mensagem
        {
            EmpresaId = amb.Cenario.Id,
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
            EnviadoPor = entrada ? null : amb.Contexto.UsuarioId,
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
