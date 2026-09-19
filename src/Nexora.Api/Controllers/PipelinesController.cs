using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;
using Nexora.Core.Seguranca;

namespace Nexora.Api.Controllers;

/// <summary>Os funis da empresa.
///
/// ===================== O `[Authorize]` FICA POR AÇÃO, NÃO NA CLASSE =====================
/// Mesma assimetria de `EtiquetasController`:
///
///   • LER é de qualquer papel — a lista alimenta o MENU, e o vendedor precisa navegar entre os
///     quadros para trabalhar;
///   • ESCREVER é do dono — criar um funil é definir um processo de trabalho para a empresa
///     inteira, não uma escolha de quem está atendendo.
///
/// Exigir `ConfigurarEmpresa` na classe fecharia o GET e deixaria metade da equipe sem menu.
/// ======================================================================================</summary>
[ApiController]
[Route("api/pipelines")]
[Authorize]
public class PipelinesController(IServicoPipelines servico) : ControllerBase
{
    /// <summary>Qualquer papel: é o menu.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) =>
        Ok(await servico.ListarAsync(ct));

    [HttpPost]
    [Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]
    public async Task<IActionResult> Criar([FromBody] NovaPipeline nova, CancellationToken ct) =>
        Ok(new { id = await servico.CriarAsync(nova, ct) });

    [HttpPut("{id:long}")]
    [Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]
    public async Task<IActionResult> Atualizar(
        long id, [FromBody] EditarPipeline dados, CancellationToken ct)
    {
        await servico.AtualizarAsync(id, dados, ct);
        return NoContent();
    }

    /// <summary>Rota própria, e não um campo no PUT: marcar como padrão muda para onde TODO lead
    /// novo vai. Escondido dentro de um formulário de renomear, seria mudado sem querer.
    /// Mesmo desenho de `POST /api/etapas/{id}/ganho`.</summary>
    [HttpPost("{id:long}/padrao")]
    [Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]
    public async Task<IActionResult> DefinirPadrao(long id, CancellationToken ct)
    {
        await servico.DefinirPadraoAsync(id, ct);
        return NoContent();
    }

    [HttpDelete("{id:long}")]
    [Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        await servico.RemoverAsync(id, ct);
        return NoContent();
    }
}
