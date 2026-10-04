using Microsoft.AspNetCore.Authorization;
using Nexora.Core.Seguranca;

namespace Nexora.Api.Seguranca;

/// <summary>Uma política por `Permissao`, cada uma exigindo o SEU GESTO.
///
/// ⚠️ É O QUE LIGA AS ROTAS À TABELA. Antes cada controller escrevia `Roles = "dono"` à mão, e a
/// tela tinha a sua própria versão de "quem é dono". Agora a rota diz O GESTO —
/// `[Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]` — e quem pode o gesto sai daqui.
///
/// Método próprio, e não um bloco no `Program.cs`: o teste de rotas monta as MESMAS políticas para
/// conferir que nenhuma rota mudou de papel na troca.
///
/// ⚠️ ERA `RequireRole(Permissoes.PapeisCom(permissao))`, e virou um requisito que guarda o GESTO
/// (<see cref="ExigeRequisito"/>). A diferença não é de estilo: `RequireRole` só sabe comparar
/// papéis, e a permissão por pessoa não cabe nisso. A troca não mexeu no acesso de ninguém — os
/// papéis continuam saindo da mesma tabela, só que um passo depois.</summary>
public static class PoliticasDePermissao
{
    public static void Registrar(AuthorizationOptions opcoes)
    {
        foreach (var permissao in Enum.GetValues<Permissao>())
            opcoes.AddPolicy(permissao.ToString(), p => p.AddRequirements(new ExigeRequisito(permissao)));
    }
}
