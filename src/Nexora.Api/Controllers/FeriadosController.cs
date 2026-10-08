using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;
using Nexora.Core.Seguranca;

namespace Nexora.Api.Controllers;

/// <summary>Feriados. LER é para todo mundo — o calendário de follow-up depende disso, e o
/// semáforo de urgência também. **Escrever é do dono**, nas quatro rotas.
///
/// ⚠️ O `Criar` já foi de dono E GESTOR, e essa era a linha fora do lugar: o gestor criava um
/// feriado e não podia apagá-lo nem marcá-lo como dia de trabalho, e não tinha tela por onde
/// fazer nem o que podia. Criar sem desfazer é a pior metade.</summary>
[ApiController]
[Route("api/feriados")]
[Authorize]
public class FeriadosController(IServicoFeriados servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Proximos(
        [FromQuery] int pagina = 1, [FromQuery] int tamanho = 20, CancellationToken ct = default) =>
        Ok(await servico.ProximosAsync(pagina, tamanho, ct));

    [HttpPost]
    [Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]
    public async Task<IActionResult> Criar([FromBody] NovoFeriado novo, CancellationToken ct) =>
        Ok(new { id = await servico.CriarManualAsync(novo, ct) });

    /// <summary>Só remove feriado MANUAL da própria empresa — os nacionais são globais e
    /// compartilhados entre todos os tenants. Para não observar um nacional, use `trabalha`.</summary>
    [HttpDelete("{id:long}")]
    [Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        await servico.RemoverManualAsync(id, ct);
        return NoContent();
    }

    /// <summary>"Nesta empresa a gente trabalha nesse feriado." Vale só para o nacional, e só
    /// para quem pediu — a linha global continua intacta para os outros tenants.</summary>
    [HttpPost("{id:long}/trabalha")]
    [Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]
    public async Task<IActionResult> Ignorar(long id, CancellationToken ct)
    {
        await servico.IgnorarAsync(id, ct);
        return NoContent();
    }

    [HttpDelete("{id:long}/trabalha")]
    [Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]
    public async Task<IActionResult> Reativar(long id, CancellationToken ct)
    {
        await servico.ReativarAsync(id, ct);
        return NoContent();
    }
}
