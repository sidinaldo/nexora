using System.Security.Claims;
using Nexora.Core;

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

    // O papel viaja no token como claim de role (ClaimTypes.Role) — o mesmo que faz o
    // as politicas de `Permissoes` funcionarem. Aqui expomos para as regras de servico.
    public string? Papel => Usuario?.FindFirstValue(ClaimTypes.Role);

    public bool EstaAutenticado => EmpresaId != 0;
}
