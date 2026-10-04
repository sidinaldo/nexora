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

    /// <summary>O que a sessão ATUAL pode fazer — do papel E das exceções que estão no TOKEN, que
    /// é exatamente o que as rotas conferem.
    ///
    /// Existe para a sessão que já estava aberta quando `Permissoes` entrou no login: ela guardou o
    /// usuário sem a lista, e sem isto o dono ficaria sem o menu de configuração até sair e
    /// entrar de novo. O painel chama ao abrir.
    ///
    /// ⚠️ ZERO I/O, E CONTINUA ASSIM depois do PER-1: lê os claims e roda a tabela. A exceção por
    /// pessoa viaja no token justamente para esta rota (e toda a autorização) não cobrar consulta.
    ///
    /// ⚠️ ESTA LISTA E A DECISÃO DA ROTA SAEM DO MESMO CÁLCULO, e é `PermissoesTests` quem exige
    /// isso célula por célula. As duas já divergiram uma vez — a importação aceitava o gestor no
    /// servidor e a tela escondia o botão dele.</summary>
    [HttpGet("permissoes")]
    [Authorize]
    public IReadOnlyList<string> Permissoes([FromServices] IContextoEmpresa contexto) =>
        Core.Seguranca.Permissoes.NaApiPara(contexto.Papel, contexto.ExcecoesDePermissao);
}
