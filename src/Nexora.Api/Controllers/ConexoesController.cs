using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>As conexoes de WhatsApp da empresa. So o DONO: parear numero e tarefa de
/// configuracao, nao de atendimento.
///
/// ===================== A ROTA MUDOU NO ARQ-2 =====================
/// Era `/api/conexao` no singular, sem id, porque havia uma conexao por empresa. Com multi-numero
/// a tela virou uma LISTA, e todo verbo que age sobre UMA conexao passa a exigir o id dela.
///
/// O plural `/api/conexoes` e a colecao; `/api/conexoes/{id}/...` sao os verbos. Manter o singular
/// e enfiar o id no corpo economizaria a mudanca no frontend e custaria a coisa que mais importa
/// numa API: a URL dizer sobre o que ela age.
/// =================================================================</summary>
[ApiController]
[Route("api/conexoes")]
[Authorize(Roles = "dono")]
public class ConexoesController(IServicoConexoes servico) : ControllerBase
{
    /// <summary>A lista + o limite do plano. O `podeAdicionar` vem do servidor porque o limite
    /// muda por contrato — a tela que o adivinhasse divergiria no dia da mudanca.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) =>
        Ok(await servico.ListarAsync(ct));

    /// <summary>Uma conexao. 404 tanto para id inexistente quanto para id de outra empresa — o
    /// corpo e o MESMO nos dois casos, senao a diferenca entre as respostas viraria um oraculo
    /// para descobrir quais ids existem em outros tenants.</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Obter(long id, CancellationToken ct) =>
        await servico.ObterAsync(id, ct) is { } c ? Ok(c) : NotFound(new { erro = "Conexão não encontrada." });

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] NovaConexao nova, CancellationToken ct)
    {
        var id = await servico.CriarAsync(nova, ct);
        return CreatedAtAction(nameof(Obter), new { id }, new { id });
    }

    /// <summary>Renomeia. So o NOME: `instance_name` e a identidade na Evolution e nao tem
    /// endpoint de edicao em lugar nenhum, de proposito.</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Renomear(long id, [FromBody] RenomearRequest req, CancellationToken ct)
    {
        await servico.RenomearAsync(id, req.Nome, ct);
        return NoContent();
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        await servico.RemoverAsync(id, ct);
        return NoContent();
    }

    /// <summary>Estado ao vivo na Evolution. A tela chama em polling de 3s enquanto o QR
    /// esta na frente do usuario.</summary>
    [HttpGet("{id:long}/status")]
    public async Task<IActionResult> Status(long id, CancellationToken ct) =>
        Ok(await servico.StatusAsync(id, ct));

    /// <summary>Cria a instancia (se preciso) e devolve o QR para escanear.</summary>
    [HttpPost("{id:long}/conectar")]
    public async Task<IActionResult> Conectar(long id, CancellationToken ct) =>
        Ok(await servico.ConectarAsync(id, ct));

    /// <summary>Pareamento por CODIGO: alternativa ao QR para quem esta no proprio celular.</summary>
    [HttpPost("{id:long}/parear")]
    public async Task<IActionResult> Parear(long id, [FromBody] PareamentoRequest req, CancellationToken ct) =>
        Ok(await servico.ParearAsync(id, req.Numero, ct));

    [HttpPost("{id:long}/desconectar")]
    public async Task<IActionResult> Desconectar(long id, CancellationToken ct)
    {
        await servico.DesconectarAsync(id, ct);
        return NoContent();
    }

    /// <summary>Quanto saiu hoje, quanto ainda espera e quanto foi perdido — NESTE numero.</summary>
    [HttpGet("{id:long}/saude")]
    public async Task<IActionResult> Saude(long id, CancellationToken ct) =>
        Ok(await servico.SaudeAsync(id, ct));

    /// <summary>A empresa viu o aviso de que conectou um numero diferente — limpa o alerta.</summary>
    [HttpPost("{id:long}/reconhecer-troca")]
    public async Task<IActionResult> ReconhecerTroca(long id, CancellationToken ct)
    {
        await servico.ReconhecerTrocaAsync(id, ct);
        return NoContent();
    }
}

public record PareamentoRequest(string Numero);
public record RenomearRequest(string Nome);
