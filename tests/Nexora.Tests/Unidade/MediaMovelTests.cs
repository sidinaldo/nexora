using Nexora.Core.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>A média móvel dos gráficos (AUD-XX, #23): janela de 7, parcial no começo, nada com
/// menos de 7 pontos. Era a regra do componente do gráfico, e agora é do servidor.</summary>
public class MediaMovelTests
{
    [Fact]
    public void A_JANELA_E_PARCIAL_NO_COMECO_E_DE_SETE_DEPOIS()
    {
        var m = MediaMovel.De([10m, 20m, 30m, 40m, 50m, 60m, 70m, 80m]);

        Assert.Equal(10m, m[0]);
        Assert.Equal(15m, m[1]);          // (10 + 20) / 2
        Assert.Equal(40m, m[6]);          // os sete primeiros
        Assert.Equal(50m, m[7]);          // de 20 a 80
    }

    [Fact]
    public void COM_MENOS_DE_SETE_PONTOS_NAO_HA_MEDIA()
    {
        Assert.All(MediaMovel.De([1m, 2m, 3m, 4m, 5m, 6m]), v => Assert.Null(v));
    }

    [Fact]
    public void ARREDONDA_EM_DUAS_CASAS_COM_O_MEIO_PARA_CIMA()
    {
        // 2/3 = 0,6666… e 2/7 = 0,2857…
        var m = MediaMovel.De([0m, 1m, 1m, 0m, 0m, 0m, 0m]);
        Assert.Equal(0.67m, m[2]);
        Assert.Equal(0.29m, m[6]);

        // O MEIO EXATO: 0,125 vai para 0,13. O arredondamento padrão do .NET (para o par) daria 0,12.
        var meio = MediaMovel.De([0.125m, 0m, 0m, 0m, 0m, 0m, 0m]);
        Assert.Equal(0.13m, meio[0]);
    }
}
