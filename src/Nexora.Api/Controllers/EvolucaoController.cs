using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A evolução da conversão, mês a mês (EVO-1).
///
/// ⚠️ `[Authorize]` SOZINHO, igual ao `RelatoriosController`, e a ausência de policy é deliberada:
/// vendedor PODE ver a própria evolução — é o número dele. O recorte é por LINHA e mora no serviço
/// (`ServicoEvolucao`, pelo gesto `ver_numeros_da_equipe`). Um
/// `[Authorize(Policy = nameof(Permissao.VerNumerosDaEquipe))]` aqui daria 403 ao vendedor e
/// tiraria dele uma tela que é legitimamente sua.
///
/// ⚠️ A JANELA NÃO É VALIDADA AQUI. `meses` entra numa conta de calendário, e a lista fechada
/// (3/6/12) mora no serviço com a razão ao lado. Repetir a checagem no controller daria dois
/// lugares para a mesma regra, e o dia em que a tela oferecer 24 meses um deles ficaria para
/// trás.</summary>
[ApiController]
[Route("api/evolucao")]
[Authorize]
public class EvolucaoController(IServicoEvolucao servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Obter([FromQuery] int meses = 6, CancellationToken ct = default) =>
        Ok(await servico.ObterAsync(meses, ct));
}
