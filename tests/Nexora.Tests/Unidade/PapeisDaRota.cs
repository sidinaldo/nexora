using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Nexora.Api.Seguranca;

namespace Nexora.Tests.Unidade;

/// <summary>QUEM UMA ROTA ACEITA, do jeito que o ASP.NET decide — para os testes perguntarem "isto é
/// só do dono?" sem saber COMO a rota escreve a regra.
///
/// ⚠️ OS TESTES LIAM `atributo.Roles == "dono"`, e quebraram todos quando as rotas passaram a
/// dizer o gesto (`Policy = nameof(Permissao.ConfigurarEmpresa)`) — sem que ninguém tivesse
/// ganhado ou perdido acesso. Eles testavam a FORMA da regra. Este auxiliar resolve a política pela
/// MESMA tabela que o `Program.cs` registra, e devolve os papéis: a afirmação volta a ser sobre
/// quem entra.
///
/// Vários `[Authorize]` (classe e método) valem JUNTOS, então os papéis se INTERSECTAM.
/// Devolve `dono`, `dono,gestor`, `autenticado`, `anonimo` ou `SEM-AUTHORIZE`.</summary>
public static class PapeisDaRota
{
    private static readonly AuthorizationOptions Opcoes = Montar();

    public static string De(Type controller, string metodo) =>
        Calcular(controller.GetCustomAttributes(true)
            .Concat(controller.GetMethod(metodo)!.GetCustomAttributes(true)));

    /// <summary>Só o que a CLASSE impõe — vale para toda ação dela.</summary>
    public static string DaClasse(Type controller) => Calcular(controller.GetCustomAttributes(true));

    internal static string Calcular(IEnumerable<object> atributos)
    {
        var attrs = atributos.ToList();
        if (attrs.OfType<AllowAnonymousAttribute>().Any()) return "anonimo";

        var auth = attrs.OfType<AuthorizeAttribute>().ToList();
        if (auth.Count == 0) return "SEM-AUTHORIZE";

        HashSet<string>? papeis = null;
        foreach (var a in auth)
        {
            IEnumerable<string>? deste = null;

            if (a.Roles is not null)
                deste = a.Roles.Split(',').Select(x => x.Trim());

            if (a.Policy is not null)
            {
                var politica = Opcoes.GetPolicy(a.Policy)
                    ?? throw new InvalidOperationException($"política '{a.Policy}' não registrada");
                deste = politica.Requirements.OfType<RolesAuthorizationRequirement>()
                    .SelectMany(r => r.AllowedRoles);
            }

            if (deste is null) continue;
            papeis = papeis is null ? deste.ToHashSet() : papeis.Intersect(deste).ToHashSet();
        }

        return papeis is null ? "autenticado" : string.Join(",", papeis.Order());
    }

    private static AuthorizationOptions Montar()
    {
        var o = new AuthorizationOptions();
        PoliticasDePermissao.Registrar(o);
        return o;
    }
}
