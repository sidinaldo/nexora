using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Conversoes;
using Nexora.Infra.Conversoes;

namespace Nexora.Tests.Unidade;

/// <summary>O CLIENTE DA META: como a resposta dela é lida (INT-4).
///
/// ===================== O QUE DECIDE TUDO É O CORPO, NÃO O STATUS =====================
/// Ela responde **200 com `error` dentro** em vários casos. Um cliente HTTP comum trataria isso como
/// sucesso, e a tela diria "entregue" para um evento recusado — a pior mentira que este bloco pode
/// contar, porque o cliente pararia de investigar.
///
/// Sem rede: um `HttpMessageHandler` falso devolve corpos reais da Graph API.
/// ==================================================================================</summary>
public class ClienteMetaTests
{
    private const string Corpo = """{"data":[{"event_name":"Lead"}]}""";

    // ==================================================================== o token
    [Fact]
    public async Task O_TOKEN_VAI_NO_CORPO__NUNCA_NA_QUERY_STRING()
    {
        // ⚠️ Query string vaza: aparece em log de proxy, em APM, em `Referer` e em qualquer captura
        // de tráfego. E o que estaria vazando é a credencial com que se escreve na conta de anúncio
        // do cliente.
        var (cliente, espiao) = Montar(HttpStatusCode.OK, """{"events_received":1}""");

        await cliente.EnviarAsync("123456", "EAAGsegredo", Corpo, null, default);

        Assert.DoesNotContain("access_token", espiao.Url!.Query);
        Assert.DoesNotContain("EAAGsegredo", espiao.Url.ToString());

        var enviado = JsonNode.Parse(espiao.Corpo!)!.AsObject();
        Assert.Equal("EAAGsegredo", (string)enviado["access_token"]!);
    }

    [Fact]
    public async Task A_URL_LEVA_A_VERSAO_FIXADA_E_O_PIXEL()
    {
        // ⚠️ Fixar a versão é a decisão certa: sem ela a Meta usa a mais antiga ainda suportada, e o
        // comportamento muda sozinho no dia em que ela a aposenta — sem deploy nosso e sem aviso.
        var (cliente, espiao) = Montar(HttpStatusCode.OK, """{"events_received":1}""");

        await cliente.EnviarAsync("987654321", "tok", Corpo, null, default);

        Assert.Equal($"/{ClienteMeta.Versao}/987654321/events", espiao.Url!.AbsolutePath);
    }

    [Fact]
    public async Task O_CODIGO_DE_TESTE_VAI_QUANDO_EXISTE__E_NAO_VAI_QUANDO_NAO()
    {
        var (comCodigo, espiaoCom) = Montar(HttpStatusCode.OK, """{"events_received":1}""");
        await comCodigo.EnviarAsync("1", "tok", Corpo, "TEST123", default);
        Assert.Equal("TEST123",
            (string)JsonNode.Parse(espiaoCom.Corpo!)!["test_event_code"]!);

        var (semCodigo, espiaoSem) = Montar(HttpStatusCode.OK, """{"events_received":1}""");
        await semCodigo.EnviarAsync("1", "tok", Corpo, null, default);

        // ⚠️ A CHAVE NÃO EXISTE NO TEXTO, e a afirmação é sobre o TEXTO de propósito. `Assert.Null`
        // no indexador passava com a chave presente valendo `null` — `JsonObject["x"] = null` grava
        // um nó nulo, e ler de volta dá null nos dois casos. Uma sabotagem provou isso: mandar o
        // código sempre, mesmo vazio, não derrubava teste nenhum.
        //
        // E a diferença importa: `test_event_code` presente e nulo faz a Meta tratar o evento como
        // de teste com um código que não existe — ele não apareceria em lugar nenhum.
        Assert.DoesNotContain("test_event_code", espiaoSem.Corpo!);
    }

    // ==================================================================== a resposta
    [Fact]
    public async Task DOIS_ZERO_ZERO_LIMPO_E_SUCESSO()
    {
        var (cliente, _) = Montar(HttpStatusCode.OK,
            """{"events_received":1,"messages":[],"fbtrace_id":"AbCdEf123"}""");

        var r = await cliente.EnviarAsync("1", "tok", Corpo, null, default);

        Assert.True(r.Aceitou);
        Assert.Equal(200, r.Codigo);
        Assert.Null(r.CodigoMeta);
        Assert.Equal("AbCdEf123", r.FbtraceId);
        Assert.Null(r.Erro);
    }

