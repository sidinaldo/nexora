using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Seguranca;
using Nexora.Core;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

public record LoginRequest(string Email, string Senha);
public record LoginResponse(string Token, DateTime ExpiraEm, UsuarioAutenticado Usuario);

[ApiController]
[Route("api/auth")]
public class AuthController(
    IServicoAutenticacao servico,
    GeradorToken gerador) : ControllerBase
{
    [HttpPost("login")]
    [EnableRateLimiting(RateLimitingConfig.PolLogin)]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var usuario = await servico.AutenticarAsync(req.Email, req.Senha, ct);

        // null = e-mail nao existe OU senha errada. Indistinguivel de proposito: responder
        // diferente permite descobrir quais e-mails estao cadastrados.
        if (usuario is null)
            return Unauthorized(new { erro = "E-mail ou senha invalidos." });

        var (token, expira) = gerador.Gerar(usuario);
        return Ok(new LoginResponse(token, expira, usuario));
    }

    /// <summary>O que a sessão ATUAL pode fazer — do papel que está no TOKEN, que é o mesmo que as
    /// rotas conferem.
    ///
    /// Existe para a sessão que já estava aberta quando `Permissoes` entrou no login: ela guardou o
    /// usuário sem a lista, e sem isto o dono ficaria sem o menu de configuração até sair e
    /// entrar de novo. O painel chama ao abrir.</summary>
    [HttpGet("permissoes")]
    [Authorize]
    public IReadOnlyList<string> Permissoes([FromServices] IContextoEmpresa contexto) =>
        Core.Seguranca.Permissoes.NaApiPara(contexto.Papel);
}
