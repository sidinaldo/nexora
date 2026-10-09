using System.Security.Cryptography;
using System.Text;

namespace Nexora.Core.Whatsapp;

/// <summary>===================== QUEM MANDOU O WEBHOOK FOI A META? (INT-XX) =====================
///
/// A Meta assina cada entrega com HMAC-SHA256 do CORPO CRU, usando o app secret, no cabecalho
/// `X-Hub-Signature-256: sha256=<hex>`. A URL do webhook nao e segredo — aparece no painel da Meta
/// de quem a cadastrou —, entao sem esta conferencia qualquer um forjaria mensagem de cliente.
///
/// ⚠️ SOBRE OS BYTES QUE CHEGARAM, e nao sobre o JSON relido: reformatar mudaria a assinatura.
/// ⚠️ COMPARACAO EM TEMPO CONSTANTE (`FixedTimeEquals`): comparar byte a byte e parar no primeiro
/// diferente deixa medir quantos bytes acertou.
/// ================================================================================</summary>
public static class AssinaturaMeta
{
    private const string Prefixo = "sha256=";

    public static bool Valida(byte[] corpo, string? cabecalho, string appSecret)
    {
        if (string.IsNullOrWhiteSpace(cabecalho)) return false;
        if (!cabecalho.StartsWith(Prefixo, StringComparison.Ordinal)) return false;
        if (string.IsNullOrEmpty(appSecret)) return false;

        byte[] recebida;
        try
        {
            recebida = Convert.FromHexString(cabecalho.Substring(Prefixo.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var esperada = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), corpo);
        return CryptographicOperations.FixedTimeEquals(recebida, esperada);
    }

    /// <summary>O cabecalho que a Meta mandaria para este corpo. Para os testes assinarem como ela.</summary>
    public static string Assinar(byte[] corpo, string appSecret) =>
        Prefixo + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), corpo)).ToLowerInvariant();
}

/// <summary>O que aconteceu com uma entrega do webhook da Meta.</summary>
public enum ResultadoWebhookMeta
{
    /// <summary>Gravado na fila de entrada: responde 200.</summary>
    Aceito,

    /// <summary>A entrega cita um numero nosso, mas a assinatura nao bate com o app secret dele:
    /// responde 401 e nada e gravado.</summary>
    AssinaturaInvalida,

    /// <summary>Nenhum numero conhecido: responde 200 mesmo assim. Um numero removido do Nexora
    /// continuaria recebendo reenvios da Meta por dias se a resposta fosse erro.</summary>
    NadaReconhecido
}

/// <summary>A porta de entrada do webhook da Cloud API (INT-XX). A Api so le os bytes e o
/// cabecalho; quem confere, grava e decide e daqui para dentro.</summary>
public interface IRecepcaoWebhookMeta
{
    /// <summary>Confere a assinatura, grava cada mudanca aceita na fila e responde rapido — a Meta
    /// reenvia se demorar. O processamento e em segundo plano.</summary>
    Task<ResultadoWebhookMeta> AceitarAsync(byte[] corpo, string? assinatura, CancellationToken ct);

    /// <summary>O handshake GET: devolve o `hub.challenge` quando o token e de uma conexao nossa,
    /// ou nulo (403).</summary>
    Task<string?> VerificarAsync(string? modo, string? token, string? desafio, CancellationToken ct);
}
