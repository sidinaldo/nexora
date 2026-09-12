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
    /// <summary>Qualquer papel: é a lista de onde o vendedor escolhe.
    ///
    /// `busca` e `ordem` são o contrato da API, e o seletor de etiquetas nos cards vai consumir o
    /// primeiro. ⚠️ A TELA `/etiquetas` NÃO OS USA de propósito: o teto de 60 cabe inteiro numa
    /// resposta, e filtrar em memória evita uma ida ao servidor por tecla digitada — com o
    /// debounce, o estado de carregando e a corrida de respostas fora de ordem que viriam junto.
    ///
    /// `ordem` é enum: valor fora da lista morre no model binder, antes do serviço.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] string? busca = null,
        [FromQuery] OrdemEtiqueta ordem = OrdemEtiqueta.Nome,
        CancellationToken ct = default) =>
        Ok(await servico.ListarAsync(busca, ordem, ct));

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
