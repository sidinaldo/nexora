using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

public class OpcoesCadastro
{
    /// <summary>Chave que autoriza criar empresa. Vem de user-secrets ou variável de ambiente,
    /// NUNCA do appsettings versionado:
    ///
    ///   dotnet user-secrets set "Cadastro:ChaveAdministracao" "..." --project src/Nexora.Api
    ///
    /// VAZIA = cadastro DESLIGADO. É o padrão, e é deliberado: um clone do repositório que
    /// alguém suba sem configurar nada não pode ficar com criação de conta aberta na internet.
    /// Mesma disciplina do segredo do webhook.</summary>
    public string ChaveAdministracao { get; set; } = "";
}

/// <summary>Criação de empresa. Rota PÚBLICA por natureza — não há sessão antes de a empresa
/// existir, e o dono é criado junto com ela.
///
/// ===================== POR QUE UMA CHAVE, E NÃO CADASTRO ABERTO =====================
/// Nesta fase o onboarding é manual: cliente entra por reunião, e quem cria a conta é a equipe.
/// Cadastro irrestrito na internet, sem verificação de e-mail nem aceite de termos, seria
/// convite para lixo — e cada tenant falso arrasta usuário, conexão e cinco etapas de funil.
///
/// A chave no header resolve isso com uma linha de configuração, sem prender o desenho: quando
/// o autoatendimento chegar, ele será OUTRO fluxo — com confirmação de e-mail, aceite de termos
/// e provavelmente pagamento —, e este endpoint continua servindo à equipe interna.
/// ====================================================================================</summary>
[ApiController]
[Route("api/cadastro")]
[AllowAnonymous]
public class CadastroController(
    IServicoCadastroEmpresa servico,
    OpcoesCadastro opcoes,
    ILogger<CadastroController> log) : ControllerBase
{
    public const string CabecalhoChave = "X-Chave-Admin";

    [HttpPost("empresa")]
    [EnableRateLimiting(RateLimitingConfig.PolCadastro)]
    public async Task<IActionResult> Criar([FromBody] NovaEmpresa nova, CancellationToken ct)
    {
        if (!ChaveConfere())
        {
            // 401 sem detalhe: a mensagem é a mesma para chave ausente e chave errada. Dizer
            // "chave ausente" confirmaria que o endpoint existe e o que ele espera.
            log.LogWarning("Cadastro de empresa recusado: chave de administração inválida.");
            return Unauthorized(new { erro = "Não autorizado." });
        }

        var id = await servico.CadastrarAsync(nova, ct);
        log.LogInformation("Empresa {Id} cadastrada.", id);

        return Ok(new { empresaId = id });
    }

    /// <summary>Comparação em TEMPO CONSTANTE. Diferente do webhook — onde o segredo vai na URL
    /// e já aparece em log de proxy —, esta chave viaja em header e não é registrada em lugar
    /// nenhum. Comparar com `==` vazaria o prefixo correto pelo tempo de resposta.</summary>
    private bool ChaveConfere()
    {
        if (string.IsNullOrEmpty(opcoes.ChaveAdministracao)) return false;
        if (!Request.Headers.TryGetValue(CabecalhoChave, out var enviada)) return false;

        var esperada = Encoding.UTF8.GetBytes(opcoes.ChaveAdministracao);
        var recebida = Encoding.UTF8.GetBytes(enviada.ToString());

        return CryptographicOperations.FixedTimeEquals(esperada, recebida);
    }
}
