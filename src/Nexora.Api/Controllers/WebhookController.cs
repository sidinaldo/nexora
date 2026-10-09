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
    IRecepcaoWebhookMeta recepcaoMeta,
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

    // ==================================================================== Cloud API (INT-XX)
    /// <summary>O handshake da Meta ao cadastrar a URL: ela manda o `verify_token` que o cliente
    /// colou no app dela, e espera o `challenge` de volta, em texto puro.</summary>
    [HttpGet("meta")]
    [EnableRateLimiting(RateLimitingConfig.PolWebhook)]
    public async Task<IActionResult> MetaVerificar(
        [FromQuery(Name = "hub.mode")] string? modo,
        [FromQuery(Name = "hub.verify_token")] string? token,
        [FromQuery(Name = "hub.challenge")] string? desafio,
        CancellationToken ct)
    {
        var resposta = await recepcaoMeta.VerificarAsync(modo, token, desafio, ct);
        if (resposta == null) return StatusCode(StatusCodes.Status403Forbidden);
        return Content(resposta, "text/plain");
    }

    /// <summary>As entregas da Meta. Le os BYTES crus — a assinatura e sobre eles, e reler o JSON
    /// mudaria o que foi assinado — e responde rapido: o processamento e em segundo plano.
    ///
    /// Sem `[Authorize]` (a Meta nao se autentica como usuario), com teto de tamanho e de taxa. Quem
    /// prova a origem e a assinatura `X-Hub-Signature-256`.</summary>
    [HttpPost("meta")]
    [EnableRateLimiting(RateLimitingConfig.PolWebhook)]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> MetaReceber(CancellationToken ct)
    {
        using var memoria = new MemoryStream();
        await Request.Body.CopyToAsync(memoria, ct);

        var assinatura = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        var resultado = await recepcaoMeta.AceitarAsync(memoria.ToArray(), assinatura, ct);

        if (resultado == ResultadoWebhookMeta.AssinaturaInvalida) return Unauthorized();
        return Ok();
    }
}
