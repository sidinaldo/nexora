using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Nexora.Api.Seguranca;
using Nexora.Core.Seguranca;

namespace Nexora.Tests.Unidade;

/// <summary>QUEM UMA ROTA ACEITA, do jeito que o ASP.NET decide — para os testes perguntarem "isto é
/// só do dono?" sem saber COMO a rota escreve a regra.
///
/// ⚠️ OS TESTES LIAM `atributo.Roles == "dono"`, e quebraram todos quando as rotas passaram a
/// dizer o gesto (`Policy = nameof(Permissao.ConfigurarEmpresa)`) — sem que ninguém tivesse
/// ganhado ou perdido acesso. Eles testavam a FORMA da regra. Este auxiliar resolve a política pela
/// MESMA tabela que o `Program.cs` registra, e devolve os papéis.
///
/// ⚠️ E AQUI ESTÁ O LIMITE DELE, QUE MUDOU. Antes os papéis vinham do `RolesAuthorizationRequirement`
/// — a regra que o ASP.NET de fato avaliaria. Agora vêm de `Permissoes.PapeisCom` a partir do gesto
/// que a política nomeia, que é a tabela BASE. Então este auxiliar afirma "a rota nomeia o gesto
/// certo", e NÃO MAIS "a rota está fechada": um `ExigeRequisito` cujo handler dissesse `Succeed()`
/// sempre continuaria reportando `dono` para as 90 entradas de `RotasPorPermissaoTests`.
///
/// Esse buraco é tampado por `PermissoesTests.A_POLITICA_REGISTRADA_DECIDE_PELO_PAPEL_DO_TOKEN`,
/// que avalia a política com o `IAuthorizationService` de verdade. Foi o preço aceito para as 90
/// entradas não mudarem uma linha numa troca que não mexeu no acesso de ninguém.
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

    /// <summary>O GESTO que a rota nomeia — a pergunta que `Calcular` NÃO responde.
    ///
    /// ⚠️ EXISTE PORQUE A PARTIÇÃO DE `ConfigurarEmpresa` SERIA INVISÍVEL SEM ELA. Os cinco gestos
    /// novos nasceram `[Dono]`, então `Calcular` devolve `dono` tanto para a rota repontada quanto
    /// para a esquecida — e um controller deixado para trás não é erro de acesso, é uma área que
    /// simplesmente não se delega. Isso só apareceria quando o cliente marcasse o interruptor e
    /// nada acontecesse.
    ///
    /// Devolve os nomes de API em ordem, ou `SEM-GESTO` quando a rota não nomeia política nenhuma.</summary>
    public static string Gesto(IEnumerable<object> atributos)
    {
        var gestos = atributos.OfType<AuthorizeAttribute>()
            .Where(a => a.Policy is not null)
            .Select(a => Opcoes.GetPolicy(a.Policy!)
                ?? throw new InvalidOperationException($"política '{a.Policy}' não registrada"))
            .SelectMany(p => p.Requirements.OfType<ExigeRequisito>())
            .Select(r => Permissoes.NaApi(r.Permissao))
            .Distinct().Order().ToList();

        return gestos.Count == 0 ? "SEM-GESTO" : string.Join(",", gestos);
    }

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
                deste = politica.Requirements.OfType<ExigeRequisito>()
                    .SelectMany(r => Permissoes.PapeisCom(r.Permissao));
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
