using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Nexora.Core.Webhooks;

namespace Nexora.Infra.Webhooks;

/// <summary>O resultado de UMA tentativa. `Codigo` nulo = nem chegou a haver resposta (DNS,
/// timeout, conexão recusada) — e essa distinção é o que separa "meu servidor recusou" de "seu
/// servidor não me achou" numa conversa de suporte.</summary>
public record ResultadoEntrega(bool Aceitou, int? Codigo, string? Erro);

public interface IClienteWebhook
{
    /// <summary>POSTa o corpo assinado. NUNCA lança: falha vira `ResultadoEntrega` com o erro, e
    /// quem decide o que fazer com ela é a política de retry.</summary>
    Task<ResultadoEntrega> EntregarAsync(
        string url, string segredo, string corpo, string evento, Guid eventoId, CancellationToken ct);
}

/// <summary>Entrega o webhook. É o único ponto do sistema que faz requisição para um endereço
/// ESCOLHIDO PELO CLIENTE — e por isso é o que mais precisa de cuidado.</summary>
public class ClienteWebhook(
    HttpClient http,
    TimeProvider relogio,
    ILogger<ClienteWebhook> log) : IClienteWebhook
{
    public async Task<ResultadoEntrega> EntregarAsync(
        string url, string segredo, string corpo, string evento, Guid eventoId, CancellationToken ct)
    {
        var timestamp = relogio.GetUtcNow().ToUnixTimeSeconds();

        using var pedido = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // O corpo vai EXATAMENTE como foi guardado — a mesma string que foi assinada. Deixar o
            // `HttpClient` reserializar um objeto aqui mudaria bytes (ordem, escapes, indentação) e
            // a assinatura deixaria de conferir do lado do receptor, sem erro nenhum do nosso lado.
            Content = new StringContent(corpo, Encoding.UTF8, "application/json")
        };

        pedido.Headers.TryAddWithoutValidation(
            AssinaturaWebhook.HeaderAssinatura, AssinaturaWebhook.Calcular(segredo, timestamp, corpo));
        pedido.Headers.TryAddWithoutValidation(
            AssinaturaWebhook.HeaderTimestamp, timestamp.ToString());
        pedido.Headers.TryAddWithoutValidation(AssinaturaWebhook.HeaderEvento, evento);
        pedido.Headers.TryAddWithoutValidation(AssinaturaWebhook.HeaderEntrega, eventoId.ToString());
        pedido.Headers.UserAgent.Add(new ProductInfoHeaderValue("Nexora-Webhook", "1.0"));

        // Timeout POR TENTATIVA, não o do HttpClient: o cliente é compartilhado, e mexer no
        // `Timeout` dele daqui afetaria todas as entregas em voo.
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(PoliticaEntrega.Timeout);

        try
        {
            // `ResponseHeadersRead`: o que decide o resultado é o STATUS. Baixar o corpo da
            // resposta seria ler bytes de um servidor de terceiro sem nenhum uso — e sem limite.
            using var resposta = await http.SendAsync(
                pedido, HttpCompletionOption.ResponseHeadersRead, limite.Token);

            var codigo = (int)resposta.StatusCode;
            return PoliticaEntrega.Aceitou(codigo)
                ? new ResultadoEntrega(true, codigo, null)
                : new ResultadoEntrega(false, codigo, $"O receptor respondeu {codigo} {resposta.ReasonPhrase}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelamento NOSSO (o timeout), não desligamento da aplicação. Distinguir importa:
            // no segundo caso a entrega tem que ficar pendente para a próxima rodada, e não contar
            // como tentativa falha.
            return new ResultadoEntrega(false, null,
                $"O receptor não respondeu em {PoliticaEntrega.Timeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Entrega de webhook falhou na rede.");
            return new ResultadoEntrega(false, null, MensagemDeRede(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Entrega de webhook falhou.");
            return new ResultadoEntrega(false, null, ex.Message);
        }
    }

    /// <summary>Erro de rede em português, porque este texto vai para a TELA do dono — e
    /// "No such host is known" não diz a ele o que fazer.</summary>
    private static string MensagemDeRede(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "O endereço não foi encontrado (DNS).",
        HttpRequestError.ConnectionError => "Não foi possível conectar ao servidor.",
        HttpRequestError.SecureConnectionError => "Falha no certificado HTTPS do servidor.",
        _ => $"Falha de rede: {ex.Message}"
    };
}

/// <summary>DNS de verdade.
///
/// NUNCA lança: nome que não resolve devolve vazio, e vazio é recusa no validador. Lançar aqui
/// faria uma zona fora do ar virar exceção no meio da rodada de drenagem.</summary>
public class ResolvedorDns(ILogger<ResolvedorDns> log) : IResolvedorDns
{
    public async Task<IPAddress[]> ResolverAsync(string host, CancellationToken ct)
    {
        try { return await Dns.GetHostAddressesAsync(host, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogInformation(ex, "Não foi possível resolver {Host}.", host);
            return [];
        }
    }
}
