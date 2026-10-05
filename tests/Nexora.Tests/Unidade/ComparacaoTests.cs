using Nexora.Core.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>===================== CMP-1 · O PERÍODO ANTERIOR =====================
///
/// Função pura, e é por isso que este arquivo não tem banco: as bordas que de fato quebram são de
/// CALENDÁRIO — mês curto, virada de ano, semana em curso —, e exercitá-las contra o Postgres seria
/// pagar meio segundo por caso para testar aritmética de datas.
/// ==============================================================</summary>
public class PeriodoAnteriorTests
{
    // 04/10/2026 é um DOMINGO. O dia da semana não importa nos casos de mês, mas importa nos de
    // semana — e errar isto foi o que fez o primeiro teste de semana apontar para o bug errado.
    private static readonly DateOnly Hoje = new(2026, 10, 4);

    /// <summary>Mês fechado desloca para o mês de calendário anterior — não para "os 30 dias
    /// anteriores", que cairia no meio de agosto.</summary>
    [Fact]
    public void MES_FECHADO_COMPARA_COM_O_MES_ANTERIOR_INTEIRO()
    {
        var j = PeriodoAnterior.Calcular(new(2026, 9, 1), new(2026, 9, 30), Hoje);

        Assert.Equal(new DateOnly(2026, 8, 1), j.AnteriorDe);
        Assert.Equal(new DateOnly(2026, 8, 31), j.AnteriorAte);
        Assert.False(j.EmAndamento);
        Assert.Equal(new DateOnly(2026, 9, 30), j.Ate);
    }

    /// <summary>⚠️ O CASO QUE MAIS ERRA. Em 04/10, "outubro" é 01–04/10 contra 01–04/09. Comparar
    /// quatro dias com setembro inteiro mostraria uma queda de ~87% no dia 4 de todo mês — cada
    /// conta certa, a pergunta errada.</summary>
    [Fact]
    public void MES_EM_ANDAMENTO_COMPARA_OS_DIAS_DECORRIDOS()
    {
        var j = PeriodoAnterior.Calcular(new(2026, 10, 1), new(2026, 10, 31), Hoje);

        Assert.True(j.EmAndamento);
        Assert.Equal(new DateOnly(2026, 10, 1), j.De);
        Assert.Equal(new DateOnly(2026, 10, 4), j.Ate);
        Assert.Equal(new DateOnly(2026, 9, 1), j.AnteriorDe);
        Assert.Equal(new DateOnly(2026, 9, 4), j.AnteriorAte);
    }

    /// <summary>⚠️ 31/03 → FEVEREIRO. Sem o clamp, montar o dia 31 de fevereiro estoura — e o
    /// relatório de março morreria com exceção de argumento, no mês inteiro.</summary>
    [Theory]
    [InlineData(2026, 28)]   // ano comum
    [InlineData(2024, 29)]   // bissexto
    public void MES_ANTERIOR_MAIS_CURTO_PARA_NO_ULTIMO_DIA_DELE(int ano, int ultimoDeFevereiro)
    {
        var j = PeriodoAnterior.Calcular(new(ano, 3, 1), new(ano, 3, 31), new(ano, 10, 4));

        Assert.Equal(new DateOnly(ano, 2, 1), j.AnteriorDe);
        Assert.Equal(new DateOnly(ano, 2, ultimoDeFevereiro), j.AnteriorAte);
    }

    /// <summary>E o mesmo clamp vale com o mês EM ANDAMENTO no dia 31.</summary>
    [Fact]
    public void MARCO_EM_ANDAMENTO_NO_DIA_31_TAMBEM_PARA_EM_FEVEREIRO()
    {
        var j = PeriodoAnterior.Calcular(new(2026, 3, 1), new(2026, 3, 31), new(2026, 3, 31));

        Assert.True(j.EmAndamento);
        Assert.Equal(new DateOnly(2026, 2, 1), j.AnteriorDe);
        Assert.Equal(new DateOnly(2026, 2, 28), j.AnteriorAte);
    }

