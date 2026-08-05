using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Aceite de convite — fluxo PÚBLICO: o convidado ainda não tem senha nem sessão.</summary>
[ApiController]
[Route("api/convite")]
[AllowAnonymous]
public class ConviteController(IServicoEquipe servico, GeradorToken gerador) : ControllerBase
{
    /// <summary>Dados do convite para a página de aceite. 404 se inválido ou expirado.</summary>
    [HttpGet("{token}")]
    public async Task<IActionResult> Info(string token, CancellationToken ct) =>
        await servico.ConviteInfoAsync(token, ct) is { } info ? Ok(info) : NotFound();

    /// <summary>Define a senha e devolve o JWT — a pessoa já entra logada.</summary>
    [HttpPost("{token}")]
    [EnableRateLimiting(RateLimitingConfig.PolSenha)]
    public async Task<IActionResult> Aceitar(string token, [FromBody] DefinirSenhaRequest req, CancellationToken ct)
    {
        var usuario = await servico.AceitarConviteAsync(token, req.Senha, ct);
        if (usuario is null) return NotFound(new { erro = "Convite inválido ou expirado." });

        var (jwt, expiraEm) = gerador.Gerar(usuario);
        return Ok(new { token = jwt, expiraEm, usuario });
    }
}

/// <summary>Compartilhado com a redefinição por link: os dois fluxos terminam do mesmo jeito —
/// a pessoa escolhe uma senha e passa a ter acesso.</summary>
public record DefinirSenhaRequest(string Senha);
