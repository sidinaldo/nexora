using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A conexao de WhatsApp da empresa. So o DONO: parear numero e tarefa de
/// configuracao, nao de atendimento.
///
/// FASE 1: uma conexao por empresa, criada no cadastro. Nao ha CRUD — o Recupera tem 10
/// endpoints porque suporta N conexoes com uma padrao; aqui sobram os de pareamento e status.</summary>
[ApiController]
[Route("api/conexao")]
[Authorize(Roles = "dono")]
public class ConexaoController(IServicoConexoes servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Minha(CancellationToken ct) =>
        await servico.MinhaAsync(ct) is { } c ? Ok(c) : NotFound();

    /// <summary>Estado ao vivo na Evolution. A tela chama em polling de 3s enquanto o QR
    /// esta na frente do usuario.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct) =>
        Ok(await servico.StatusAsync(ct));

    /// <summary>Cria a instancia (se preciso) e devolve o QR para escanear.</summary>
    [HttpPost("conectar")]
    public async Task<IActionResult> Conectar(CancellationToken ct) =>
        Ok(await servico.ConectarAsync(ct));

    /// <summary>Pareamento por CODIGO: alternativa ao QR para quem esta no proprio celular.</summary>
    [HttpPost("parear")]
    public async Task<IActionResult> Parear([FromBody] PareamentoRequest req, CancellationToken ct) =>
        Ok(await servico.ParearAsync(req.Numero, ct));

    [HttpPost("desconectar")]
    public async Task<IActionResult> Desconectar(CancellationToken ct)
    {
        await servico.DesconectarAsync(ct);
        return NoContent();
    }

    /// <summary>Quanto saiu hoje, quanto ainda espera e quanto foi perdido.</summary>
    [HttpGet("saude")]
    public async Task<IActionResult> Saude(CancellationToken ct) =>
        Ok(await servico.SaudeAsync(ct));

    /// <summary>A empresa viu o aviso de que conectou um numero diferente — limpa o alerta.</summary>
    [HttpPost("reconhecer-troca")]
    public async Task<IActionResult> ReconhecerTroca(CancellationToken ct)
    {
        await servico.ReconhecerTrocaAsync(ct);
        return NoContent();
    }
}

public record PareamentoRequest(string Numero);
