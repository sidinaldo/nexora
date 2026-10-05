using Nexora.Core.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>===================== EVO-1 · SINAL OU RUÍDO =====================
///
/// Função pura, e é por isso que este arquivo não tem banco: as regras que decidem se um número
/// MERECE virar julgamento sobre uma pessoa precisam ser exercitadas nos limites exatos — 9 contra
/// 10 decididos, 2,9 contra 3,0 pontos —, e montar um cenário de Postgres para cada borda faria o
/// teste medir a semeadura em vez da regra.
///
/// ⚠️ A FÁBRICA `RegrasTendencia.Mes` É O ALVO, e não só `De`. Na primeira versão a conta da
/// amostra insuficiente morava no serviço e só a constante ficava aqui: o teste recebia a marca
/// como ENTRADA e, por isso, subir o limiar de 10 para 50 não derrubava teste nenhum. A regra
/// estava partida em dois arquivos e o teste não cobria a metade que importa.
/// ==============================================================</summary>
public class RegrasTendenciaTests
{
    /// <summary>Um mês fechado com 100 decididos — assim `ganhos` LÊ-SE como porcentagem e a
    /// aritmética do teste não concorre com a regra pela atenção de quem lê.</summary>
    private static MesDaConversao Pct(int mes, int porcento) =>
        RegrasTendencia.Mes(2026, mes, decididos: 100, ganhos: porcento, parcial: false);

    // ==================================================================== a faixa morta

    /// <summary>⚠️ O LIMITE É INCLUSIVO, e a borda é o teste. Três pontos exatos é "melhorando";
    /// um `>` em vez de `>=` deixaria este caso cair em "estável", com o número +3,0 p.p. escrito
    /// ao lado do selo cinza — a tela se contradizendo na mesma linha.</summary>
    [Fact]
    public void TRES_PONTOS_EXATOS_JA_E_MELHORANDO()
    {
        var (variacao, tendencia) = RegrasTendencia.De(
            [Pct(1, 20), Pct(2, 20), Pct(3, 20), Pct(4, 23), Pct(5, 23), Pct(6, 23)]);

        Assert.Equal(3.0, variacao);
        Assert.Equal("melhorando", tendencia);
    }

    /// <summary>A décima parte de ponto abaixo do limite continua estável.
    ///
    /// ⚠️ É ESTE TESTE QUE PAGA A FAIXA MORTA. Sem ela, 2,9 pontos viram "melhorando", 0,4 vira
    /// "melhorando" e o selo pisca de cor todo mês sem nada ter acontecido no mundo.</summary>
    [Fact]
    public void DOIS_PONTOS_E_NOVE_AINDA_E_ESTAVEL()
    {
        var mil = (int mes, int ganhos) =>
            RegrasTendencia.Mes(2026, mes, decididos: 1000, ganhos: ganhos, parcial: false);

        var (variacao, tendencia) = RegrasTendencia.De(
            [mil(1, 200), mil(2, 200), mil(3, 200), mil(4, 229), mil(5, 229), mil(6, 229)]);

        Assert.Equal(2.9, variacao);
        Assert.Equal("estavel", tendencia);
    }

    /// <summary>A queda tem o mesmo limite, e também inclusivo.</summary>
    [Fact]
    public void TRES_PONTOS_PARA_BAIXO_JA_E_PIORANDO()
    {
        var (variacao, tendencia) = RegrasTendencia.De(
            [Pct(1, 23), Pct(2, 23), Pct(3, 23), Pct(4, 20), Pct(5, 20), Pct(6, 20)]);

        Assert.Equal(-3.0, variacao);
        Assert.Equal("piorando", tendencia);
    }

    // ==================================================================== quantos meses bastam

    /// <summary>⚠️ TRÊS MESES VÁLIDOS NÃO DÃO TENDÊNCIA, e a variação vem NULA — não zero. Zero
    /// seria lido como "não mudou", que é uma afirmação; nulo é a recusa de afirmar.</summary>
    [Fact]
    public void COM_TRES_MESES_VALIDOS_NAO_HA_TENDENCIA()
    {
        var (variacao, tendencia) = RegrasTendencia.De([Pct(1, 20), Pct(2, 30), Pct(3, 40)]);

        Assert.Null(variacao);
        Assert.Equal("sem_dados", tendencia);
    }

    /// <summary>Quatro bastam, e aí o grupo "anterior" tem um mês só.
    ///
    /// ⚠️ É O CASO QUE QUEBRA UMA IMPLEMENTAÇÃO DE 3 CONTRA 3 FIXOS: exigir seis meses faria a
    /// pessoa com quatro cair em "sem dados" com quatro meses de dados bons na tela ao lado.</summary>
    [Fact]
    public void COM_QUATRO_O_GRUPO_ANTERIOR_TEM_UM_MES_SO()
    {
        var (variacao, tendencia) = RegrasTendencia.De(
            [Pct(1, 10), Pct(2, 40), Pct(3, 40), Pct(4, 40)]);

        Assert.Equal(30.0, variacao);
        Assert.Equal("melhorando", tendencia);
    }

