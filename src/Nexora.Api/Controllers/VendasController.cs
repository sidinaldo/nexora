using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>O HISTÓRICO de vendas de um contato (NEG-1).
///
/// LER é para qualquer papel: o vendedor precisa saber que está falando com quem já comprou, e
/// isso é informação comercial de todo dia.
///
/// CANCELAR é do dono e do gestor — a checagem fica no serviço, não num `[Authorize(Roles=...)]`,
/// porque a regra é de negócio ("cancelar tira faturamento da contagem") e precisa valer também
/// quando outro código chamar o serviço sem passar por HTTP.</summary>
[ApiController]
[Route("api")]
[Authorize]
public class VendasController(IServicoVendas servico) : ControllerBase
{
    [HttpGet("contatos/{contatoId:long}/vendas")]
    public async Task<IActionResult> DoContato(long contatoId, CancellationToken ct) =>
        Ok(await servico.DoContatoAsync(contatoId, ct));

    /// <summary>Desfazer uma venda marcada por engano. NÃO é o mesmo que reabrir o contato —
    /// reabrir é "o cliente voltou" e preserva o histórico.
    ///
    /// POST e não DELETE, de propósito: nada é apagado. O verbo descreve o que acontece.</summary>
    [HttpPost("vendas/{id:long}/cancelar")]
    public async Task<IActionResult> Cancelar(long id, CancellationToken ct)
    {
        await servico.CancelarAsync(id, ct);
        return NoContent();
    }
}
