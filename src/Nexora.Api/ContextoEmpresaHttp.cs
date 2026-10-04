using System.Security.Claims;
using Nexora.Api.Seguranca;
using Nexora.Core;
using Nexora.Core.Seguranca;

namespace Nexora.Api;

/// <summary>Le empresa e usuario dos claims do JWT. Fora de uma requisicao autenticada
/// (job de fundo, webhook, login) devolve 0 — e nesses pontos que o codigo PRECISA usar
/// .IgnoreQueryFilters() mais um filtro explicito por empresaId, senao as consultas
/// voltam vazias em silencio. Vale para todos os blocos seguintes.</summary>
public class ContextoEmpresaHttp(IHttpContextAccessor acessor, ContextoDeFundo fundo) : IContextoEmpresa
{
    public const string ClaimEmpresa = "empresa_id";

    private ClaimsPrincipal? Usuario => acessor.HttpContext?.User;

    // empresa_id nao esta no mapa de claims do JwtBearer, entao chega intacto.
    //
    // ⚠️ SEM CLAIM, CAI PARA O `ContextoDeFundo` — que e 0 tambem, a nao ser que um JOB tenha
    // assumido uma empresa nesta rodada. E o que permite o processamento da importacao em segundo
    // plano rodar O MESMO codigo do botao, com o query filter protegendo do mesmo jeito. Ver
    // `ContextoDeFundo` para por que nao e `IgnoreQueryFilters` como nos outros jobs.
    public long EmpresaId =>
        long.TryParse(Usuario?.FindFirstValue(ClaimEmpresa), out var id) && id != 0
            ? id
            : fundo.EmpresaId;

    // ARMADILHA: o JwtBearer, por padrao (MapInboundClaims=true), remapeia "sub" para
    // ClaimTypes.NameIdentifier. Por isso lemos os DOIS — senao o id do usuario chega
    // nulo em silencio. No Recupera esse foi o bug que deixava a coluna de autoria
    // ("quem registrou") sempre NULL, e ninguem percebeu ate os relatorios saírem vazios.
    public long UsuarioId =>
        long.TryParse(
            Usuario?.FindFirstValue("sub") ?? Usuario?.FindFirstValue(ClaimTypes.NameIdentifier),
            out var id) && id != 0 ? id : fundo.UsuarioId;

    // O papel viaja no token como claim de role (ClaimTypes.Role) — o mesmo que faz as politicas
    // de `Permissoes` funcionarem. Aqui expomos para as regras de servico.
    //
    // ⚠️ VIA `ClaimsDoToken.PapelDe`, E NAO COM UM `FindFirstValue` PROPRIO. O `ExigeRequisito`
    // (as rotas) le o papel pela MESMA funcao: se as duas camadas lessem o claim cada uma do seu
    // jeito, poderiam discordar — e e justamente a divergencia de camadas que `Permissoes`
    // registra como ja tendo custado um bug.
    public string? Papel => ClaimsDoToken.PapelDe(Usuario);

    // O que o dono ligou ou desligou para ESTA pessoa — do token, sem ida ao banco. `null` fora de
    // requisicao autenticada e para quem nao tem excecao nenhuma, que e quase todo mundo.
    public IReadOnlyDictionary<Permissao, bool>? ExcecoesDePermissao =>
        ClaimsDoToken.ExcecoesDe(Usuario);

    public bool EstaAutenticado => EmpresaId != 0;
}
