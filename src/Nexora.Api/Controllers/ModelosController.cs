using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Os templates da API oficial (INT-XX). So o DONO, com o mesmo gesto da conexao: template
/// e configuracao, e passa pela revisao da Meta.
///
/// A lista e a criacao penduram na conexao (`/api/conexoes/{id}/modelos`) — o template nasce de um
/// numero, e sai pelo token dele. Os verbos sobre UM template usam o id dele (`/api/modelos/{id}`).
///
/// Quem ENVIA template e o vendedor, na conversa: ver `ConversasController`.</summary>
[ApiController]
[Route("api")]
[Authorize(Policy = nameof(Permissao.GerenciarConexao))]
public class ModelosController(IServicoModelos servico) : ControllerBase
{
    [HttpGet("conexoes/{conexaoId:long}/modelos")]
    public async Task<IActionResult> Listar(long conexaoId, CancellationToken ct) =>
        Ok(await servico.ListarAsync(conexaoId, ct));

    /// <summary>Cria o RASCUNHO. Nada vai a Meta ainda: o dono revisa o texto antes.</summary>
    [HttpPost("conexoes/{conexaoId:long}/modelos")]
    public async Task<IActionResult> Criar(long conexaoId, [FromBody] NovoModelo novo, CancellationToken ct) =>
        Ok(new { id = await servico.CriarAsync(conexaoId, novo, ct) });

    [HttpPut("modelos/{id:long}")]
    public async Task<IActionResult> Editar(long id, [FromBody] NovoModelo novo, CancellationToken ct)
    {
        await servico.EditarAsync(id, novo, ct);
        return NoContent();
    }

    [HttpDelete("modelos/{id:long}")]
    public async Task<IActionResult> Excluir(long id, CancellationToken ct)
    {
        await servico.ExcluirAsync(id, ct);
        return NoContent();
    }

    /// <summary>Manda para a revisao da Meta.</summary>
    [HttpPost("modelos/{id:long}/enviar")]
    public async Task<IActionResult> Submeter(long id, CancellationToken ct) =>
        Ok(await servico.SubmeterAsync(id, ct));

    /// <summary>Pergunta a Meta como esta a revisao. POST porque grava o que ela responder.</summary>
    [HttpPost("modelos/{id:long}/atualizar")]
    public async Task<IActionResult> Sincronizar(long id, CancellationToken ct) =>
        Ok(await servico.SincronizarAsync(id, ct));
}
