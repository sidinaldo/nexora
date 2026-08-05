using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Feriados. LER é para todo mundo (o calendário de follow-up depende disso); CRIAR e
/// REMOVER é configuração da empresa, então dono e gestor.</summary>
[ApiController]
[Route("api/feriados")]
[Authorize]
public class FeriadosController(IServicoFeriados servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Proximos(CancellationToken ct) =>
        Ok(await servico.ProximosAsync(ct));

    [HttpPost]
    [Authorize(Roles = "dono,gestor")]
    public async Task<IActionResult> Criar([FromBody] NovoFeriado novo, CancellationToken ct) =>
        Ok(new { id = await servico.CriarManualAsync(novo, ct) });

    /// <summary>Só remove feriado MANUAL da própria empresa — os nacionais são globais e
    /// compartilhados entre todos os tenants. Para não observar um nacional, use `trabalha`.</summary>
    [HttpDelete("{id:long}")]
    [Authorize(Roles = "dono")]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        await servico.RemoverManualAsync(id, ct);
        return NoContent();
    }

    /// <summary>"Nesta empresa a gente trabalha nesse feriado." Vale só para o nacional, e só
    /// para quem pediu — a linha global continua intacta para os outros tenants.</summary>
    [HttpPost("{id:long}/trabalha")]
    [Authorize(Roles = "dono")]
    public async Task<IActionResult> Ignorar(long id, CancellationToken ct)
    {
        await servico.IgnorarAsync(id, ct);
        return NoContent();
    }

    [HttpDelete("{id:long}/trabalha")]
    [Authorize(Roles = "dono")]
    public async Task<IActionResult> Reativar(long id, CancellationToken ct)
    {
        await servico.ReativarAsync(id, ct);
        return NoContent();
    }
}
