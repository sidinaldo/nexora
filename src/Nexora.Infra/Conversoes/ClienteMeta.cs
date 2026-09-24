using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Nexora.Core.Conversoes;

namespace Nexora.Infra.Conversoes;

/// <summary>O resultado de UMA chamada à Graph API.
///
/// `Codigo` é o HTTP; `CodigoMeta` é o `error.code` do corpo — e é ele que decide se vale tentar de
/// novo. `FbtraceId` é a primeira coisa que o suporte da Meta pede quando o cliente abre um
/// chamado.</summary>
public record ResultadoEnvioMeta(
    bool Aceitou, int? Codigo, int? CodigoMeta, string? FbtraceId, string? Erro);

public interface IClienteMeta
{
    /// <summary>Manda UM evento. NUNCA lança: falha vira resultado com erro, e quem decide o que
    /// fazer é a `PoliticaConversao`.
    ///
    /// `codigoTeste` não nulo faz o evento aparecer em "Eventos de teste" no Gerenciador e **não
    /// entrar** na otimização das campanhas do cliente.</summary>
    Task<ResultadoEnvioMeta> EnviarAsync(
        string pixelId, string token, string corpo, string? codigoTeste, CancellationToken ct);
}

/// <summary>Fala com a API de Conversões da Meta (INT-4).
///
/// ===================== O TOKEN VAI NO CORPO, NUNCA NA QUERY STRING =====================
/// A documentação mostra o exemplo com `?access_token=`. Query string vaza: aparece em log de
/// proxy, em APM, em `Referer` e em qualquer captura de tráfego intermediária — e o que estaria
/// vazando é a credencial com que se escreve na conta de anúncio do cliente.
///
/// O corpo é aceito, e é o que usamos.
/// =======================================================================================
///
/// ===================== E AQUI O CORPO DA RESPOSTA É LIDO =====================
/// Ao contrário do `ClienteWebhook`, que só olha o status. Aqui o corpo **é** a informação: a Meta
/// responde `200` com `error` dentro em vários casos, e é o `error.code` que separa "token morto,
/// para de tentar" de "estou limitando taxa, tenta de novo".
///
/// Com teto de 8 KB: é resposta de terceiro, e uma página de erro inteira não pode encher a memória
/// nem a coluna `erro` da tabela.
/// =============================================================================</summary>
public class ClienteMeta(
    HttpClient http,
    ILogger<ClienteMeta> log) : IClienteMeta
{
    /// <summary>A versão da Graph API, fixada.
    ///
    /// ⚠️ FIXAR É A DECISÃO CERTA. Sem versão na URL a Meta usa a mais antiga ainda suportada, e o
    /// comportamento muda sozinho no dia em que ela a aposenta — sem deploy nosso, sem aviso, e o
    /// sintoma seria "as conversões pararam".</summary>
    public const string Versao = "v21.0";

    /// <summary>Teto do corpo da resposta que a gente lê.</summary>
    private const int TetoDaResposta = 8 * 1024;

    public async Task<ResultadoEnvioMeta> EnviarAsync(
        string pixelId, string token, string corpo, string? codigoTeste, CancellationToken ct)
    {
        // O corpo guardado NÃO tem o token nem o código de teste (ver `MontadorEventoMeta`). Eles
        // entram agora: o payload que fica na tabela e aparece na tela do cliente não pode ter
        // credencial dentro, e trocar o token não pode invalidar o que já está na fila.
        var envelope = JsonNode.Parse(corpo)!.AsObject();
        envelope["access_token"] = token;
        if (!string.IsNullOrWhiteSpace(codigoTeste)) envelope["test_event_code"] = codigoTeste;

        var url = $"{Versao}/{pixelId}/events";

        using var pedido = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json")
        };
        pedido.Headers.UserAgent.Add(new ProductInfoHeaderValue("Nexora", "1.0"));

        // Timeout POR TENTATIVA, não o do HttpClient: ele é compartilhado, e mexer no `Timeout`
        // daqui afetaria todos os envios em voo.
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(PoliticaConversao.Timeout);

        try
        {
            using var resposta = await http.SendAsync(pedido, limite.Token);
            var codigo = (int)resposta.StatusCode;
            var texto = await LerAsync(resposta, limite.Token);

            return Interpretar(codigo, texto);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelamento NOSSO (o timeout), não desligamento da aplicação. No segundo caso o
            // evento tem de ficar pendente para a próxima rodada, e não contar como tentativa.
            return new ResultadoEnvioMeta(false, null, null, null,
                $"A Meta não respondeu em {PoliticaConversao.Timeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Envio de conversão falhou na rede.");
            return new ResultadoEnvioMeta(false, null, null, null, MensagemDeRede(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Envio de conversão falhou.");
            return new ResultadoEnvioMeta(false, null, null, null, ex.Message);
        }
    }

    /// <summary>===================== 200 COM `error` DENTRO É FALHA =====================
    /// É o caso que um cliente HTTP comum trata como sucesso, e o resultado seria a tela dizendo
    /// "entregue" para um evento que a Meta recusou — o pior tipo de mentira que este bloco pode
    /// contar, porque o cliente pararia de investigar.
    /// ==========================================================================</summary>
    private ResultadoEnvioMeta Interpretar(int codigo, string? texto)
    {
        var (codigoMeta, mensagem, fbtrace) = LerErro(texto);

        if (codigo is >= 200 and < 300 && codigoMeta is null)
            return new ResultadoEnvioMeta(true, codigo, null, fbtrace, null);

        var erro = mensagem is null
            ? $"A Meta respondeu {codigo}."
            : $"A Meta recusou: {mensagem}";

        return new ResultadoEnvioMeta(false, codigo, codigoMeta, fbtrace, Cortar(erro));
    }

    /// <summary>Lê `error.code`, `error.message` e o `fbtrace_id`. Nunca lança: corpo que não é
    /// JSON, ou que veio num formato novo, não pode derrubar a rodada.</summary>
    private (int? Codigo, string? Mensagem, string? Fbtrace) LerErro(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return (null, null, null);

        try
        {
            var raiz = JsonNode.Parse(texto)?.AsObject();
            if (raiz is null) return (null, null, null);

            // O `fbtrace_id` vem no erro OU na raiz, conforme o caso. Os dois lugares valem.
            var erro = raiz["error"]?.AsObject();
            var fbtrace = (string?)(erro?["fbtrace_id"] ?? raiz["fbtrace_id"]);

            if (erro is null) return (null, null, fbtrace);

            var codigo = erro["code"] is { } c && c.GetValueKind() == JsonValueKind.Number
                ? (int?)c.GetValue<int>()
                : null;

            // `error_user_msg` é a frase que a Meta escreve para pessoa ler; ela existe só em parte
            // dos erros, e quando existe é melhor que a `message` técnica.
            var mensagem = (string?)(erro["error_user_msg"] ?? erro["message"]);

            return (codigo, mensagem, fbtrace);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            log.LogInformation(ex, "Resposta da Meta não era JSON reconhecível.");
            return (null, null, null);
        }
    }

    /// <summary>Lê no máximo 8 KB do corpo. Resposta de terceiro não tem tamanho garantido.</summary>
    private static async Task<string?> LerAsync(HttpResponseMessage resposta, CancellationToken ct)
    {
        try
        {
            await using var fluxo = await resposta.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[TetoDaResposta];
            var lidos = 0;

            while (lidos < buffer.Length)
            {
                var n = await fluxo.ReadAsync(buffer.AsMemory(lidos), ct);
                if (n == 0) break;
                lidos += n;
            }

            return lidos == 0 ? null : Encoding.UTF8.GetString(buffer, 0, lidos);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Erro de rede em português: este texto vai para a TELA do dono, e "No such host is
    /// known" não diz a ele o que fazer.</summary>
    private static string MensagemDeRede(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "Não foi possível encontrar o servidor da Meta (DNS).",
        HttpRequestError.ConnectionError => "Não foi possível conectar à Meta.",
        HttpRequestError.SecureConnectionError => "Falha no certificado HTTPS da Meta.",
        _ => $"Falha de rede ao falar com a Meta: {ex.Message}"
    };

    private static string Cortar(string erro) => erro.Length <= 500 ? erro : erro[..500];
}