    [Fact]
    public void JANEIRO_COMPARA_COM_DEZEMBRO_DO_ANO_ANTERIOR()
    {
        var j = PeriodoAnterior.Calcular(new(2026, 1, 1), new(2026, 1, 31), new(2026, 10, 4));

        Assert.Equal(new DateOnly(2025, 12, 1), j.AnteriorDe);
        Assert.Equal(new DateOnly(2025, 12, 31), j.AnteriorAte);
    }

    /// <summary>Intervalo qualquer desloca por N DIAS, imediatamente antes.</summary>
    [Fact]
    public void INTERVALO_DE_N_DIAS_COMPARA_COM_OS_N_DIAS_ANTES()
    {
        var j = PeriodoAnterior.Calcular(new(2026, 9, 10), new(2026, 9, 19), Hoje);

        Assert.Equal(new DateOnly(2026, 8, 31), j.AnteriorDe);
        Assert.Equal(new DateOnly(2026, 9, 9), j.AnteriorAte);
    }

    /// <summary>Semana de segunda a domingo — a mesma que o Postgres usa ao truncar por semana.</summary>
    [Fact]
    public void SEMANA_FECHADA_COMPARA_COM_A_SEMANA_ANTERIOR()
    {
        // 05/10/2026 é segunda; 11/10 é domingo.
        var j = PeriodoAnterior.Calcular(new(2026, 10, 5), new(2026, 10, 11), new(2026, 10, 20));

        Assert.Equal(new DateOnly(2026, 9, 28), j.AnteriorDe);
        Assert.Equal(new DateOnly(2026, 10, 4), j.AnteriorAte);
    }

    /// <summary>⚠️ É POR ISTO QUE A SEMANA TEM RAMO PRÓPRIO. Numa semana CHEIA, deslocar 7 dias e
    /// deslocar N dias dão o mesmo resultado — numa semana EM CURSO, não: na quarta, a regra de N
    /// dias compararia segunda–quarta com sexta–domingo da semana passada.</summary>
    [Fact]
    public void SEMANA_EM_ANDAMENTO_COMPARA_OS_MESMOS_DIAS_DA_SEMANA_ANTERIOR()
    {
        // Semana de 05/10 (segunda) a 11/10 (domingo), e hoje é quarta, 07/10.
        var j = PeriodoAnterior.Calcular(new(2026, 10, 5), new(2026, 10, 11), new(2026, 10, 7));

        Assert.True(j.EmAndamento);
        Assert.Equal(new DateOnly(2026, 10, 7), j.Ate);
        Assert.Equal(new DateOnly(2026, 9, 28), j.AnteriorDe);
        Assert.Equal(new DateOnly(2026, 9, 30), j.AnteriorAte);
    }

    /// <summary>Um pedido que ATRAVESSA dois meses não é "um mês", mesmo começando no dia 1.</summary>
    [Fact]
    public void INTERVALO_QUE_ATRAVESSA_MESES_NAO_VIRA_COMPARACAO_DE_MES()
    {
        var j = PeriodoAnterior.Calcular(new(2026, 9, 1), new(2026, 10, 31), Hoje);

        // Efetivo: 01/09 a 04/10 = 34 dias. Anterior: os 34 dias antes de 01/09.
        Assert.Equal(new DateOnly(2026, 10, 4), j.Ate);
        Assert.Equal(new DateOnly(2026, 8, 31), j.AnteriorAte);
        Assert.Equal(new DateOnly(2026, 7, 29), j.AnteriorDe);
    }
}

/// <summary>A conta da variação, e o cruzamento que decide a COR.</summary>
public class ComparacaoTests
{
    private static readonly DateOnly De = new(2026, 8, 1);
    private static readonly DateOnly Ate = new(2026, 8, 31);

    private static IndicadorComparativo Sobe(decimal atual, decimal anterior) =>
        Comparacao.De(atual, anterior, SentidoBom.Sobe, De, Ate);

    private static IndicadorComparativo Desce(decimal atual, decimal anterior) =>
        Comparacao.De(atual, anterior, SentidoBom.Desce, De, Ate);

