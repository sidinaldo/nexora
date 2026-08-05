using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A equipe da empresa. Só o DONO — convidar e mudar papel é configuração, não
/// atendimento. O ACEITE do convite é público, e mora no `ConviteController`.</summary>
[ApiController]
[Route("api/equipe")]
[Authorize(Roles = "dono")]
public class EquipeController(IServicoEquipe servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) =>
        Ok(await servico.ListarAsync(ct));

    /// <summary>Convida e devolve o TOKEN para o dono montar o link e mandar por fora.
    /// Não há envio de e-mail na fase 1 — limitação registrada desde o bloco 1.</summary>
    [HttpPost("convites")]
    public async Task<IActionResult> Convidar([FromBody] NovoConvite novo, CancellationToken ct) =>
        Ok(await servico.ConvidarAsync(novo, ct));

    [HttpPost("{id:long}/reenviar-convite")]
    public async Task<IActionResult> Reenviar(long id, CancellationToken ct) =>
        Ok(await servico.ReenviarConviteAsync(id, ct));

    [HttpPost("{id:long}/reset-senha")]
    public async Task<IActionResult> ResetSenha(long id, CancellationToken ct) =>
        Ok(await servico.GerarResetSenhaAsync(id, ct));

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Atualizar(long id, [FromBody] EditarUsuario dados, CancellationToken ct)
    {
        await servico.AtualizarAsync(id, dados, ct);
        return NoContent();
    }
}
