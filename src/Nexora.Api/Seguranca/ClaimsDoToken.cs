using System.Security.Claims;
using Nexora.Core.Seguranca;

namespace Nexora.Api.Seguranca;

/// <summary>===================== O PAPEL SE LÊ NUM LUGAR SÓ =====================
///
/// Duas camadas decidem autorização a partir do papel do token: o <see cref="ExigeRequisito"/>
/// (as rotas) e o `ContextoEmpresaHttp.Papel` (os serviços). Elas PRECISAM ler o mesmo claim do
/// mesmo jeito.
///
/// ⚠️ ESTA CLASSE EXISTE PARA IMPEDIR UMA DIVERGÊNCIA QUE JÁ CUSTOU UM BUG. `Permissoes` registra
/// o caso no seu próprio comentário: a mesma regra escrita em três camadas, e elas discordaram —
/// a importação aceitava o gestor no servidor e a tela, escrita com `ehDono`, escondia o botão
/// dele.
///
/// Se uma camada usasse `IsInRole` e a outra `FindFirstValue(ClaimTypes.Role)`, a discordância
/// voltaria por duas portas: `IsInRole` compara EXATO (e o papel do token é minúsculo), e o
/// `RoleClaimType` é configurável por identidade — duas leituras, duas respostas possíveis para
/// a mesma pergunta.
///
/// Uma função, dois chamadores, nenhuma chance de divergir.
/// ====================================================================</summary>
public static class ClaimsDoToken
{
    /// <summary>O papel como o token o carrega — `dono`, `gestor`, `vendedor` —, ou `null` fora de
    /// uma requisição autenticada (job de fundo, webhook, login).
    ///
    /// ⚠️ `ClaimTypes.Role`, e não `"role"`: o JwtBearer remapeia os claims na ENTRADA
    /// (`MapInboundClaims`), então o nome curto que o `GeradorToken` escreveu chega aqui já
    /// expandido. É a mesma armadilha que o `ContextoEmpresaHttp` documenta para o `sub` — e lá
    /// ela deixou a coluna de autoria sempre NULL até os relatórios saírem vazios.</summary>
    public static string? PapelDe(ClaimsPrincipal? usuario) =>
        usuario?.FindFirstValue(ClaimTypes.Role);

    /// <summary>O tipo do claim das exceções.
    ///
    /// ⚠️ NOME CURTO E FORA DOS `ClaimTypes`, pelo mesmo motivo que o `empresa_id`: nomes do
    /// esquema da Microsoft passam pelo mapa de remapeamento do JwtBearer, e este não — chega
    /// intacto, do jeito que foi escrito.</summary>
    public const string TipoExcecoes = "nxp";

    /// <summary>As exceções que o token carrega, ou `null` quando não há nenhuma — o caso de quase
    /// todo mundo, e por isso o token de quase todo mundo não cresce um byte.
    ///
    /// O valor é o nome da API com SINAL: `+cancelar_venda` concede, `-ver_historico` revoga.
    ///
    /// ⚠️ TUDO QUE NÃO SE RECONHECE É IGNORADO, e isso é regra, não descuido: valor sem sinal,
    /// vazio, ou com o nome de uma permissão que não existe mais. Um token malformado não pode
    /// virar acesso nem derrubar a requisição — ele cai para a base do papel, que é o
    /// comportamento de sempre.</summary>
    public static IReadOnlyDictionary<Permissao, bool>? ExcecoesDe(ClaimsPrincipal? usuario)
    {
        var valores = usuario?.FindAll(TipoExcecoes).Select(c => c.Value).ToList();
        if (valores is null || valores.Count == 0) return null;

        var excecoes = new Dictionary<Permissao, bool>();

        foreach (var valor in valores)
        {
            if (valor.Length < 2 || (valor[0] != '+' && valor[0] != '-')) continue;
            if (Permissoes.DoNomeDaApi(valor[1..]) is { } gesto) excecoes[gesto] = valor[0] == '+';
        }

        return excecoes.Count == 0 ? null : excecoes;
    }
}
