using Microsoft.AspNetCore.Authorization;
using Nexora.Core.Seguranca;

namespace Nexora.Api.Seguranca;

/// <summary>Uma política por `Permissao`, com os papéis da tabela de `Permissoes`.
///
/// ⚠️ É O QUE LIGA AS ROTAS À TABELA. Antes cada controller escrevia `Roles = "dono"` à mão, e a
/// tela tinha a sua própria versão de "quem é dono". Agora a rota diz O GESTO —
/// `[Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]` — e quem pode o gesto sai daqui.
///
/// Método próprio, e não um bloco no `Program.cs`: o teste de rotas monta as MESMAS políticas para
/// conferir que nenhuma rota mudou de papel na troca.</summary>
public static class PoliticasDePermissao
{
    public static void Registrar(AuthorizationOptions opcoes)
    {
        foreach (var permissao in Enum.GetValues<Permissao>())
            opcoes.AddPolicy(permissao.ToString(), p => p.RequireRole(Permissoes.PapeisCom(permissao)));
    }
}
