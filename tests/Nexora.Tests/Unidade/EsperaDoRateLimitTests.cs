using Nexora.Api.Seguranca;

namespace Nexora.Tests.Unidade;

/// <summary>BUG-XX: o limite de tentativas dizia "Aguarde 900 segundos" e "Aguarde 1 segundos".</summary>
public class EsperaDoRateLimitTests
{
    [Theory]
    [InlineData(1, "1 segundo")]
    [InlineData(40, "40 segundos")]
    [InlineData(900, "15 minutos")]
    public void A_ESPERA_SAI_EM_PALAVRAS(int segundos, string esperado) =>
        Assert.Equal(esperado, RateLimitingConfig.Espera(segundos));
}
