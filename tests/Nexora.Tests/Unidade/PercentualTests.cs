using Nexora.Core.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>A convenção de percentual do produto (AUD-1): 0 a 100, 2 casas, meio para cima, e
/// NULL sem denominador.</summary>
public class PercentualTests
{
    [Fact]
    public void SEM_DENOMINADOR_E_NULO_E_NAO_ZERO()
    {
        // "Não houve o que medir" vira "—" na tela; "0%" afirmaria que alguém tentou.
        Assert.Null(Percentual.De(0, 0));
        Assert.Null(Percentual.De(5, 0));
        Assert.Equal(0m, Percentual.De(0, 10));
    }

    [Fact]
    public void DE_0_A_100_COM_DUAS_CASAS()
    {
        Assert.Equal(33.33m, Percentual.De(1, 3));
        Assert.Equal(66.67m, Percentual.De(2, 3));
        Assert.Equal(100m, Percentual.De(7, 7));
    }

    /// <summary>⚠️ O PADRÃO DO .NET É ARREDONDAR PARA O PAR: 1/800 = 0,125 sairia 0,12. A regra
    /// é o meio para cima, como numa planilha.</summary>
    [Fact]
    public void O_MEIO_ARREDONDA_PARA_CIMA()
    {
        Assert.Equal(0.13m, Percentual.De(1, 800));
        Assert.Equal(0.38m, Percentual.De(3, 800));
    }

    [Fact]
    public void AS_FATIAS_SOMAM_EXATAMENTE_100()
    {
        var fatias = Percentual.Fatias([1, 1, 1]);

        Assert.Equal([33.34m, 33.33m, 33.33m], fatias);
        Assert.Equal(100m, fatias.Sum());
    }

    [Fact]
    public void O_CENTESIMO_QUE_SOBRA_VAI_PARA_O_MAIOR_RESTO()
    {
        // 2/7 = 28,571…, 2/7 = 28,571…, 3/7 = 42,857…: truncados somam 99,99, e o centésimo vai
        // para a fatia de maior resto (42,857 → resto 0,7 em centésimos).
        var fatias = Percentual.Fatias([2, 2, 3]);

        Assert.Equal(100m, fatias.Sum());
        Assert.Equal(42.86m, fatias[2]);
    }

    [Fact]
    public void SEM_TOTAL_TODAS_AS_FATIAS_SAO_ZERO()
    {
        Assert.Equal([0m, 0m], Percentual.Fatias([0, 0]));
        Assert.Empty(Percentual.Fatias([]));
    }
}
