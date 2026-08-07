using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A trilha de auditoria (AUD-1).
///
/// `[Authorize]` simples: a regra de papel esta no SERVICO, para valer por qualquer caminho.
/// Duplica-la num `Roles=` aqui criaria duas fontes da mesma verdade — e a do controller e a que
/// se esquece de atualizar.</summary>
[ApiController]
[Route("api/trilha")]
[Authorize]
public class TrilhaController(IServicoTrilha servico) : ControllerBase
{
    [HttpGet("contato/{id:long}")]
    public async Task<IActionResult> Contato(long id, CancellationToken ct, int tamanho = 50) =>
        Ok(await servico.DoRegistroAsync(EntidadeAuditada.Contato, id, tamanho, ct));

    [HttpGet("venda/{id:long}")]
    public async Task<IActionResult> Venda(long id, CancellationToken ct, int tamanho = 50) =>
        Ok(await servico.DoRegistroAsync(EntidadeAuditada.Venda, id, tamanho, ct));

    [HttpGet("empresa/{id:long}")]
    public async Task<IActionResult> Empresa(long id, CancellationToken ct, int tamanho = 50) =>
        Ok(await servico.DoRegistroAsync(EntidadeAuditada.Empresa, id, tamanho, ct));
}
