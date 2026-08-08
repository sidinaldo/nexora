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
    /// <summary>`limite` corta a LISTA; os contadores da resposta continuam sendo o total.
    ///
    /// O padrão é o teto (200) para quem não passa nada — a tela Meu Dia. O cartão do dashboard
    /// pede 6, que é o que ele mostra: antes ele baixava tudo e jogava fora com `.slice`.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? limite, CancellationToken ct) =>
        Ok(await servico.MeuDiaAsync(limite ?? LimiteMeuDia.Maximo, ct));
}
