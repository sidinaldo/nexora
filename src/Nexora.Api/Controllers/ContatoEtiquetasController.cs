using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

public record NovasEtiquetasDoContato(IReadOnlyList<long>? Ids);

/// <summary>As etiquetas coladas num contato.
///
/// ===================== POR QUE UM CONTROLLER PRÓPRIO =====================
/// A rota é `api/contatos/{id}/etiquetas`, mas a lógica é de etiqueta — e `EtiquetasController`
/// está preso em `[Route("api/etiquetas")]`. Em vez de forçar um dos dois, este segue o molde que
/// `VendasController` já usa para `api/contatos/{id}/vendas`: `[Route("api")]` com o caminho
/// inteiro em cada ação.
/// =======================================================================
///
/// ===================== AQUI NÃO HÁ PAPEL =====================
/// Nenhuma ação é do dono, e é o ponto de todo o bloco: criar etiqueta é CONFIGURAÇÃO — define o
/// vocabulário da empresa — e por isso `POST /api/etiquetas` é só do dono. APLICAR é TRABALHO DO
/// DIA: quem está atendendo vê que o cliente é revendedor e marca ali, sem pedir permissão a
/// ninguém.
///
/// Fechar isto ao dono tornaria a etiqueta inútil para quem de fato a usa.
/// ============================================================</summary>
[ApiController]
[Route("api")]
[Authorize]
public class ContatoEtiquetasController(IServicoEtiquetas servico) : ControllerBase
{
    [HttpGet("contatos/{contatoId:long}/etiquetas")]
    public async Task<IActionResult> Listar(long contatoId, CancellationToken ct) =>
        Ok(await servico.DoContatoAsync(contatoId, ct));

    /// <summary>Substitui o conjunto inteiro. `PUT`, e não `POST`/`DELETE` por etiqueta: a
    /// operação é idempotente, e repetir por duplo clique ou retry de rede dá o mesmo resultado.
    ///
    /// Corpo vazio ou `ids` nulo limpa as etiquetas do contato — é o caso legítimo de desmarcar
    /// a última, e recusá-lo obrigaria a tela a ter um segundo caminho só para isso.</summary>
    [HttpPut("contatos/{contatoId:long}/etiquetas")]
    public async Task<IActionResult> Aplicar(
        long contatoId, [FromBody] NovasEtiquetasDoContato corpo, CancellationToken ct)
    {
        await servico.AplicarAsync(contatoId, corpo?.Ids ?? [], ct);
        return NoContent();
    }

    // ==================================================================== a etiqueta do negócio
    /// <summary>⚠️ ROTA IRMÃ, E NÃO UM PARÂMETRO NA DE CIMA. Um corpo que aceitasse
    /// `contatoId` OU `negociacaoId` teria um caso em que os dois vêm juntos e nenhuma resposta
    /// óbvia para ele — o mesmo argumento que separou `vendas/concluir` na época.
    ///
    /// São dois ALVOS diferentes para o mesmo vocabulário: "Revendedor" gruda na pessoa e vale em
    /// todos os negócios dela; "Urgente" gruda num negócio e não diz nada sobre o outro.</summary>
    [HttpGet("negociacoes/{negociacaoId:long}/etiquetas")]
    public async Task<IActionResult> ListarDoNegocio(long negociacaoId, CancellationToken ct) =>
        Ok(await servico.DaNegociacaoAsync(negociacaoId, ct));

    [HttpPut("negociacoes/{negociacaoId:long}/etiquetas")]
    public async Task<IActionResult> AplicarNoNegocio(
        long negociacaoId, [FromBody] NovasEtiquetasDoContato corpo, CancellationToken ct)
    {
        await servico.AplicarNaNegociacaoAsync(negociacaoId, corpo?.Ids ?? [], ct);
        return NoContent();
    }
}
