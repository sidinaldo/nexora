using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Os formulários de captação, na área logada. Só o DONO configura: a chave gerada aqui
/// abre um endpoint de escrita na internet.
///
/// Sem `[EnableCors]`: esta rota é do painel e usa a política padrão, de lista fixa. Só a rota
/// PÚBLICA de captação precisa aceitar a origem do site do cliente.</summary>
[ApiController]
[Route("api/formularios")]
[Authorize(Roles = "dono")]
public class FormulariosController(IServicoFormularios servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) =>
        Ok(await servico.ListarAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] NovoFormulario novo, CancellationToken ct) =>
        Ok(new { id = await servico.CriarAsync(novo, ct) });

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Atualizar(
        long id, [FromBody] NovoFormulario dados, CancellationToken ct)
    {
        await servico.AtualizarAsync(id, dados, ct);
        return NoContent();
    }

    [HttpPost("{id:long}/ativo")]
    public async Task<IActionResult> Alternar(
        long id, [FromBody] AlternarFormulario corpo, CancellationToken ct)
    {
        await servico.AlternarAtivoAsync(id, corpo.Ativo, ct);
        return NoContent();
    }

    /// <summary>Regera a chave. A antiga para de funcionar NA HORA — é o ponto de existir: quem
    /// regera está reagindo a um vazamento. O HTML no site do cliente precisa ser trocado.</summary>
    [HttpPost("{id:long}/chave")]
    public async Task<IActionResult> Regerar(long id, CancellationToken ct) =>
        Ok(new { chave = await servico.RegerarChaveAsync(id, ct) });
}

public record AlternarFormulario(bool Ativo);
