using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Os primeiros passos da empresa.
///
/// LER é para qualquer papel — o vendedor que entra numa conta recém-criada também merece saber
/// que o WhatsApp ainda não foi conectado, em vez de encarar uma caixa vazia sem explicação.
///
/// DISPENSAR é só do dono: é decisão de quem responde pela conta.</summary>
[ApiController]
[Route("api/onboarding")]
[Authorize]
public class OnboardingController(IServicoOnboarding servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Obter(CancellationToken ct) =>
        Ok(await servico.ObterAsync(ct));

    /// <summary>"Convido a equipe depois." Resolve o passo sem cumpri-lo.</summary>
    [HttpPost("equipe/dispensar")]
    [Authorize(Roles = "dono")]
    public async Task<IActionResult> DispensarEquipe(CancellationToken ct)
    {
        await servico.DispensarEquipeAsync(ct);
        return NoContent();
    }

    /// <summary>Fecha o painel. Onboarding que prende o usuário irrita mais do que ajuda.</summary>
    [HttpPost("dispensar")]
    [Authorize(Roles = "dono")]
    public async Task<IActionResult> Dispensar(CancellationToken ct)
    {
        await servico.DispensarAsync(ct);
        return NoContent();
    }
}
