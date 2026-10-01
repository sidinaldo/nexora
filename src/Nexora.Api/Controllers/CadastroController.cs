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
    [HttpPost("empresa")]
    [EnableRateLimiting(RateLimitingConfig.PolCadastro)]
    public async Task<IActionResult> Criar([FromBody] NovaEmpresa nova, CancellationToken ct)
    {
        // A guarda mora em `ChaveAdmin` e não aqui porque as rotas de operação são três, e
        // três cópias de uma comparação em tempo constante é como uma delas vira `==`.
        if (!ChaveAdmin.Confere(Request, opcoes.ChaveAdministracao))
        {
            log.LogWarning("Cadastro de empresa recusado: chave de administração inválida.");
            return Unauthorized(ChaveAdmin.CorpoRecusa());
        }

        var id = await servico.CadastrarAsync(nova, ct);
        log.LogInformation("Empresa {Id} cadastrada.", id);

        return Ok(new { empresaId = id });
    }
}
