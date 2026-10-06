using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Os leads que pararam de ser trabalhados (LPA-1).
///
/// ⚠️ `[Authorize]` SOZINHO, igual ao `RelatoriosController`, e a ausência de policy é deliberada:
/// o vendedor PODE ver os leads parados DELE — é a lista de trabalho dele, não um número da
/// equipe. O recorte é por LINHA e mora no serviço (`ServicoLeadsParados`, pelo gesto
/// `ver_numeros_da_equipe`). Uma policy aqui daria 403 ao vendedor e tiraria dele uma tela que é
/// legitimamente sua.
///
/// ⚠️ ESTA ENTREGA É SÓ LEITURA. As ações em lote vêm depois, e é lá que nasce o gesto próprio —
/// ver não é agir, e antecipar a permissão fecharia a tela para quem ela mais ajuda.
///
/// A janela em dias não é validada aqui: ela entra numa conta de calendário, e a lista fechada
/// mora no serviço com a razão ao lado. Repetir a checagem daria dois lugares para a mesma
/// regra.</summary>
[ApiController]
[Route("api/leads-parados")]
[Authorize]
public class LeadsParadosController(IServicoLeadsParados servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] ParametrosLeadsParados q, CancellationToken ct) =>
        Ok(await servico.ListarAsync(
            new FiltroLeadsParados(
                q.Dias ?? JanelasDeParada.Padrao,
                q.ResponsavelId,
                q.Pagina ?? 1,
                q.Tamanho ?? JanelasDeParada.TamanhoMaximoPagina),
            ct));
}

/// <summary>Os filtros da tela, do jeito que chegam na query string. Todos opcionais: a tela abre
/// sem recorte nenhum e o serviço aplica os padrões.</summary>
public record ParametrosLeadsParados(
    int? Dias = null,
    long? ResponsavelId = null,
    int? Pagina = null,
    int? Tamanho = null);
