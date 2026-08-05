using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>O payload BARATO do shell (badge + banner). O rico, com funil e séries, é do
/// dashboard e vem sob demanda — separação herdada do Recupera.</summary>
[ApiController]
[Route("api/painel")]
[Authorize]
public class PainelController(IServicoPainel servico) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct) =>
        Ok(await servico.StatusAsync(ct));
}
