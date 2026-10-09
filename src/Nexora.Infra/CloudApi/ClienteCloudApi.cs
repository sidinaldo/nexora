using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexora.Core.Servicos;

namespace Nexora.Infra.CloudApi;

/// <summary>O numero como a Meta o descreve. `Numero` ja vem so com digitos.</summary>
public record NumeroCloud(
    string Numero, string? NomeVerificado, string? Qualidade, string? StatusDoNome,
    string? StatusDaVerificacao);

/// <summary>A Graph API da Meta, na parte do WhatsApp (INT-XX). Interface para os testes
/// trocarem a rede por um duble.</summary>
public interface IClienteCloudApi
{
    /// <summary>Le o numero. LANCA `IntegracaoWhatsAppException` com a causa em portugues.</summary>
    Task<NumeroCloud> LerNumeroAsync(string phoneNumberId, string token, CancellationToken ct);

    /// <summary>O numero pertence a esta WABA? E o que impede cadastrar o numero de outra conta
    /// com um token que por acaso o enxerga.</summary>
    Task<bool> NumeroEstaNaWabaAsync(string wabaId, string phoneNumberId, string token, CancellationToken ct);

    /// <summary>Inscreve o app nos webhooks da WABA. Sem isso a Meta nao manda as mensagens
    /// recebidas, mesmo com a URL configurada. Idempotente.</summary>
    Task AssinarWebhooksAsync(string wabaId, string token, CancellationToken ct);

    /// <summary>`open` | `close` | `offline`, o mesmo vocabulario da Evolution — o verificador de
    /// 5 minutos, o banner e o freio de envio funcionam sem saber o canal. NUNCA LANCA.</summary>
    Task<string> EstadoAsync(string phoneNumberId, string token, CancellationToken ct);
}

/// <summary>===================== A GRAPH API, PELA CONEXAO OFICIAL (INT-XX) =====================
///
/// ⚠️ O TOKEN VAI NO CABECALHO `Authorization: Bearer`, NUNCA NA QUERY STRING. Query string aparece
/// em log de proxy, de balanceador e de erro — o mesmo cuidado do `ClienteMeta` das conversoes.
///
/// ⚠️ A VERSAO E FIXA (`Versao`). A Meta aposenta cada versao uns dois anos depois de lancar; trocar e
/// uma decisao, nao um efeito colateral de atualizar pacote.
///
/// Os ids entram no caminho ESCAPADOS: vem do formulario do cliente, e um `/` ali mudaria o recurso.
/// ===========================================================================================</summary>
public class ClienteCloudApi(HttpClient http, ILogger<ClienteCloudApi> log) : IClienteCloudApi
{
    /// <summary>Graph API v25.0, de fevereiro de 2026.</summary>
    public const string Versao = "v25.0";

    public async Task<NumeroCloud> LerNumeroAsync(string phoneNumberId, string token, CancellationToken ct)
    {
        var caminho = $"{Versao}/{Uri.EscapeDataString(phoneNumberId)}"
                    + "?fields=display_phone_number,verified_name,quality_rating,name_status,code_verification_status";

        using var resposta = await EnviarAsync(HttpMethod.Get, caminho, token, ct);
        var corpo = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDaMeta(corpo));

        using var doc = JsonDocument.Parse(corpo);
        var raiz = doc.RootElement;
        var exibido = Texto(raiz, "display_phone_number") ?? "";

