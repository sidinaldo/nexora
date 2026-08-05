using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A equipe da empresa. Só o DONO — convidar e mudar papel é configuração, não
/// atendimento. O ACEITE do convite é público (ConviteController).</summary>
[ApiController]
[Route("api/equipe")]
[Authorize(Roles = "dono")]
public class EquipeController(IServicoEquipe servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) =>
        Ok(await servico.ListarAsync(ct));

    /// <summary>Convida e devolve o TOKEN para o dono montar o link e mandar por fora.
    /// Não há envio de e-mail na fase 1 — limitação registrada desde o bloco 1.</summary>
    [HttpPost("convites")]
    public async Task<IActionResult> Convidar([FromBody] NovoConvite novo, CancellationToken ct) =>
        Ok(await servico.ConvidarAsync(novo, ct));

    [HttpPost("{id:long}/reenviar-convite")]
    public async Task<IActionResult> Reenviar(long id, CancellationToken ct) =>
        Ok(await servico.ReenviarConviteAsync(id, ct));

    [HttpPost("{id:long}/reset-senha")]
    public async Task<IActionResult> ResetSenha(long id, CancellationToken ct) =>
        Ok(await servico.GerarResetSenhaAsync(id, ct));

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Atualizar(long id, [FromBody] EditarUsuario dados, CancellationToken ct)
    {
        await servico.AtualizarAsync(id, dados, ct);
        return NoContent();
    }
}

/// <summary>A própria conta. [Authorize] simples: qualquer usuário mexe na SUA conta, e só na
/// sua — o serviço usa o id do contexto, ninguém passa id de outro.</summary>
[ApiController]
[Route("api/conta")]
[Authorize]
public class ContaController(IServicoEquipe servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Minha(CancellationToken ct) =>
        Ok(await servico.MinhaContaAsync(ct));

    /// <summary>Não recebe id: o alvo é sempre o usuário do contexto. É o que permite esta rota
    /// ser [Authorize] simples — não há como um vendedor editar a conta de outro.</summary>
    [HttpPut]
    public async Task<IActionResult> Atualizar([FromBody] EditarMinhaConta dados, CancellationToken ct)
    {
        await servico.AtualizarMinhaContaAsync(dados, ct);
        return NoContent();
    }

    [HttpPost("senha")]
    public async Task<IActionResult> TrocarSenha([FromBody] TrocarSenhaRequest req, CancellationToken ct)
    {
        await servico.TrocarMinhaSenhaAsync(req.SenhaAtual, req.SenhaNova, ct);
        return NoContent();
    }
}

public record TrocarSenhaRequest(string SenhaAtual, string SenhaNova);

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

public record DefinirSenhaRequest(string Senha);
public record SolicitarResetRequest(string Email);
