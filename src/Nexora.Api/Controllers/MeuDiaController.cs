using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A tela de abrir o sistema: o que fazer hoje, sem tabela nova.</summary>
[ApiController]
[Route("api/meu-dia")]
[Authorize]
public class MeuDiaController(IServicoMeuDia servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await servico.MeuDiaAsync(ct));
}
