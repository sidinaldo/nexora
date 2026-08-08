using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Os contatos. Qualquer papel: cadastrar e trabalhar lead é atendimento, não
/// configuração — o vendedor precisa disso o dia inteiro.</summary>
[ApiController]
[Route("api/contatos")]
[Authorize]
public class ContatosController(IServicoContatos servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] FiltroContato filtro = FiltroContato.Abertos,
        [FromQuery] string? busca = null,
        [FromQuery] long? etapaId = null,
        [FromQuery] long? responsavelId = null,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanho = 30,
        CancellationToken ct = default) =>
        Ok(await servico.ListarAsync(filtro, busca, etapaId, responsavelId, pagina, tamanho, ct));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detalhe(long id, CancellationToken ct) =>
        Ok(await servico.DetalheAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] NovoContato novo, CancellationToken ct) =>
        Ok(new { id = await servico.CriarAsync(novo, ct) });

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Atualizar(
        long id, [FromBody] EditarContato dados, CancellationToken ct)
    {
        await servico.AtualizarAsync(id, dados, ct);
        return NoContent();
    }

    /// <summary>A PORTA ÚNICA DO GANHO. Arrastar o card para a coluna de venda e clicar em "venda
    /// fechada" chamam este mesmo endpoint — `POST /api/funil/{id}/mover` recusa a etapa de ganho
    /// justamente para forçar por aqui, onde o valor é obrigatório.</summary>
    [HttpPost("{id:long}/ganho")]
    public async Task<IActionResult> MarcarGanho(
        long id, [FromBody] RegistrarGanho corpo, CancellationToken ct)
    {
        await servico.MarcarGanhoAsync(id, corpo.Valor, corpo.CanalId, ct);
        return NoContent();
    }

    /// <summary>O que o modal de fechamento precisa para oferecer o canal (NEG-3). Endpoint
    /// próprio e não um campo no detalhe do contato: o funil abre o mesmo modal a partir de um
    /// card, que não carrega o detalhe — e engordar o payload do kanban por causa de um campo
    /// que só aparece num modal seria pagar em toda rolagem do quadro.</summary>
    [HttpGet("{id:long}/canais-fechamento")]
    public async Task<IActionResult> CanaisDoFechamento(long id, CancellationToken ct) =>
        Ok(await servico.CanaisDoFechamentoAsync(id, ct));

    [HttpPost("{id:long}/perda")]
    public async Task<IActionResult> MarcarPerdido(
        long id, [FromBody] RegistrarPerda corpo, CancellationToken ct)
    {
        await servico.MarcarPerdidoAsync(id, corpo.Motivo, ct);
        return NoContent();
    }

    [HttpPost("{id:long}/reabrir")]
    public async Task<IActionResult> Reabrir(long id, CancellationToken ct)
    {
        await servico.ReabrirAsync(id, ct);
        return NoContent();
    }

    /// <summary>LGPD. Só DONO e GESTOR: apagar a PII é irreversível, e o histórico do contato
    /// deixa de ter nome para sempre.</summary>
    [HttpPost("{id:long}/anonimizar")]
    [Authorize(Roles = "dono,gestor")]
    public async Task<IActionResult> Anonimizar(long id, CancellationToken ct)
    {
        await servico.AnonimizarAsync(id, ct);
        return NoContent();
    }
}

/// <summary>`CanalId` é OPCIONAL e omiti-lo é o caso normal (NEG-3): sem ele a venda herda o canal
/// do ciclo detectado nas mensagens. Informar serve para o vendedor confirmar ou corrigir — é o
/// único ponto onde alguém sabe de verdade por que o cliente voltou.</summary>
public record RegistrarGanho(decimal Valor, long? CanalId = null);
public record RegistrarPerda(string Motivo);
