using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Api.Seguranca;

public class OpcoesJwt
{
    public string Chave { get; set; } = "";          // >= 32 bytes; vem do user-secrets
    public string Emissor { get; set; } = "nexora";
    public string Audiencia { get; set; } = "nexora-painel";
    public int HorasDeValidade { get; set; } = 12;    // um turno de trabalho
}

/// <summary>Emite o JWT. Fica na Api, e nao no dominio, de proposito: token e um detalhe
/// do transporte HTTP. Quem autentica (regra) e o IServicoAutenticacao; quem emite o
/// cracha e isto aqui.</summary>
public class GeradorToken(OpcoesJwt opcoes)
{
    public (string Token, DateTime ExpiraEm) Gerar(UsuarioAutenticado usuario)
    {
        var expira = DateTime.UtcNow.AddHours(opcoes.HorasDeValidade);

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, usuario.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, usuario.Email),
            new(ClaimTypes.Name, usuario.Nome),
            new(ClaimTypes.Role, usuario.Papel),
            // O claim que sustenta TODO o isolamento multi-tenant: e daqui que o
            // ContextoEmpresaHttp le o EmpresaId, e dele que o HasQueryFilter depende.
            new(ContextoEmpresaHttp.ClaimEmpresa, usuario.EmpresaId.ToString())
        ];

        // ===================== AS EXCECOES DE PERMISSAO (PER-1) =====================
        // Uma linha por gesto que DIVERGE do papel, com sinal: `+cancelar_venda` concede,
        // `-ver_historico` revoga. Quem nao tem excecao nenhuma — quase todo mundo — nao ganha
        // claim nenhum, e o token fica do mesmo tamanho de antes.
        //
        // ⚠️ AS EXCECOES, E NAO A LISTA PRONTA. A base continua saindo da tabela de `Permissoes`
        // a cada requisicao. Com a lista congelada aqui, um deploy que mudasse quem pode um gesto
        // nao alcancaria nenhuma sessao aberta por ate 12h — e `Permissoes` promete o contrario.
        //
        // ⚠️ SE ESTE BLOCO SUMIR, A SABOTAGEM E INVISIVEL: todo mundo volta em silencio a base do
        // papel, nenhuma excecao vale nada, e nenhum teste que so exercita a base quebra. Quem
        // segura isso e `PermissoesTests.O_QUE_A_TELA_RECEBE_E_O_QUE_A_ROTA_DEIXA_PASSAR_...`, que
        // faz a volta completa por aqui em vez de montar o cracha a mao.
        if (usuario.Excecoes is { } excecoes)
            foreach (var (gesto, concedida) in excecoes)
                claims.Add(new Claim(
                    ClaimsDoToken.TipoExcecoes,
                    (concedida ? "+" : "-") + Permissoes.NaApi(gesto)));

        var jwt = new JwtSecurityToken(
            issuer: opcoes.Emissor,
            audience: opcoes.Audiencia,
            claims: claims,
            expires: expira,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opcoes.Chave)),
                SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expira);
    }
}
