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
[Authorize(Policy = nameof(Permissao.GerenciarAnuncios))]
public class ConversoesController(IServicoConversoes servico) : ControllerBase
{
    /// <summary>A credencial (sem o token) e o número de leads com anúncio dos últimos 30 dias.</summary>
    [HttpGet]
    public async Task<IActionResult> Obter(CancellationToken ct) =>
        Ok(await servico.ObterAsync(ct));

    /// <summary>Só as duas contas do aviso da Captação. Rota própria para não carregar as 50
    /// últimas conversões só para desenhar uma frase.</summary>
    [HttpGet("resumo")]
    public async Task<IActionResult> Resumo(CancellationToken ct) =>
        Ok(await servico.ResumoAsync(ct));

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

    /// <summary>Manda um evento de teste e ESPERA a resposta — o único endpoint deste bloco que
    /// entrega dentro da requisição. A pessoa está olhando o botão.</summary>
    [HttpPost("testar")]
    public async Task<IActionResult> Testar(CancellationToken ct) =>
        Ok(await servico.TestarAsync(ct));

    /// <summary>Devolve uma conversão falha para a fila. Não envia na hora.
    ///
    /// ⚠️ `id` AQUI É DE UM EVENTO. Em `vendas/{negociacaoId}/enviar`, logo abaixo, é de uma
    /// NEGOCIAÇÃO. Dois espaços de id na mesma posição da rota é como duas funcionalidades
    /// parecidas viram uma só por engano, seis meses depois — por isso a outra mora debaixo de
    /// `vendas/`, e a diferença fica visível na URL antes de ficar visível no código.</summary>
    [HttpPost("{id:long}/reenviar")]
    public async Task<IActionResult> Reenviar(long id, CancellationToken ct)
    {
        await servico.ReenviarAsync(id, ct);
        return NoContent();
    }

    /// <summary>INT-5 · põe na fila a conversão de uma VENDA que nunca virou evento.</summary>
    [HttpPost("vendas/{negociacaoId:long}/enviar")]
    public async Task<IActionResult> EnviarVenda(long negociacaoId, CancellationToken ct)
    {
        await servico.EnviarVendaAsync(negociacaoId, ct);
        return NoContent();
    }

    /// <summary>INT-5 · as que ainda cabem nos 7 dias, até o teto de uma rodada do motor.</summary>
    [HttpPost("vendas/enviar-pendentes")]
    public async Task<IActionResult> EnviarVendasPendentes(CancellationToken ct) =>
        Ok(await servico.EnviarVendasPendentesAsync(ct));
}
