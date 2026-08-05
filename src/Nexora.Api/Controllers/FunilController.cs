using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>O quadro kanban.</summary>
[ApiController]
[Route("api/funil")]
[Authorize]
public class FunilController(IServicoFunil servico) : ControllerBase
{
    /// <summary>O quadro todo: etapas com contagem, soma e os primeiros cards de cada coluna.
    /// SEMPRE paginado por coluna — 3.000 leads em "Novo Lead" derrubariam a tela.</summary>
    [HttpGet]
    public async Task<IActionResult> Quadro(
        [FromQuery] int porColuna = 50, CancellationToken ct = default) =>
        Ok(await servico.QuadroAsync(porColuna, ct));

    /// <summary>Mais cards de UMA coluna. Cursor = (ordemKanban, id) do último card carregado.</summary>
    [HttpGet("etapas/{etapaId:long}/contatos")]
    public async Task<IActionResult> Coluna(
        long etapaId,
        [FromQuery] decimal? cursorOrdem = null,
        [FromQuery] long? cursorId = null,
        [FromQuery] int tamanho = 50,
        CancellationToken ct = default) =>
        Ok(await servico.ColunaAsync(etapaId, cursorOrdem, cursorId, tamanho, ct));

    /// <summary>Move ou reordena. Recusa a etapa de ganho — a venda entra por
    /// `POST /api/contatos/{id}/ganho`, que exige o valor.
    ///
    /// Devolve a nova ordem para o cliente conferir contra o que pintou de forma otimista: se
    /// divergir (houve renormalização da coluna), ele recarrega.</summary>
    [HttpPost("{contatoId:long}/mover")]
    public async Task<IActionResult> Mover(
        long contatoId, [FromBody] MoverContato destino, CancellationToken ct) =>
        Ok(new { ordemKanban = await servico.MoverAsync(contatoId, destino, ct) });
}
