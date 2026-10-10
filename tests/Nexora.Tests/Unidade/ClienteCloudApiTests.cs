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
    // Código desconhecido sai com o NÚMERO, e não com o `message` em inglês (BUG-XX).
    [InlineData("""{"error":{"message":"Algo novo","code":999}}""", "A Meta recusou o envio (código 999)")]
    [InlineData("""{"error":{"message":"Healthy ecosystem","code":131049}}""", "não cansar o cliente")]
    [InlineData("""{"error":{"message":"Recipient phone number not in allowed list","code":131030}}""", "lista de destinatários")]
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

    // ==================================================================== envio (etapa 5)
    /// <summary>Guarda cada pedido com o corpo LIDO na hora — o conteudo e descartado depois do
    /// envio — e responde conforme o recurso: o upload devolve o id do arquivo, a mensagem devolve
    /// o wamid.</summary>
    private sealed class HandlerDeEnvio : HttpMessageHandler
    {
        public List<(string Caminho, string? Tipo, string Corpo)> Pedidos { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage pedido, CancellationToken ct)
        {
            var corpo = pedido.Content == null ? "" : await pedido.Content.ReadAsStringAsync(ct);
            Pedidos.Add((pedido.RequestUri!.AbsolutePath, pedido.Content?.Headers.ContentType?.MediaType, corpo));

            var caminho = pedido.RequestUri.AbsolutePath;
            string resposta;
            if (caminho.EndsWith("/media")) resposta = """{"id":"MIDIA-1"}""";
            else if (caminho.EndsWith("/messages"))
                resposta = """{"messaging_product":"whatsapp","messages":[{"id":"wamid.ABC"}]}""";
            else if (caminho.EndsWith("/message_templates"))
                resposta = """{"id":"594425479261596","status":"PENDING","category":"UTILITY"}""";
            else
                resposta = """{"id":"594425479261596","status":"REJECTED","rejected_reason":"INVALID_FORMAT"}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resposta) };
        }
    }

    private static (ClienteCloudApi Cliente, HandlerDeEnvio Handler) ParaEnvio()
    {
        var handler = new HandlerDeEnvio();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.facebook.com/") };
        return (new ClienteCloudApi(http, NullLogger<ClienteCloudApi>.Instance), handler);
    }

    [Fact]
    public async Task O_TEXTO_SAI_COM_O_DESTINO_E_DEVOLVE_O_WAMID()
    {
        var (cliente, handler) = ParaEnvio();

        var id = await cliente.EnviarTextoAsync("1090000000001", "tok", "558488887777", "Oi, tudo bem?", default);

        Assert.Equal("wamid.ABC", id);
        var (caminho, _, corpo) = Assert.Single(handler.Pedidos);
        Assert.Equal($"/{ClienteCloudApi.Versao}/1090000000001/messages", caminho);
        Assert.Contains("\"to\":\"558488887777\"", corpo);
        Assert.Contains("\"type\":\"text\"", corpo);
        Assert.Contains("\"body\":\"Oi, tudo bem?\"", corpo);
        Assert.Contains("\"preview_url\":false", corpo);
    }

    /// <summary>O anexo vai PRIMEIRO para a Meta (multipart), e a mensagem cita o id — o arquivo do
    /// cliente nao precisa ficar publico na internet.</summary>
    [Fact]
    public async Task O_ANEXO_SOBE_PRIMEIRO_E_A_MENSAGEM_CITA_O_ID()
    {
        var (cliente, handler) = ParaEnvio();

        var id = await cliente.EnviarMidiaAsync(
            "1090000000001", "tok", "558488887777", [1, 2, 3], "application/pdf", "document",
            "proposta.pdf", "Segue a proposta", default);

        Assert.Equal("wamid.ABC", id);
        Assert.Equal(2, handler.Pedidos.Count);
        Assert.EndsWith("/1090000000001/media", handler.Pedidos[0].Caminho);
        Assert.Equal("multipart/form-data", handler.Pedidos[0].Tipo);

        var mensagem = handler.Pedidos[1].Corpo;
        Assert.Contains("\"type\":\"document\"", mensagem);
        Assert.Contains("\"id\":\"MIDIA-1\"", mensagem);
        Assert.Contains("\"caption\":\"Segue a proposta\"", mensagem);
        Assert.Contains("\"filename\":\"proposta.pdf\"", mensagem);
    }

    /// <summary>A Meta nao aceita legenda em audio: mandada, a nota de voz e recusada.</summary>
    [Fact]
    public async Task O_AUDIO_SAI_SEM_LEGENDA()
    {
        var (cliente, handler) = ParaEnvio();

        await cliente.EnviarMidiaAsync(
            "1090000000001", "tok", "558488887777", [1, 2, 3], "audio/ogg", "audio", null, "legenda", default);

        Assert.DoesNotContain("caption", handler.Pedidos[1].Corpo);
        Assert.Contains("\"type\":\"audio\"", handler.Pedidos[1].Corpo);
    }

    /// <summary>Se mesmo assim a Meta recusar pela janela (131047), a falha gravada na linha diz o
    /// motivo em portugues.</summary>
    [Fact]
    public async Task A_JANELA_FECHADA_DA_META_VOLTA_EM_PORTUGUES()
    {
        var (cliente, _) = Novo(HttpStatusCode.BadRequest,
            """{"error":{"message":"Re-engagement message","code":131047}}""");

        var erro = await Assert.ThrowsAsync<IntegracaoWhatsAppException>(
            () => cliente.EnviarTextoAsync("1090000000001", "tok", "5584988887777", "oi", default));

        Assert.Contains("janela de 24h", erro.Message);
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

    // ==================================================================== templates (etapa 7)
    /// <summary>O template vai para a revisao com a categoria em maiusculas, o corpo NUMERADO e um
    /// exemplo por variavel — sem o exemplo, a Meta recusa o pedido.</summary>
    [Fact]
    public async Task O_TEMPLATE_VAI_PARA_A_REVISAO_COM_A_CATEGORIA_E_OS_EXEMPLOS()
    {
        var (cliente, handler) = ParaEnvio();

        var criado = await cliente.CriarModeloAsync(
            "2090000000001", "tok", "boas_vindas", "utility", "pt_BR",
            "Olá {{1}}, aqui é da {{2}}. Podemos ajudar?", ["Maria", "Loja Exemplo"], default);

        Assert.Equal(new ModeloNaMeta("594425479261596", "PENDING", null), criado);
        var (caminho, _, corpo) = Assert.Single(handler.Pedidos);
        Assert.Equal($"/{ClienteCloudApi.Versao}/2090000000001/message_templates", caminho);
        Assert.Contains("\"name\":\"boas_vindas\"", corpo);
        Assert.Contains("\"language\":\"pt_BR\"", corpo);
        Assert.Contains("\"category\":\"UTILITY\"", corpo);
        Assert.Contains("\"type\":\"BODY\"", corpo);
        Assert.Contains("\"body_text\":[[\"Maria\",\"Loja Exemplo\"]]", corpo);
    }

    /// <summary>Sem variavel, o `example` nao vai: a Meta recusa exemplo que nao tem onde entrar.</summary>
    [Fact]
    public async Task TEMPLATE_SEM_VARIAVEL_VAI_SEM_EXEMPLO()
    {
        var (cliente, handler) = ParaEnvio();

        await cliente.CriarModeloAsync("2090000000001", "tok", "aviso", "utility", "pt_BR",
            "Seu pedido saiu para entrega.", [], default);

        Assert.DoesNotContain("example", Assert.Single(handler.Pedidos).Corpo);
    }

    [Fact]
    public async Task A_REVISAO_E_LIDA_PELO_ID_DA_META()
    {
        var (cliente, handler) = ParaEnvio();

        var lido = await cliente.LerModeloAsync("594425479261596", "tok", default);

        Assert.Equal(new ModeloNaMeta("594425479261596", "REJECTED", "INVALID_FORMAT"), lido);
        Assert.Equal($"/{ClienteCloudApi.Versao}/594425479261596", Assert.Single(handler.Pedidos).Caminho);
    }

    [Fact]
    public async Task O_TEMPLATE_SAI_COM_O_NOME_O_IDIOMA_E_OS_VALORES_NA_ORDEM()
    {
        var (cliente, handler) = ParaEnvio();

        var id = await cliente.EnviarModeloAsync(
            "1090000000001", "tok", "558488887777", "boas_vindas", "pt_BR", ["Maria", "Loja Exemplo"], default);

        Assert.Equal("wamid.ABC", id);
        var corpo = Assert.Single(handler.Pedidos).Corpo;
        Assert.Contains("\"type\":\"template\"", corpo);
        Assert.Contains("\"name\":\"boas_vindas\"", corpo);
        Assert.Contains("\"language\":{\"code\":\"pt_BR\"}", corpo);
        Assert.Contains(
            "\"parameters\":[{\"type\":\"text\",\"text\":\"Maria\"},{\"type\":\"text\",\"text\":\"Loja Exemplo\"}]",
            corpo);
    }

    /// <summary>O 100 na revisao de template e a Meta recusando o PEDIDO — e nao "numero nao
    /// encontrado", que e o que ele quer dizer no resto da API.</summary>
    [Fact]
    public async Task A_RECUSA_DO_TEMPLATE_DIZ_O_MOTIVO_DA_META()
    {
        var (cliente, _) = Novo(HttpStatusCode.BadRequest, """
            {"error":{"message":"Invalid parameter","code":100,
              "error_user_msg":"Já existe conteúdo neste idioma."}}
            """);

        var erro = await Assert.ThrowsAsync<IntegracaoWhatsAppException>(() => cliente.CriarModeloAsync(
            "2090000000001", "tok", "boas_vindas", "utility", "pt_BR", "Olá {{1}}!", ["Maria"], default));

        Assert.Equal("A Meta recusou o template: Já existe conteúdo neste idioma.", erro.Message);
    }

    [Theory]
    [InlineData(132001, "não achou este template")]
    [InlineData(132015, "pausou")]
    [InlineData(132000, "variáveis")]
    public async Task O_ERRO_DO_ENVIO_DE_TEMPLATE_VOLTA_EM_PORTUGUES(int codigo, string trecho)
    {
        var (cliente, _) = Novo(HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"x\",\"code\":" + codigo + "}}");

        var erro = await Assert.ThrowsAsync<IntegracaoWhatsAppException>(() => cliente.EnviarModeloAsync(
            "1090000000001", "tok", "558488887777", "boas_vindas", "pt_BR", ["Maria"], default));

        Assert.Contains(trecho, erro.Message);
    }
}
