using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
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
