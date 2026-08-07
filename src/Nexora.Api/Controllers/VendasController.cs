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

    /// <summary>"Esse pedido acabou" (NEG-2). Tira o card da coluna Venda SEM tirar o dinheiro do
    /// relatório.
    ///
    /// UM endpoint em LOTE, e não um por venda mais um "em massa" depois: a tela precisa dos dois
    /// (o botão do card e a seleção múltipla da coluna), e uma lista de um id atende o primeiro
    /// caso sem duplicar rota, autorização e teste.
    ///
    /// Devolve quantas de fato mudaram — o que já não estava `fechada` é ignorado em silêncio,
    /// para o lote não falhar inteiro por causa de uma linha que outra pessoa concluiu no meio.</summary>
    [HttpPost("vendas/concluir")]
    public async Task<IActionResult> Concluir([FromBody] ConcluirVendasRequest corpo, CancellationToken ct) =>
        Ok(new { concluidas = await servico.ConcluirAsync(corpo.Ids ?? [], ct) });

    /// <summary>O mesmo concluir, dito pelo CONTATO — é o que o card do kanban tem em mãos.
    ///
    /// Rota SEPARADA e não um campo opcional no mesmo corpo: um corpo que aceita `ids` OU
    /// `contatoIds` teria um caso em que os dois vêm juntos, e nenhuma resposta óbvia para ele.</summary>
    [HttpPost("vendas/concluir-do-contato")]
    public async Task<IActionResult> ConcluirDoContato(
        [FromBody] ConcluirDoContatoRequest corpo, CancellationToken ct) =>
        Ok(new { concluidas = await servico.ConcluirDoContatoAsync(corpo.ContatoIds ?? [], ct) });
}

public record ConcluirVendasRequest(IReadOnlyList<long>? Ids);

public record ConcluirDoContatoRequest(IReadOnlyList<long>? ContatoIds);
