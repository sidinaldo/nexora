using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Lembretes manuais. Qualquer papel: marcar tarefa é atendimento, não configuração.</summary>
[ApiController]
[Route("api/lembretes")]
[Authorize]
public class LembretesController(IServicoLembretes servico) : ControllerBase
{
    [HttpGet("contato/{contatoId:long}")]
    public async Task<IActionResult> DoContato(long contatoId, CancellationToken ct) =>
        Ok(await servico.DoContatoAsync(contatoId, ct));

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] NovoLembrete novo, CancellationToken ct) =>
        Ok(new { id = await servico.CriarAsync(novo, ct) });

    [HttpPost("{id:long}/concluir")]
    public async Task<IActionResult> Concluir(long id, CancellationToken ct)
    {
        await servico.ConcluirAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:long}/cancelar")]
    public async Task<IActionResult> Cancelar(long id, CancellationToken ct)
    {
        await servico.CancelarAsync(id, ct);
        return NoContent();
    }
}
