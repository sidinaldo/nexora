using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Redefinição de senha por LINK — fluxo PÚBLICO (a pessoa perdeu o acesso).</summary>
[ApiController]
[Route("api/redefinir")]
[AllowAnonymous]
public class RedefinicaoController(IServicoEquipe servico) : ControllerBase
{
    /// <summary>"Esqueci minha senha", auto-serviço.
    ///
    /// ===================== A RESPOSTA É SEMPRE A MESMA =====================
    /// 200 com o mesmo corpo, exista o e-mail ou não. Nunca 404, nunca mensagem diferente.
    ///
    /// Qualquer distinção transformaria este endpoint num VERIFICADOR DE CONTAS: bastaria testar
    /// endereços para descobrir quem é cliente do Nexora — e essa lista tem valor para quem
    /// monta phishing. É a mesma disciplina do login com HashDummy (PoliticaLogin, bloco 1).
    ///
    /// O texto é redigido para ser verdadeiro NOS DOIS CASOS: "se houver uma conta com esse
    /// e-mail". Não afirma que enviou.
    /// ======================================================================
    ///
    /// Rate limit por IP: o token ainda não existe, então a política do aceite (que particiona
    /// por IP+token) não serve aqui.</summary>
    [HttpPost("solicitar")]
    [EnableRateLimiting(RateLimitingConfig.PolRecuperacao)]
    public async Task<IActionResult> Solicitar(
        [FromBody] SolicitarResetRequest req, CancellationToken ct)
    {
        await servico.SolicitarResetSenhaAsync(req.Email, ct);

        return Ok(new
        {
            mensagem = "Se houver uma conta com esse e-mail, enviamos um link para redefinir a " +
                       "senha. Confira também a caixa de spam."
        });
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> Info(string token, CancellationToken ct) =>
        await servico.ResetInfoAsync(token, ct) is { } info ? Ok(info) : NotFound();

    [HttpPost("{token}")]
    [EnableRateLimiting(RateLimitingConfig.PolSenha)]
    public async Task<IActionResult> Redefinir(string token, [FromBody] DefinirSenhaRequest req, CancellationToken ct) =>
        await servico.RedefinirSenhaAsync(token, req.Senha, ct)
            ? Ok(new { ok = true })
            : NotFound(new { erro = "Link inválido ou expirado. Peça um novo." });
}

public record SolicitarResetRequest(string Email);