        return new NumeroCloud(
            new string(exibido.Where(char.IsAsciiDigit).ToArray()),
            Texto(raiz, "verified_name"),
            Texto(raiz, "quality_rating"),
            Texto(raiz, "name_status"),
            Texto(raiz, "code_verification_status"));
    }

    public async Task<bool> NumeroEstaNaWabaAsync(
        string wabaId, string phoneNumberId, string token, CancellationToken ct)
    {
        var caminho = $"{Versao}/{Uri.EscapeDataString(wabaId)}/phone_numbers?fields=id&limit=100";

        using var resposta = await EnviarAsync(HttpMethod.Get, caminho, token, ct);
        var corpo = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDaMeta(corpo));

        using var doc = JsonDocument.Parse(corpo);
        if (!doc.RootElement.TryGetProperty("data", out var dados) || dados.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in dados.EnumerateArray())
        {
            if (Texto(item, "id") == phoneNumberId) return true;
        }
        return false;
    }

    public async Task AssinarWebhooksAsync(string wabaId, string token, CancellationToken ct)
    {
        var caminho = $"{Versao}/{Uri.EscapeDataString(wabaId)}/subscribed_apps";

        using var resposta = await EnviarAsync(HttpMethod.Post, caminho, token, ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDaMeta(await resposta.Content.ReadAsStringAsync(ct)));
    }

    public async Task<string> EstadoAsync(string phoneNumberId, string token, CancellationToken ct)
    {
        try
        {
            var caminho = $"{Versao}/{Uri.EscapeDataString(phoneNumberId)}?fields=id";
            using var resposta = await EnviarAsync(HttpMethod.Get, caminho, token, ct);

            if (resposta.IsSuccessStatusCode) return "open";

            // 5xx e limite de taxa sao problema da Meta, nao do numero: `offline`, como a Evolution
            // fora do ar. O resto (token vencido, numero removido) e o numero que caiu.
            var codigo = (int)resposta.StatusCode;
            if (codigo >= 500 || resposta.StatusCode == HttpStatusCode.TooManyRequests) return "offline";
            return "close";
        }
        catch (IntegracaoWhatsAppException)
        {
            return "offline";
        }
    }

    // ==================================================================== apoio
    private async Task<HttpResponseMessage> EnviarAsync(
        HttpMethod metodo, string caminho, string token, CancellationToken ct)
    {
        using var pedido = new HttpRequestMessage(metodo, caminho);
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            return await http.SendAsync(pedido, ct);
        }
        catch (HttpRequestException ex)
        {
            throw SemResposta(ex, metodo, caminho);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout do HttpClient. Cancelamento de quem chamou continua subindo como esta.
            throw SemResposta(ex, metodo, caminho);
        }
    }

    private IntegracaoWhatsAppException SemResposta(Exception ex, HttpMethod metodo, string caminho)
    {
        log.LogWarning(ex, "A Graph API da Meta nao respondeu ({Metodo} {Recurso}).",
            metodo, caminho.Split('?')[0]);
        return new IntegracaoWhatsAppException("A Meta não respondeu. Tente de novo em alguns minutos.", ex);
    }

    /// <summary>O erro da Graph API em portugues. O `message` dela vai junto quando o codigo nao e
    /// um dos conhecidos — e o que permite investigar sem abrir log.</summary>
    internal static string ErroDaMeta(string corpo)
    {
        int? codigo = null;
        string? mensagem = null;

        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.TryGetProperty("error", out var erro) && erro.ValueKind == JsonValueKind.Object)
            {
                if (erro.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
                    codigo = c.GetInt32();
                mensagem = Texto(erro, "message");
            }
        }
        catch (JsonException)
        {
            // Corpo que nao e JSON: cai na mensagem generica.
        }

        if (codigo == 190)
            return "A Meta recusou o token: ele expirou ou não vale para esta conta.";
        if (codigo == 100)
            return "A Meta não encontrou este número com este token. Confira o Phone Number ID e o WABA ID.";
        if (codigo == 10 || codigo == 200)
            return "O token não tem permissão de WhatsApp. Ele precisa de whatsapp_business_messaging e whatsapp_business_management.";
        if (mensagem != null)
            return "A Meta recusou: " + mensagem;
        return "A Meta recusou o pedido.";
    }

    private static string? Texto(JsonElement no, string campo)
    {
        if (no.ValueKind != JsonValueKind.Object) return null;
        if (!no.TryGetProperty(campo, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return v.GetString();
    }
}
