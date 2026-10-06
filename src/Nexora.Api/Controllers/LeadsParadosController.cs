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
                q.Tamanho ?? JanelasDeParada.TamanhoMaximoPagina,
                q.PipelineId,
                q.EtapaId,
                // ⚠️ `Origem` CHEGA COMO TEXTO, nao como enum. `[FromQuery]` sobre enum devolve
                // 400 generico do model binder quando o valor nao casa, sem dizer qual campo —
                // mesma decisao do `ParametrosRelatorio`. A comparacao no SQL e `::text`.
                string.IsNullOrWhiteSpace(q.Origem) ? null : q.Origem.Trim().ToLowerInvariant(),
                q.EtiquetaId,
                q.ValorMin,
                q.ValorMax),
            ct));

    /// <summary>⚠️ SEM `[Authorize(Policy=)]` AQUI TAMBEM, e nao por esquecimento: a trava do gesto
    /// `AgirEmLote` esta no SERVICO. Uma policy na rota daria 403 sem dizer o que falta, e o
    /// servico ja devolve a frase que o operador precisa ler. Uma fonte da verdade.</summary>
    /// <summary>O que a reativacao rendeu. ⚠️ SEM GESTO, como a listagem: quem nao ve os numeros
    /// da equipe recebe o numero dos PROPRIOS negocios, e isso e util para ele.</summary>
    [HttpGet("reativacao")]
    public async Task<IActionResult> Reativacao(
        [FromQuery] long etiquetaId, [FromQuery] DateOnly de, [FromQuery] DateOnly ate,
        [FromQuery] long? responsavelId, CancellationToken ct) =>
        Ok(await servico.ReativacaoAsync(
            new FiltroReativacao(etiquetaId, de, ate, responsavelId), ct));

    /// <summary>⚠️ ADICIONA A ETIQUETA, nao substitui o conjunto como o
    /// `PUT /api/etiquetas/negociacoes/{id}/etiquetas`. Em lote, substituir apagaria as outras
    /// etiquetas de cinquenta cards de uma vez.</summary>
    [HttpPost("etiquetas")]
    public async Task<IActionResult> AplicarEtiqueta(
        [FromBody] EtiquetaEmLote pedido, CancellationToken ct) =>
        Ok(await servico.AplicarEtiquetaAsync(pedido, ct));

    [HttpPost("lembretes")]
    public async Task<IActionResult> CriarLembretes(
        [FromBody] LembreteEmLote pedido, CancellationToken ct) =>
        Ok(await servico.CriarLembretesAsync(pedido, ct));
}

/// <summary>Os filtros da tela, do jeito que chegam na query string. Todos opcionais: a tela abre
/// sem recorte nenhum e o serviço aplica os padrões.</summary>
public record ParametrosLeadsParados(
    int? Dias = null,
    long? ResponsavelId = null,
    int? Pagina = null,
    int? Tamanho = null,
    long? PipelineId = null,
    long? EtapaId = null,
    string? Origem = null,
    long? EtiquetaId = null,
    decimal? ValorMin = null,
    decimal? ValorMax = null);