    [Fact]
    public async Task DOIS_ZERO_ZERO_COM_ERROR_DENTRO_E_FALHA()
    {
        // ⚠️ O CASO QUE UM CLIENTE HTTP COMUM CHAMA DE SUCESSO. Se passasse, a tela diria "entregue"
        // para um evento que a Meta recusou — e o dono pararia de procurar o problema.
        var (cliente, _) = Montar(HttpStatusCode.OK, """
            {"error":{"message":"Invalid OAuth access token.","type":"OAuthException",
              "code":190,"fbtrace_id":"XyZ789"}}
            """);

        var r = await cliente.EnviarAsync("1", "tok", Corpo, null, default);

        Assert.False(r.Aceitou);
        Assert.Equal(200, r.Codigo);
        Assert.Equal(190, r.CodigoMeta);
        Assert.Equal("XyZ789", r.FbtraceId);
        Assert.Contains("Invalid OAuth", r.Erro);
    }

    [Fact]
    public async Task O_CODIGO_DA_META_E_LIDO_DO_ERRO_DE_400()
    {
        var (cliente, _) = Montar(HttpStatusCode.BadRequest, """
            {"error":{"message":"Param event_time must be within the last 7 days",
              "code":100,"error_subcode":2804003,"fbtrace_id":"Q1"}}
            """);

        var r = await cliente.EnviarAsync("1", "tok", Corpo, null, default);

        Assert.False(r.Aceitou);
        Assert.Equal(400, r.Codigo);
        Assert.Equal(100, r.CodigoMeta);
        Assert.Contains("7 days", r.Erro);
    }

    [Fact]
    public async Task A_FRASE_PARA_PESSOA_GANHA_DA_TECNICA_QUANDO_EXISTE()
    {
        // `error_user_msg` é o que a Meta escreve para alguém ler; quando existe, é melhor que a
        // `message` técnica — e é esta string que vai para a tela do dono.
        var (cliente, _) = Montar(HttpStatusCode.BadRequest, """
            {"error":{"message":"(#100) Invalid parameter",
              "error_user_msg":"O pixel informado não pertence a esta conta.","code":100}}
            """);

        var r = await cliente.EnviarAsync("1", "tok", Corpo, null, default);

        Assert.Contains("não pertence a esta conta", r.Erro);
        Assert.DoesNotContain("Invalid parameter", r.Erro);
    }

    [Fact]
    public async Task RESPOSTA_QUE_NAO_E_JSON_NAO_DERRUBA_A_RODADA()
    {
        // Um balanceador no meio do caminho devolve HTML. Lançar aqui pararia a drenagem inteira por
        // causa de um evento.
        var (cliente, _) = Montar(HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>");

        var r = await cliente.EnviarAsync("1", "tok", Corpo, null, default);

        Assert.False(r.Aceitou);
        Assert.Equal(502, r.Codigo);
        Assert.Null(r.CodigoMeta);
        Assert.Contains("502", r.Erro);
    }

    [Fact]
    public async Task FALHA_DE_REDE_VIRA_FRASE_EM_PORTUGUES_E_NAO_LANCA()
    {
        // Este texto vai para a TELA do dono, e "No such host is known" não diz a ele o que fazer.
        var cliente = new ClienteMeta(
            new HttpClient(new HandlerQueQuebra()) { BaseAddress = new Uri("https://graph.facebook.com/") },
            NullLogger<ClienteMeta>.Instance);

        var r = await cliente.EnviarAsync("1", "tok", Corpo, null, default);

        Assert.False(r.Aceitou);
        Assert.Null(r.Codigo);
        Assert.Contains("Meta", r.Erro);
    }

    [Fact]
    public async Task O_CORPO_DA_RESPOSTA_TEM_TETO()
    {
        // Resposta de terceiro não tem tamanho garantido. Sem teto, um corpo de 50 MB viraria uma
        // string de 50 MB na memória e um `erro` gigante na tabela.
        var gigante = "{\"error\":{\"message\":\"" + new string('x', 200_000) + "\",\"code\":1}}";
        var (cliente, _) = Montar(HttpStatusCode.BadRequest, gigante);

        var r = await cliente.EnviarAsync("1", "tok", Corpo, null, default);

        Assert.False(r.Aceitou);
        // Cortado em 8 KB, o JSON fica truncado e ilegível — então não há `code`, e o erro cai na
        // frase genérica. O que importa é que não estourou e o texto é curto.
        Assert.True(r.Erro!.Length <= 500);
    }

    // ==================================================================== apoio
    private sealed class Espiao
    {
        public Uri? Url { get; set; }
        public string? Corpo { get; set; }
    }

    private static (ClienteMeta, Espiao) Montar(HttpStatusCode codigo, string corpo)
    {
        var espiao = new Espiao();
        var http = new HttpClient(new HandlerFalso(espiao, codigo, corpo))
        {
            BaseAddress = new Uri("https://graph.facebook.com/")
        };

        return (new ClienteMeta(http, NullLogger<ClienteMeta>.Instance), espiao);
    }

    private sealed class HandlerFalso(Espiao espiao, HttpStatusCode codigo, string corpo)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage pedido, CancellationToken ct)
        {
            espiao.Url = pedido.RequestUri;
            espiao.Corpo = pedido.Content is null ? null : await pedido.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(codigo)
            {
                Content = new StringContent(corpo, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class HandlerQueQuebra : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage pedido, CancellationToken ct) =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host");
    }
}

/// <summary>A CLASSIFICAÇÃO DO ERRO DA META (INT-4).
///
/// Ela decide três coisas diferentes: tentar de novo, desistir, e **desligar a credencial da empresa
/// inteira**. A terceira é a que precisa estar certa: desligar por um erro transitório pararia o
/// envio de um cliente por nada, e não desligar com token morto deixaria a tela dizendo "enviando"
/// enquanto nada sai.</summary>
public class PoliticaConversaoTests
{
    [Theory]
    [InlineData(190)]   // token inválido ou expirado
    [InlineData(102)]   // sessão inválida
    public void TOKEN_MORTO_DESISTE_NA_PRIMEIRA_E_DESLIGA_A_CREDENCIAL(int codigo)
    {
        // Insistir 3× com token morto são três linhas idênticas e zero informação. E sem a
        // desativação, a credencial ficaria "ativa" enquanto nada sai — o pior estado possível para
        // quem está olhando a tela.
        var d = PoliticaConversao.Classificar(codigo);

        Assert.False(d.TentarDeNovo);
        Assert.True(d.DesativarCredencial);
        Assert.Contains("token", d.Motivo!);
    }

