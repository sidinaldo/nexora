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
/// ⚠️ A LISTAGEM E O RELATORIO NAO ESTAO AQUI: sao da Etapa 3, junto da tela.
/// ============================================================================</summary>
[ApiController]
[Route("api/pesquisas-nps")]
[Authorize]
public class PesquisasNpsController(IServicoPesquisaNps servico) : ControllerBase
{
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
