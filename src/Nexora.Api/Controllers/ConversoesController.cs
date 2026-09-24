using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A credencial de anúncio da empresa — o pixel e o token com que o Nexora avisa a Meta
/// de que um lead virou venda (INT-4).
///
/// ===================== NENHUMA PERMISSÃO NOVA =====================
/// `ConfigurarEmpresa`, a mesma do webhook de saída. A tabela de permissões já diz que integração
/// é `ConfigurarEmpresa`, e criar uma permissão por parceiro faria a tabela crescer um item por
/// integração para responder sempre a mesma pergunta.
/// ==================================================================
///
/// ⚠️ O `GET` NUNCA DEVOLVE O TOKEN. Nem na criação, ao contrário do segredo do webhook: aquele nós
/// geramos e dava para revelar uma vez; este o cliente cola do Gerenciador de Eventos da Meta, e
/// não temos o que revelar. A tela mostra sufixo mascarado.</summary>
[ApiController]
[Route("api/conversoes")]
[Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]
public class ConversoesController(IServicoConversoes servico) : ControllerBase
{
    /// <summary>A credencial (sem o token) e o número de leads com anúncio dos últimos 30 dias.</summary>
    [HttpGet]
    public async Task<IActionResult> Obter(CancellationToken ct) =>
        Ok(await servico.ObterAsync(ct));

    /// <summary>Cria ou atualiza. Token em branco MANTÉM o que já estava lá.</summary>
    [HttpPut]
    public async Task<IActionResult> Salvar([FromBody] SalvarCredencial dados, CancellationToken ct)
    {
        await servico.SalvarAsync(dados, ct);
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Remover(CancellationToken ct)
    {
        await servico.RemoverAsync(ct);
        return NoContent();
    }
}