    [Theory]
    [InlineData(200)]   // sem permissão
    [InlineData(10)]
    [InlineData(272)]
    public void SEM_PERMISSAO_TAMBEM_DESLIGA(int codigo)
    {
        var d = PoliticaConversao.Classificar(codigo);

        Assert.False(d.TentarDeNovo);
        Assert.True(d.DesativarCredencial);
        Assert.NotNull(d.Motivo);
    }

    [Fact]
    public void PARAMETRO_INVALIDO_DESISTE_MAS_NAO_DESLIGA()
    {
        // ⚠️ A DISTINÇÃO IMPORTA. `100` é problema DESTE evento (payload, ou `event_time` fora da
        // janela), não do token. Desligar a credencial por causa dele pararia todas as conversões da
        // empresa por causa de um evento ruim.
        var d = PoliticaConversao.Classificar(100);

        Assert.False(d.TentarDeNovo);
        Assert.False(d.DesativarCredencial);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]     // limite de taxa
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(613)]
    [InlineData(null)]  // rede, DNS, timeout
    [InlineData(99999)] // um código que ainda não existia quando isto foi escrito
    public void O_TRANSITORIO_E_O_DESCONHECIDO_TENTAM_DE_NOVO(int? codigo)
    {
        // O desconhecido tenta: três tentativas custam pouco, e um código novo é mais provavelmente
        // transitório do que permanente.
        var d = PoliticaConversao.Classificar(codigo);

        Assert.True(d.TentarDeNovo);
        Assert.False(d.DesativarCredencial);
    }

    [Fact]
    public void O_BACKOFF_COBRE_TRES_FALHAS_E_PARA()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), PoliticaConversao.EsperaApos(1));
        Assert.Equal(TimeSpan.FromMinutes(5), PoliticaConversao.EsperaApos(2));

        // Na terceira acabou: repetir para sempre transformaria a Meta fora do ar numa fila que só
        // cresce, e no dia em que ela voltasse receberia semanas de eventos velhos de uma vez —
        // metade deles já fora da janela de 7 dias.
        Assert.Null(PoliticaConversao.EsperaApos(3));
        Assert.Null(PoliticaConversao.EsperaApos(0));
    }

    [Fact]
    public void A_RODADA_E_MAIS_LENTA_E_MENOR_QUE_A_DE_WEBHOOK()
    {
        // Não é detalhe de tuning: a Meta atribui pelo `event_time` do fato, então atrasar não custa
        // atribuição — e custa metade da pressão numa API com limite de taxa. O webhook corre porque
        // do outro lado há um sistema esperando; aqui não há ninguém.
        Assert.Equal(50, PoliticaConversao.MaximoPorRodada);
        Assert.Equal(TimeSpan.FromSeconds(60), PoliticaConversao.Intervalo);
        Assert.Equal(7, PoliticaConversao.DiasDeValidade);
    }
}
