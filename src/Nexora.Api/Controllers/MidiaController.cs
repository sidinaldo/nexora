using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Api.Controllers;

/// <summary>Serve a midia recebida pelo WhatsApp. FASE 1: endpoint autenticado simples, sem URL
/// assinada — o binario passa pela API.
///
/// Na fase 2 isto vira URL assinada de curta duracao contra o object storage, e o endpoint deixa
/// de servir bytes. A troca nao afeta quem grava (ver IArmazenamentoMidia).
///
/// O ISOLAMENTO E O QUERY FILTER: a busca da mensagem sai filtrada pela empresa do JWT, entao
/// pedir o id de uma mensagem de outro tenant devolve 404 — nao 403, que ja confirmaria a
/// existencia.</summary>
[ApiController]
[Route("api/midia")]
[Authorize]
public class MidiaController(
    NexoraDbContext db,
    IArmazenamentoMidia armazenamento) : ControllerBase
{
    [HttpGet("{mensagemId:long}")]
    public async Task<IActionResult> Baixar(long mensagemId, CancellationToken ct)
    {
        var midia = await db.Mensagens.AsNoTracking()
            .Where(m => m.Id == mensagemId && m.MidiaChave != null)
            .Select(m => new { m.MidiaChave, m.MidiaMime, m.MidiaNome })
            .FirstOrDefaultAsync(ct);

        if (midia is null) return NotFound();

        var conteudo = await armazenamento.AbrirAsync(midia.MidiaChave!, ct);
        if (conteudo is null) return NotFound();   // linha existe, arquivo sumiu

        return File(conteudo, midia.MidiaMime ?? "application/octet-stream", midia.MidiaNome);
    }
}
