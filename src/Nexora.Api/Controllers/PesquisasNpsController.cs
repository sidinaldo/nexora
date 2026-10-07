using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Nps;

namespace Nexora.Api.Controllers;

/// <summary>===================== OS TRES GESTOS SOBRE UMA PESQUISA =====================
///
/// ⚠️ `[Authorize]` SIMPLES, SEM GESTO PROPRIO. Quem ve a conversa decide sobre a pesquisa dela: o
/// aviso "isto e uma nota?" aparece no meio da thread, e quem esta ali atendendo e quem sabe
/// responder. Pedir uma permissao a mais faria o vendedor ver a pergunta e nao poder responde-la.
///
/// O recorte por empresa e o filtro global do EF: este caminho roda na requisicao, com tenant no
/// contexto — diferente do `MotorNps` (job) e da `LeituraDaResposta` (webhook), que carregam o
/// `empresa_id` a mao.
///
/// O relatorio e a lista de respostas moram no `RelatoriosController`, com a barra de filtros.
/// ============================================================================</summary>
[ApiController]
[Route("api/pesquisas-nps")]
[Authorize]
public class PesquisasNpsController(IServicoPesquisaNps servico) : ControllerBase
{
    /// <summary>O historico de notas por compra, para a ficha do contato (NPS-1 3.5).</summary>
    [HttpGet("contato/{contatoId:long}")]
    public async Task<IActionResult> DoContato(long contatoId, CancellationToken ct) =>
        Ok(await servico.DoContatoAsync(contatoId, ct));

    /// <summary>A nota em duvida desta conversa, se houver (NPS-1 3.6). ⚠️ 204, E NAO 404, quando
    /// nao ha: "nenhuma duvida" e a resposta normal de quase toda conversa, e um 404 encheria o
    /// console do navegador de erro a cada conversa aberta.</summary>
    [HttpGet("em-duvida")]
    public async Task<IActionResult> EmDuvida([FromQuery] long conversaId, CancellationToken ct)
    {
        var duvida = await servico.EmDuvidaNaConversaAsync(conversaId, ct);
        if (duvida == null) return NoContent();
        return Ok(duvida);
    }

    /// <summary>"Isto e uma nota X." ⚠️ AS ACOES DA FAIXA CORREM AQUI TAMBEM — confirmar nota 2 na
    /// mao cria o lembrete do detrator igual a nota 2 lida sozinha.</summary>
    [HttpPost("{id:long}/confirmar")]
    public async Task<IActionResult> Confirmar(long id, CancellationToken ct)
    {
        await servico.ConfirmarNotaAsync(id, ct);
        return NoContent();
    }

    /// <summary>"Era outra coisa." A pesquisa volta a esperar a nota de verdade, e a suspeita e
    /// apagada.</summary>
    [HttpPost("{id:long}/nao-e-nota")]
    public async Task<IActionResult> NaoEhNota(long id, CancellationToken ct)
    {
        await servico.NaoEhNotaAsync(id, ct);
        return NoContent();
    }

    /// <summary>"Deixa para la." Para o caso de o cliente pedir para nao ser incomodado, ou de a
    /// venda ter sido erro de registro que ninguem cancelou.</summary>
    [HttpPost("{id:long}/cancelar")]
    public async Task<IActionResult> Cancelar(long id, CancellationToken ct)
    {
        await servico.CancelarAsync(id, ct);
        return NoContent();
    }
}
