using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Controllers;

namespace Nexora.Tests.Unidade;

/// <summary>===================== ROTA ANÔNIMA SEM TETO É ROTA SEM TETO =====================
///
/// O limitador global deste sistema particiona por USUÁRIO, e devolve `NoLimiter("anon")` para quem
/// não está autenticado — a decisão está escrita em `RateLimitingConfig`, e é deliberada: as rotas
/// sensíveis têm política nomeada.
///
/// A consequência é o que este arquivo trava: **uma ação anônima que esqueça o
/// `[EnableRateLimiting]` não tem limite nenhum.** Não um limite frouxo — nenhum. E não há sintoma:
/// a rota funciona, os testes passam, e a falta só aparece quando alguém a encontra.
///
/// Esta varredura nasceu no OPE-1, com a área do operador, e na primeira execução achou DOIS buracos
/// que já existiam: os GET de consulta de convite e de redefinição. Nenhum dos dois era brecha de
/// autenticação — os tokens têm 32 bytes de CSPRNG —, mas os dois eram rota anônima que ninguém
/// podia parar de chamar.
/// ======================================================================================</summary>
public class RotasAnonimasTests
{
    /// <summary>Todos os controllers da API, lidos do assembly — não de uma lista escrita à mão, que
    /// é o tipo de lista que fica para trás no primeiro controller novo.</summary>
    private static IEnumerable<Type> Controllers() =>
        typeof(CadastroController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

    private static IEnumerable<MethodInfo> AcoesDe(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    /// <summary>A ação é alcançável sem sessão? É quando nem ela nem a classe exigem `[Authorize]`,
    /// ou quando a própria ação declara `[AllowAnonymous]`.</summary>
    private static bool EhAnonima(Type controller, MethodInfo acao)
    {
        if (acao.GetCustomAttribute<AllowAnonymousAttribute>() is not null) return true;
        if (acao.GetCustomAttribute<AuthorizeAttribute>() is not null) return false;

        if (controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null) return true;
        return controller.GetCustomAttribute<AuthorizeAttribute>() is null;
    }

    [Fact]
    public void TODA_ACAO_ANONIMA_TEM_POLITICA_DE_RATE_LIMIT()
    {
        var descobertas = new List<string>();

        foreach (var controller in Controllers())
        {
            foreach (var acao in AcoesDe(controller))
            {
                if (!EhAnonima(controller, acao)) continue;
                if (acao.GetCustomAttribute<EnableRateLimitingAttribute>() is not null) continue;

                descobertas.Add($"{controller.Name}.{acao.Name}");
            }
        }

        Assert.True(descobertas.Count == 0,
            "Ação anônima SEM `[EnableRateLimiting]` — o limitador global não cobre anônimo, "
            + "então estas rotas não têm teto nenhum:\n  " + string.Join("\n  ", descobertas));
    }

    [Fact]
    public void A_AREA_DO_OPERADOR_E_INTEIRAMENTE_ANONIMA_E_INTEIRAMENTE_LIMITADA()
    {
        // ===================== POR QUE ESTE SEGUNDO TESTE =====================
        // O de cima vale para todo mundo e continuaria verde se alguém pusesse `[Authorize]` no
        // `OperadorController` — mas isso quebraria a área de um jeito silencioso e ao contrário:
        // um JWT de CLIENTE passaria a ser NECESSÁRIO para alcançar a área do operador, que não tem
        // e nunca terá um.
        // ======================================================================
        var acoes = AcoesDe(typeof(OperadorController)).ToList();

        Assert.NotEmpty(acoes);
        foreach (var acao in acoes)
        {
            Assert.True(EhAnonima(typeof(OperadorController), acao),
                $"{acao.Name}: a área do operador não autentica por JWT — ver o cabeçalho do controller.");
            Assert.True(acao.GetCustomAttribute<EnableRateLimitingAttribute>() is not null,
                $"{acao.Name}: ação anônima sem teto.");
        }
    }
}
