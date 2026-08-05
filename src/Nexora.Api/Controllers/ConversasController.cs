using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>O caminho quente do vendedor: responder na conversa.
///
/// Qualquer papel atende — atendimento e o trabalho do vendedor, nao configuracao.</summary>
[ApiController]
[Route("api/conversas")]
[Authorize]
public class ConversasController(IServicoConversas servico, IServicoCaixa caixa) : ControllerBase
{
    /// <summary>A lista da caixa, paginada por CURSOR (cursorEm = ultima_mensagem_em +
    /// cursorId = id do último item). Nulos = primeira página.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] FiltroConversa filtro = FiltroConversa.Aguardando,
        [FromQuery] string? busca = null,
        [FromQuery] DateTime? cursorEm = null,
        [FromQuery] long? cursorId = null,
        [FromQuery] int tamanho = 30,
        CancellationToken ct = default) =>
        Ok(await caixa.ConversasAsync(filtro, busca, cursorEm, cursorId, tamanho, ct));

    /// <summary>A thread, por cursor: as `tamanho` mensagens mais novas antes de `antes`
    /// (null = as últimas).</summary>
    [HttpGet("{id:long}/mensagens")]
    public async Task<IActionResult> Mensagens(
        long id, [FromQuery] long? antes = null, [FromQuery] int tamanho = 30,
        CancellationToken ct = default) =>
        Ok(await caixa.MensagensAsync(id, antes, tamanho, ct));

    /// <summary>Zera o contador de não lidas. NÃO mexe no semáforo: ler não é responder.</summary>
    [HttpPost("{id:long}/lida")]
    public async Task<IActionResult> MarcarLida(long id, CancellationToken ct)
    {
        await caixa.MarcarLidaAsync(id, ct);
        return NoContent();
    }

    /// <summary>Responder. Registrar a mensagem e SUCESSO; a entrega ao WhatsApp falhar e um
    /// detalhe (`enviada: false`) — a linha fica gravada com o erro e a tela a mostra como
    /// "não chegou". Devolver 502 aqui esconderia o id que a tela precisa para o balão.</summary>
    [HttpPost("{id:long}/responder")]
    public async Task<IActionResult> Responder(long id, [FromBody] ResponderRequest req, CancellationToken ct) =>
        Ok(await servico.ResponderAsync(id, req.Texto, ct));

    /// <summary>Assume a conversa (vira o dono). 409 se já for de outro vendedor.</summary>
    [HttpPost("{id:long}/assumir")]
    public async Task<IActionResult> Assumir(long id, CancellationToken ct)
    {
        await servico.AssumirAsync(id, ct);
        return NoContent();
    }

    /// <summary>Devolve para "Não atribuídas".</summary>
    [HttpPost("{id:long}/liberar")]
    public async Task<IActionResult> Liberar(long id, CancellationToken ct)
    {
        await servico.LiberarAsync(id, ct);
        return NoContent();
    }
}

public record ResponderRequest(string Texto);