    [Fact]
    public void SUBIR_NUM_INDICADOR_QUE_DEVE_SUBIR_E_MELHOR()
    {
        var i = Sobe(24000m, 18000m);

        Assert.Equal(6000m, i.VariacaoAbsoluta);
        Assert.Equal(33.3m, i.VariacaoPercentual);
        Assert.Equal("subiu", i.Tendencia);
        Assert.Equal("melhor", i.Avaliacao);
    }

    [Fact]
    public void CAIR_NUM_INDICADOR_QUE_DEVE_SUBIR_E_PIOR()
    {
        var i = Sobe(18000m, 24000m);

        Assert.Equal(-6000m, i.VariacaoAbsoluta);
        Assert.Equal(-25m, i.VariacaoPercentual);
        Assert.Equal("caiu", i.Tendencia);
        Assert.Equal("pior", i.Avaliacao);
    }

    /// <summary>===================== A REGRA QUE A COR SEGUE =====================
    ///
    /// ⚠️ "CANCELADO" CAINDO É SETA PARA BAIXO E NOTÍCIA BOA. É o par que prova que `Tendencia` e
    /// `Avaliacao` são campos separados: se a cor seguisse a seta, a tela pintaria de vermelho a
    /// melhor coisa que aconteceu no mês.
    /// ==============================================================</summary>
    [Fact]
    public void CAIR_NUM_INDICADOR_QUE_DEVE_DESCER_E_MELHOR()
    {
        var i = Desce(900m, 1600m);

        Assert.Equal("caiu", i.Tendencia);
        Assert.Equal("melhor", i.Avaliacao);
    }

    [Fact]
    public void SUBIR_NUM_INDICADOR_QUE_DEVE_DESCER_E_PIOR()
    {
        var i = Desce(2100m, 1600m);

        Assert.Equal("subiu", i.Tendencia);
        Assert.Equal("pior", i.Avaliacao);
    }

    /// <summary>⚠️ DIVIDIR POR ZERO NÃO É −100% NEM INFINITO. O percentual vem NULO, e quem decide
    /// o que isso significa é a tela — e não é a mesma frase em todo indicador: "sem cancelamento
    /// em ago" não é "novo".</summary>
    [Fact]
    public void SEM_NADA_ANTES_O_PERCENTUAL_E_NULO()
    {
        var i = Sobe(3200m, 0m);

        Assert.Null(i.VariacaoPercentual);
        Assert.Equal(3200m, i.VariacaoAbsoluta);
        Assert.Equal("subiu", i.Tendencia);
        Assert.Equal("melhor", i.Avaliacao);
    }

    [Fact]
    public void ZERO_CONTRA_ZERO_E_ESTAVEL_E_NEUTRO()
    {
        var i = Sobe(0m, 0m);

        Assert.Null(i.VariacaoPercentual);
        Assert.Equal(0m, i.VariacaoAbsoluta);
        Assert.Equal("estavel", i.Tendencia);
        Assert.Equal("neutro", i.Avaliacao);
    }

    /// <summary>Mesmo número nos dois períodos: nada subiu, nada é melhor nem pior.</summary>
    [Fact]
    public void IGUAL_E_ESTAVEL_E_NEUTRO_MESMO_COM_VALOR()
    {
        var i = Desce(1600m, 1600m);

        Assert.Equal(0m, i.VariacaoPercentual);
        Assert.Equal("estavel", i.Tendencia);
        Assert.Equal("neutro", i.Avaliacao);
    }

    /// <summary>As datas do anterior viajam no indicador: é com elas que a tela escreve "em ago" e
    /// monta o tooltip. Sem elas, a tela recalcularia o período — uma segunda cópia da regra de
    /// calendário, no lugar mais fácil de divergir.</summary>
    [Fact]
    public void O_INDICADOR_CARREGA_AS_DATAS_DO_PERIODO_ANTERIOR()
    {
        var i = Sobe(1m, 1m);

        Assert.Equal(De, i.AnteriorDe);
        Assert.Equal(Ate, i.AnteriorAte);
    }
}
