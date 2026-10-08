using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Os tetos da empresa e quanto está em uso (AUD-XX). Qualquer papel: é o que explica por
/// que um botão de criar está desligado, e não revela nada que a tela já não mostre.</summary>
[ApiController]
[Route("api/limites")]
[Authorize]
public class LimitesController(IServicoLimites servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Obter([FromQuery] long? pipeline, CancellationToken ct) =>
        Ok(await servico.ObterAsync(pipeline, ct));
}
