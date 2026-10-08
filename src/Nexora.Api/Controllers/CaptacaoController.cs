using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>O resumo do topo da tela de Captação, pronto (AUD-XX). A mesma permissão da tela.</summary>
[ApiController]
[Route("api/captacao")]
[Authorize(Policy = nameof(Permissao.GerenciarCaptacao))]
public class CaptacaoController(IServicoCaptacao servico) : ControllerBase
{
    [HttpGet("resumo")]
    public async Task<IActionResult> Resumo(CancellationToken ct) =>
        Ok(await servico.ResumoAsync(ct));
}
