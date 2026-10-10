using Nexora.Infra.Whatsapp;

namespace Nexora.Tests.Unidade;

/// <summary>O PEDIDO DE ENVIO PODE TER CHEGADO? (BUG-XX). So o que e CERTO que nao saiu volta para
/// a fila; na duvida, a mensagem nao e reenviada.</summary>
public class FalhaDeRedeTests
{
    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public void NAO_CONECTOU_E_CERTO_QUE_NAO_SAIU(HttpRequestError erro) =>
        Assert.False(FalhaDeRede.PodeTerChegado(new HttpRequestException(erro, "sem conexao")));

    [Fact]
    public void A_CONEXAO_QUE_CAIU_NO_MEIO_PODE_TER_ENVIADO() =>
        Assert.True(FalhaDeRede.PodeTerChegado(
            new HttpRequestException(HttpRequestError.ResponseEnded, "caiu no meio")));

    [Fact]
    public void O_TEMPO_ESGOTADO_PODE_TER_ENVIADO() =>
        Assert.True(FalhaDeRede.PodeTerChegado(new TaskCanceledException("timeout")));
}
