using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>O webhook de SAÍDA — o Nexora avisando um sistema do cliente.
///
/// ⚠️ Não confundir com `WebhookController`, que é a ENTRADA (a Evolution avisando o Nexora). São
/// direções opostas com modelos de segurança opostos: lá o segredo vem na query string porque a
/// Evolution não assina nada; aqui nós assinamos com HMAC e o segredo nunca viaja.
///
/// Só o DONO: a URL configurada aqui é chamada pelo NOSSO servidor, e recebe dado de cliente.</summary>
[ApiController]
[Route("api/webhooks-saida")]
[Authorize(Roles = "dono")]
public class WebhooksSaidaController(IServicoWebhooks servico) : ControllerBase
{
    /// <summary>A configuração + as últimas 50 entregas. **Nunca devolve o segredo.**</summary>
    [HttpGet]
    public async Task<IActionResult> Obter(CancellationToken ct) =>
        Ok(await servico.ObterAsync(ct));

    /// <summary>Cria ou atualiza. O corpo da resposta traz o segredo SÓ na criação — em toda
    /// atualização ele vem nulo, e a tela não tem como recuperá-lo.</summary>
    [HttpPut]
    public async Task<IActionResult> Salvar([FromBody] SalvarWebhook dados, CancellationToken ct) =>
        Ok(new { segredo = await servico.SalvarAsync(dados, ct) });

    [HttpPost("segredo")]
    public async Task<IActionResult> Regerar(CancellationToken ct) =>
        Ok(await servico.RegerarSegredoAsync(ct));

    [HttpDelete]
    public async Task<IActionResult> Remover(CancellationToken ct)
    {
        await servico.RemoverAsync(ct);
        return NoContent();
    }

    /// <summary>Dispara um evento de teste e ESPERA a resposta — o único endpoint do sistema que
    /// entrega webhook dentro da requisição. A pessoa está olhando o botão.</summary>
    [HttpPost("testar")]
    public async Task<IActionResult> Testar(CancellationToken ct) =>
        Ok(await servico.TestarAsync(ct));

    /// <summary>Devolve uma entrega falha para a fila. Não posta na hora: a próxima rodada posta.</summary>
    [HttpPost("entregas/{id:long}/reenviar")]
    public async Task<IActionResult> Reenviar(long id, CancellationToken ct)
    {
        await servico.ReenviarAsync(id, ct);
        return NoContent();
    }
}
