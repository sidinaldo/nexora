using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>O vocabulário de etiquetas da empresa.
///
/// ===================== O `[Authorize]` FICA POR AÇÃO, NÃO NA CLASSE =====================
/// Diferente do `EtapasController`, onde tudo é do dono. Aqui a leitura e a escrita têm públicos
/// distintos:
///
///   • ESCREVER é configuração — define o vocabulário da empresa, e vocabulário que qualquer um
///     inventa vira "Revendedor", "revenda" e "Revendedores" na mesma semana;
///   • LER é do vendedor — ele está atendendo e precisa da lista para escolher qual aplicar.
///
/// Marcar a classe inteira com `Roles = "dono"` fecharia o `GET` e tornaria a etiqueta inútil para
/// quem de fato a usa.
/// ======================================================================================</summary>
[ApiController]
[Route("api/etiquetas")]
[Authorize]
public class EtiquetasController(IServicoEtiquetas servico) : ControllerBase
{
    /// <summary>Qualquer papel: é a lista de onde o vendedor escolhe.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) =>
        Ok(await servico.ListarAsync(ct));

    [HttpPost]
    [Authorize(Roles = "dono")]
    public async Task<IActionResult> Criar([FromBody] NovaEtiqueta nova, CancellationToken ct) =>
        Ok(new { id = await servico.CriarAsync(nova, ct) });

    [HttpPut("{id:long}")]
    [Authorize(Roles = "dono")]
    public async Task<IActionResult> Atualizar(
        long id, [FromBody] EditarEtiqueta dados, CancellationToken ct)
    {
        await servico.AtualizarAsync(id, dados, ct);
        return NoContent();
    }

    [HttpDelete("{id:long}")]
    [Authorize(Roles = "dono")]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        await servico.RemoverAsync(id, ct);
        return NoContent();
    }
}
