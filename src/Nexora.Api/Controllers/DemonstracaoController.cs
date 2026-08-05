using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

public class OpcoesDemonstracao
{
    /// <summary>Liga o comando de seed. FALSO por padrão, e é a guarda que impede o seed de
    /// existir em produção:
    ///
    ///   dotnet user-secrets set "Demonstracao:Habilitado" "true" --project src/Nexora.Api
    ///
    /// Configuração explícita, e não `IsDevelopment()`: um ambiente de homologação legítimo pode
    /// querer o tenant de demonstração, e amarrar isso ao nome do ambiente obrigaria a mentir
    /// sobre qual ambiente é qual.</summary>
    public bool Habilitado { get; set; }
}

/// <summary>Semeia o tenant de DEMONSTRAÇÃO. Comando administrativo, não funcionalidade.
///
/// ===================== POR QUE NÃO É UM ENDPOINT ABERTO =====================
/// Este comando cria uma empresa e apaga o conteúdo dela se já existir. Duas travas
/// INDEPENDENTES, e as duas precisam passar:
///
///   1. `Demonstracao:Habilitado` — falso por padrão. Em produção ninguém liga, e sem isto a
///      rota devolve 404 (não 403: 403 confirmaria que a rota existe para quem sondasse);
///   2. a MESMA chave de administração do cadastro de empresa (PI-2), em header, comparada em
///      tempo constante.
///
/// Não usa `[Authorize]`: o comando roda antes de existir sessão em qualquer tenant, e é
/// operado por quem já tem a chave — a mesma pessoa que cria empresa.
/// ===========================================================================</summary>
[ApiController]
[Route("api/demonstracao")]
[AllowAnonymous]
public class DemonstracaoController(
    IServicoSeedDemonstracao seed,
    OpcoesDemonstracao opcoes,
    OpcoesCadastro opcoesCadastro,
    ILogger<DemonstracaoController> log) : ControllerBase
{
    /// <summary>Cria ou repovoa o tenant. Idempotente: rodar de novo repõe os mesmos dados com
    /// datas frescas, em vez de duplicar.
    ///
    /// `contatos` e `dias` são opcionais e vão na query string. Sem eles, o padrão de
    /// `OpcoesSeedDemonstracao` — que é o volume pensado para uma demonstração comercial, não para
    /// exercitar paginação. Valores fora da faixa são RECORTADOS, não recusados: quem pede 999999
    /// quer "muitos", e o resumo devolvido diz o que de fato foi criado.</summary>
    [HttpPost("semear")]
    [EnableRateLimiting(RateLimitingConfig.PolCadastro)]
    public async Task<IActionResult> Semear(
        CancellationToken ct, [FromQuery] int? contatos = null, [FromQuery] int? dias = null)
    {
        // GUARDA DE AMBIENTE PRIMEIRO: sem ela ligada, nem a chave certa abre a porta.
        if (!opcoes.Habilitado)
        {
            log.LogWarning("Seed de demonstração recusado: Demonstracao:Habilitado está desligado.");
            return NotFound();
        }

        if (!ChaveConfere())
        {
            log.LogWarning("Seed de demonstração recusado: chave de administração inválida.");
            return Unauthorized(new { erro = "Não autorizado." });
        }

        var padrao = new OpcoesSeedDemonstracao();
        var resumo = await seed.SemearAsync(
            new OpcoesSeedDemonstracao(contatos ?? padrao.Contatos, dias ?? padrao.Dias), ct);

        log.LogInformation("Tenant de demonstração {Id} semeado.", resumo.EmpresaId);

        return Ok(resumo);
    }

    /// <summary>A MESMA chave do cadastro de empresa, com a mesma disciplina: comparação em tempo
    /// constante e vazio = desligado.</summary>
    private bool ChaveConfere()
    {
        if (string.IsNullOrEmpty(opcoesCadastro.ChaveAdministracao)) return false;
        if (!Request.Headers.TryGetValue(CadastroController.CabecalhoChave, out var enviada)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(opcoesCadastro.ChaveAdministracao),
            Encoding.UTF8.GetBytes(enviada.ToString()));
    }
}
