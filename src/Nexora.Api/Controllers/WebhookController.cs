using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Seguranca;
using Nexora.Core.Whatsapp;

namespace Nexora.Api.Controllers;

public class OpcoesWebhook
{
    /// <summary>Segredo na URL do webhook. A Evolution NAO assina o payload — sem isto,
    /// qualquer um que descubra o endereco pode forjar "o contato respondeu" e poluir o funil
    /// com leads falsos. E a unica barreira que temos.</summary>
    public string Segredo { get; set; } = "";
}

[ApiController]
[Route("api/webhook")]
public class WebhookController(
    IProcessadorWebhookWhatsApp processador,
    OpcoesWebhook opcoes,
    ILogger<WebhookController> log) : ControllerBase
{
    /// <summary>Le o corpo cru e entrega ao servico. O controller NAO conhece o formato da
    /// Evolution — quem faz o parse e a Infra.
    ///
    /// Responde 200 mesmo quando o processamento falha, DE PROPOSITO: a Evolution reentrega
    /// ate receber 2xx, e um payload desconhecido ficaria em loop eterno.</summary>
    [HttpPost("evolution")]
    [EnableRateLimiting(RateLimitingConfig.PolWebhook)]
    public async Task<IActionResult> Evolution([FromQuery] string? token, CancellationToken ct)
    {
        // Comparacao em tempo constante nao vale a pena aqui (o segredo esta na URL, que ja
        // aparece em log de proxy), mas segredo vazio NAO pode passar: seria webhook aberto.
        if (string.IsNullOrEmpty(opcoes.Segredo) || token != opcoes.Segredo)
        {
            log.LogWarning("Webhook recusado: token invalido.");
            return Unauthorized();
        }

        using var leitor = new StreamReader(Request.Body);
        var payload = await leitor.ReadToEndAsync(ct);

        await processador.ProcessarAsync(payload, ct);

        return Ok();
    }
}