    // ==================================================================== o limiar de amostra

    /// <summary>===================== 9 NÃO VOTA, 10 VOTA =====================
    /// O par é o teste. Os quatro primeiros meses são 20% e sozinhos dão "estável"; o quinto é
    /// 100%, e se ele votar a tendência vira "melhorando".
    ///
    /// ⚠️ UM TESTE SÓ NÃO BASTARIA. Afirmar apenas que 9 não vota passaria numa versão que
    /// descartasse TODO mês; afirmar apenas que 10 vota passaria numa que não descartasse nenhum.
    /// A borda existe entre os dois.
    /// ==============================================================</summary>
    [Theory]
    [InlineData(9, "estavel", 0.0)]
    [InlineData(10, "melhorando", 26.7)]
    public void A_BORDA_DA_AMOSTRA_MINIMA_ESTA_ENTRE_NOVE_E_DEZ(
        int decididos, string esperada, double variacaoEsperada)
    {
        var ultimo = RegrasTendencia.Mes(
            2026, 5, decididos: decididos, ganhos: decididos, parcial: false);

        var (variacao, tendencia) = RegrasTendencia.De(
            [Pct(1, 20), Pct(2, 20), Pct(3, 20), Pct(4, 20), ultimo]);

        Assert.Equal(esperada, tendencia);
        Assert.Equal(variacaoEsperada, variacao);
    }

    /// <summary>O mês de amostra pequena fica MARCADO, não apagado: a tela o mostra com o volume ao
    /// lado, e é o volume que explica por que ele não vale.</summary>
    [Fact]
    public void AMOSTRA_PEQUENA_E_MARCADA_MAS_O_NUMERO_CONTINUA_LA()
    {
        var m = RegrasTendencia.Mes(2026, 5, decididos: 3, ganhos: 2, parcial: false);

        Assert.True(m.AmostraInsuficiente);
        Assert.Equal(2d / 3d, m.Conversao);
        Assert.Equal(3, m.Decididos);
        Assert.False(RegrasTendencia.Vota(m));
    }

    // ==================================================================== o mês em andamento

    /// <summary>⚠️ O MÊS EM ANDAMENTO ESTÁ PELA METADE, e metade de um mês comparada com meses
    /// inteiros inventa uma queda todo dia 5. Mesmo cenário do teste de amostra: com 100 decididos
    /// ele tem tamanho de sobra e continua fora — o que prova que a exclusão é pela marca de
    /// parcial, e não pelo volume.</summary>
    [Fact]
    public void O_MES_EM_ANDAMENTO_NAO_VOTA_NEM_COM_VOLUME_DE_SOBRA()
    {
        var corrente = RegrasTendencia.Mes(
            2026, 5, decididos: 100, ganhos: 100, parcial: true);

        var (variacao, tendencia) = RegrasTendencia.De(
            [Pct(1, 20), Pct(2, 20), Pct(3, 20), Pct(4, 20), corrente]);

        Assert.Equal(0.0, variacao);
        Assert.Equal("estavel", tendencia);
        Assert.False(RegrasTendencia.Vota(corrente));
    }

    // ==================================================================== nulo não é zero

    /// <summary>===================== AUSÊNCIA NÃO É FRACASSO =====================
    /// ⚠️ ZERO DECIDIDOS DÁ CONVERSÃO NULA E `AmostraInsuficiente` FALSO. São dois estados que a
    /// tela pinta diferente: "0%" é um resultado ruim, "—" é a falta de resultado. E "amostra
    /// insuficiente" num mês vazio acusaria de pouco quem não teve nada para decidir.
    ///
    /// O relatório antigo devolve `0` nos dois casos — é a diferença que esta tela existe para
    /// mostrar.
    /// ==============================================================</summary>
    [Fact]
    public void MES_VAZIO_TEM_CONVERSAO_NULA_E_NAO_E_AMOSTRA_PEQUENA()
    {
        var vazio = RegrasTendencia.Mes(2026, 5, decididos: 0, ganhos: 0, parcial: false);

        Assert.Null(vazio.Conversao);
        Assert.False(vazio.AmostraInsuficiente);
        Assert.False(RegrasTendencia.Vota(vazio));
    }

    /// <summary>Zero por cento com volume de sobra VOTA — e tinha de votar: é o mês ruim de
    /// verdade, e descartá-lo deixaria a tendência de quem parou de fechar parecendo intacta.</summary>
    [Fact]
    public void ZERO_POR_CENTO_COM_VOLUME_VOTA()
    {
        var ruim = RegrasTendencia.Mes(2026, 5, decididos: 40, ganhos: 0, parcial: false);

        Assert.Equal(0d, ruim.Conversao);
        Assert.True(RegrasTendencia.Vota(ruim));

        var (variacao, tendencia) = RegrasTendencia.De(
            [Pct(1, 20), Pct(2, 20), Pct(3, 20), Pct(4, 20), ruim]);

        Assert.Equal(-6.7, variacao);
        Assert.Equal("piorando", tendencia);
    }
}
