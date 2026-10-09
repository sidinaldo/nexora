using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Servicos;
using Nexora.Infra.CloudApi;

namespace Nexora.Tests.Unidade;

/// <summary>A GRAPH API PELA CONEXAO OFICIAL (INT-XX).
///
/// Sem rede: um `HttpMessageHandler` falso guarda o pedido e devolve o corpo que a Meta devolveria.
/// O que importa provar e o que sai daqui — versao fixa, token no cabecalho e nunca na URL — e
/// como a resposta volta em portugues.</summary>
public class ClienteCloudApiTests
{
    private sealed class Handler(HttpStatusCode codigo, string corpo) : HttpMessageHandler
    {
        public HttpRequestMessage? Pedido { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage pedido, CancellationToken ct)
        {
            Pedido = pedido;
            return Task.FromResult(new HttpResponseMessage(codigo) { Content = new StringContent(corpo) });
        }
    }

    private sealed class HandlerQueQuebra : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage pedido, CancellationToken ct) =>
            throw new HttpRequestException("sem rede");
    }

    private static (ClienteCloudApi Cliente, Handler Handler) Novo(HttpStatusCode codigo, string corpo)
    {
        var handler = new Handler(codigo, corpo);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.facebook.com/") };
        return (new ClienteCloudApi(http, NullLogger<ClienteCloudApi>.Instance), handler);
    }

    [Fact]
    public async Task LE_O_NUMERO_COM_O_TOKEN_NO_CABECALHO_E_NUNCA_NA_URL()
    {
        var (cliente, handler) = Novo(HttpStatusCode.OK, """
            { "display_phone_number": "+55 84 91234-5678", "verified_name": "Loja Teste",
              "quality_rating": "GREEN", "id": "1090000000001" }
            """);

        var numero = await cliente.LerNumeroAsync("1090000000001", "EAAG-segredo", default);

        Assert.Equal("5584912345678", numero.Numero);
        Assert.Equal("Loja Teste", numero.NomeVerificado);
        Assert.Equal("GREEN", numero.Qualidade);

        var url = handler.Pedido!.RequestUri!.ToString();
        Assert.StartsWith($"https://graph.facebook.com/{ClienteCloudApi.Versao}/1090000000001?", url);
        Assert.DoesNotContain("EAAG", url);
        Assert.Equal("Bearer", handler.Pedido.Headers.Authorization!.Scheme);
        Assert.Equal("EAAG-segredo", handler.Pedido.Headers.Authorization.Parameter);
    }

    /// <summary>O erro da Meta chega em portugues e com a causa — e o que o formulario mostra.</summary>
    [Theory]
    [InlineData("""{"error":{"message":"Invalid OAuth access token.","code":190}}""", "token")]
    [InlineData("""{"error":{"message":"Unsupported get request.","code":100}}""", "Phone Number ID")]
    [InlineData("""{"error":{"message":"Algo novo","code":999}}""", "A Meta recusou: Algo novo")]
    [InlineData("nao e json", "A Meta recusou o pedido.")]
    public async Task O_ERRO_DA_META_VOLTA_EM_PORTUGUES(string corpo, string trecho)
    {
        var (cliente, _) = Novo(HttpStatusCode.BadRequest, corpo);

        var erro = await Assert.ThrowsAsync<IntegracaoWhatsAppException>(
            () => cliente.LerNumeroAsync("1090000000001", "tok", default));

        Assert.Contains(trecho, erro.Message);
    }

    [Theory]
    [InlineData("""{"data":[{"id":"111"},{"id":"1090000000001"}]}""", true)]
    [InlineData("""{"data":[{"id":"111"}]}""", false)]
    [InlineData("""{"data":[]}""", false)]
    public async Task CONFERE_SE_O_NUMERO_E_DA_WABA(string corpo, bool esperado)
    {
        var (cliente, handler) = Novo(HttpStatusCode.OK, corpo);

        Assert.Equal(esperado, await cliente.NumeroEstaNaWabaAsync("2090000000001", "1090000000001", "tok", default));
        Assert.Contains("/2090000000001/phone_numbers", handler.Pedido!.RequestUri!.AbsolutePath);
    }

    /// <summary>O mesmo vocabulario da Evolution, para o verificador e o banner nao saberem o
    /// canal: token caido e o numero que caiu (`close`); a Meta fora do ar e problema nosso
    /// (`offline`).</summary>
    [Theory]
    [InlineData(HttpStatusCode.OK, "open")]
    [InlineData(HttpStatusCode.Unauthorized, "close")]
    [InlineData(HttpStatusCode.BadRequest, "close")]
    [InlineData(HttpStatusCode.TooManyRequests, "offline")]
    [InlineData(HttpStatusCode.InternalServerError, "offline")]
    public async Task O_ESTADO_FALA_A_LINGUA_DA_EVOLUTION(HttpStatusCode codigo, string esperado)
    {
        var (cliente, _) = Novo(codigo, "{}");
        Assert.Equal(esperado, await cliente.EstadoAsync("1090000000001", "tok", default));
    }

    [Fact]
    public async Task SEM_REDE_O_ESTADO_E_OFFLINE_E_A_LEITURA_EXPLICA()
    {
        var http = new HttpClient(new HandlerQueQuebra()) { BaseAddress = new Uri("https://graph.facebook.com/") };
        var cliente = new ClienteCloudApi(http, NullLogger<ClienteCloudApi>.Instance);

        Assert.Equal("offline", await cliente.EstadoAsync("1090000000001", "tok", default));

        var erro = await Assert.ThrowsAsync<IntegracaoWhatsAppException>(
            () => cliente.LerNumeroAsync("1090000000001", "tok", default));
        Assert.Contains("não respondeu", erro.Message);
    }
}
