using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Seguranca;
using Nexora.Core;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A área do operador (OPE-1): os números por cliente, o catálogo de planos e os tetos.
///
/// ===================== `[AllowAnonymous]` É O CERTO AQUI, E É CONTRAINTUITIVO =====================
/// Em todo o resto do sistema o JWT É a credencial. Aqui ele é IRRELEVANTE: quem autoriza é a chave
/// de administração no cabeçalho, a mesma do cadastro de empresa.
///
/// Pôr `[Authorize]` tornaria um JWT de cliente NECESSÁRIO para alcançar a área do operador — que é
/// o contrário do que se quer. Com `[AllowAnonymous]`, um vendedor com token perfeito leva 401
/// igual a qualquer um, porque o portão nunca olha para `User`.
/// ==================================================================================================
///
/// ⚠️ TODA AÇÃO PRECISA DE `[EnableRateLimiting]` EXPLÍCITO. O limitador global deste sistema
/// devolve `NoLimiter("anon")` para quem não está autenticado — então rota anônima SEM política
/// nomeada não tem teto nenhum. Esquecer o atributo numa ação deixa uma leitura de TODAS as
/// empresas destravada atrás de um segredo estático. Há teste varrendo este controller.
///
/// ⚠️ E O CLOUDFLARE ACCESS VAI NA FRENTE DISTO, não no lugar disto. Ele dá identidade e permite
/// tirar o acesso de uma pessoa sem trocar o segredo das outras; a chave continua sendo a
/// credencial. Ver `IdentidadeDoOperador`.</summary>
[ApiController]
[Route("api/operador")]
[AllowAnonymous]
public class OperadorController(
    IServicoOperador servico,
    OpcoesCadastro opcoes,
    IdentidadeDoOperador identidade,
    ILogger<OperadorController> log) : ControllerBase
{
    // ==================================================================== os números

    [HttpGet("empresas")]
    [EnableRateLimiting(RateLimitingConfig.PolOperador)]
    public async Task<IActionResult> Empresas(
        CancellationToken ct,
        [FromQuery] string? busca = null, [FromQuery] int pagina = 1,
        [FromQuery] int tamanho = 25, [FromQuery] int dias = 30)
    {
        if (!Autorizado()) return Recusa();
        return Ok(await servico.ListarEmpresasAsync(
            new FiltroEmpresas(busca, pagina, tamanho, dias), ct));
    }

    [HttpGet("empresas/{id:long}")]
    [EnableRateLimiting(RateLimitingConfig.PolOperador)]
    public async Task<IActionResult> Empresa(long id, CancellationToken ct)
    {
        if (!Autorizado()) return Recusa();
        return Ok(await servico.LimitesAsync(id, ct));
    }

    // ==================================================================== o catálogo

    [HttpGet("planos")]
    [EnableRateLimiting(RateLimitingConfig.PolOperador)]
    public async Task<IActionResult> Planos(CancellationToken ct)
    {
        if (!Autorizado()) return Recusa();
        return Ok(await servico.ListarPlanosAsync(ct));
    }

    [HttpPost("planos")]
    [EnableRateLimiting(RateLimitingConfig.PolOperadorEscrita)]
    public async Task<IActionResult> CriarPlano([FromBody] NovoPlano novo, CancellationToken ct)
    {
        if (!Autorizado()) return Recusa();
        var id = await servico.CriarPlanoAsync(novo, ct);
        log.LogInformation("Plano {Id} criado pela área do operador.", id);
        return Ok(new { planoId = id });
    }

    [HttpPut("planos/{id:long}")]
    [EnableRateLimiting(RateLimitingConfig.PolOperadorEscrita)]
    public async Task<IActionResult> AtualizarPlano(
        long id, [FromBody] EditarPlano dados, CancellationToken ct)
    {
        if (!Autorizado()) return Recusa();
        await servico.AtualizarPlanoAsync(id, dados, ct);
        return NoContent();
    }

    // ==================================================================== a empresa

    [HttpPut("empresas/{id:long}/plano")]
    [EnableRateLimiting(RateLimitingConfig.PolOperadorEscrita)]
    public async Task<IActionResult> AtribuirPlano(
        long id, [FromBody] AtribuicaoDePlano corpo, CancellationToken ct)
    {
        if (!Autorizado()) return Recusa();
        return Ok(await servico.AtribuirPlanoAsync(id, corpo.PlanoId, corpo.ConfirmarExcedente, ct));
    }

    [HttpPut("empresas/{id:long}/limites")]
    [EnableRateLimiting(RateLimitingConfig.PolOperadorEscrita)]
    public async Task<IActionResult> AjustarLimites(
        long id, [FromBody] AjusteDeLimites ajuste, CancellationToken ct)
    {
        if (!Autorizado()) return Recusa();
        return Ok(await servico.AjustarLimitesAsync(id, ajuste, ct));
    }

    /// <summary>⚠️ DESATIVAR BLOQUEIA LOGIN NOVO E NÃO DERRUBA QUEM JÁ ESTÁ DENTRO. O token dura 12h
    /// e hoje não é reconferido por requisição — quem recebeu o dele dez minutos antes continua
    /// lendo a caixa e mandando mensagem até ele vencer.
    ///
    /// A tela precisa dizer isso em palavras: um botão "desativar" que não desconecta ninguém é uma
    /// mentira que se descobre no meio de um incidente.</summary>
    [HttpPut("empresas/{id:long}/ativa")]
    [EnableRateLimiting(RateLimitingConfig.PolOperadorEscrita)]
    public async Task<IActionResult> DefinirAtiva(
        long id, [FromBody] DefinicaoDeAtividade corpo, CancellationToken ct)
    {
        if (!Autorizado()) return Recusa();
        log.LogWarning(
            "Empresa {Id} {Acao} pela área do operador.", id, corpo.Ativa ? "REATIVADA" : "DESATIVADA");
        return Ok(await servico.DefinirAtivaAsync(id, corpo.Ativa, ct));
    }

    // ==================================================================== o portão

    /// <summary>A chave, e — quando a borda disser — quem está do outro lado.
    ///
    /// A identidade é lida DEPOIS da chave de propósito: sem credencial válida não há por que
    /// guardar nada sobre quem bateu na porta.</summary>
    private bool Autorizado()
    {
        if (!ChaveAdmin.Confere(Request, opcoes.ChaveAdministracao))
        {
            log.LogWarning(
                "Acesso à área do operador recusado em {Rota}: chave inválida.", Request.Path);
            return false;
        }

        identidade.Email = IdentidadeDoOperador.Sanitizar(
            Request.Headers[IdentidadeDoOperador.CabecalhoAccess]);

        return true;
    }

    private IActionResult Recusa() => Unauthorized(ChaveAdmin.CorpoRecusa());
}

public record AtribuicaoDePlano(long PlanoId, bool ConfirmarExcedente = false);
public record DefinicaoDeAtividade(bool Ativa);
