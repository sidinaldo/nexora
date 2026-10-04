using Microsoft.AspNetCore.Authorization;
using Nexora.Core.Seguranca;

namespace Nexora.Api.Seguranca;

/// <summary>A rota exige UM GESTO; quem pode o gesto sai da tabela de `Permissoes`.
///
/// ⚠️ É `AuthorizationHandler&lt;ExigeRequisito&gt;` ALÉM DE `IAuthorizationRequirement`, e isso não
/// é estilo: é um requisito SELF-HANDLING, na mesma forma do `RolesAuthorizationRequirement` que
/// ele substituiu. O motivo é concreto —
/// `PermissoesTests.A_POLITICA_REGISTRADA_DECIDE_PELO_PAPEL_DO_TOKEN` monta o container com
/// `.AddAuthorization(PoliticasDePermissao.Registrar)` e NÃO registra handler nenhum (funciona
/// porque o `AddAuthorizationCore` traz o `PassThroughAuthorizationHandler`, que invoca o próprio
/// requisito). Com um handler em classe separada, os quatro casos daquele teste falhariam por falta
/// de handler — e a mensagem de erro não diria isso.
///
/// ⚠️ SUBSTITUIU O `RequireRole`, E NINGUÉM GANHOU NEM PERDEU ACESSO. Antes a política comparava
/// PAPÉIS; agora ela nomeia o GESTO, e `PapeisDaRota` resolve os papéis pela mesma tabela — as 90
/// entradas de `RotasPorPermissaoTests` não mudaram uma linha. O que a troca abre é o caminho para
/// a permissão por pessoa: essa decisão não cabe num `RequireRole`.
///
/// ⚠️ ANÔNIMO CONTINUA FORA, sem precisar de `RequireAuthenticatedUser`: sem claim de papel,
/// `PapelDe` devolve `null`, e `Permissoes.Pode(null, _)` é `false`. É exatamente o que o
/// `RolesAuthorizationRequirement` fazia — ele também só perguntava pelo papel.</summary>
public class ExigeRequisito(Permissao permissao)
    : AuthorizationHandler<ExigeRequisito>, IAuthorizationRequirement
{
    /// <summary>O gesto que esta política exige.
    ///
    /// Público porque `PapeisDaRota` o lê para responder, nos testes, QUEM entra nesta rota: a
    /// política guarda o gesto, e os papéis saem de `Permissoes.PapeisCom`. É o que mantém a
    /// afirmação dos testes sobre quem entra, e não sobre a forma da regra.</summary>
    public Permissao Permissao { get; } = permissao;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext contexto, ExigeRequisito requisito)
    {
        if (Permissoes.Pode(ClaimsDoToken.PapelDe(contexto.User), requisito.Permissao,
                            ClaimsDoToken.ExcecoesDe(contexto.User)))
            contexto.Succeed(requisito);

        return Task.CompletedTask;
    }
}
