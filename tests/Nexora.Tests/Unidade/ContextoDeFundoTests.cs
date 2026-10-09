using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Nexora.Api;
using Nexora.Core;
using Nexora.Core.Seguranca;

namespace Nexora.Tests.Unidade;

/// <summary>O PAPEL QUE UM JOB ASSUME (RES-XX).
///
/// O resumo diario assume a empresa como DONO para os servicos das telas contarem a empresa inteira.
/// O que nao pode acontecer e o contrario: uma requisicao de verdade herdar esse papel.</summary>
public class ContextoDeFundoTests
{
    [Fact]
    public void SEM_REQUISICAO_VALE_O_PAPEL_QUE_O_JOB_ASSUMIU()
    {
        var fundo = new ContextoDeFundo();
        fundo.Assumir(7, 3, "dono");

        var contexto = new ContextoEmpresaHttp(new HttpContextAccessor(), fundo);

        Assert.Equal(7, contexto.EmpresaId);
        Assert.Equal("dono", contexto.Papel);
        Assert.True(contexto.Pode(Permissao.VerNumerosDaEquipe));
    }

    /// <summary>A importacao assume sem papel: grava, nao le numero. Continua sem.</summary>
    [Fact]
    public void JOB_SEM_PAPEL_CONTINUA_SEM_PAPEL()
    {
        var fundo = new ContextoDeFundo();
        fundo.Assumir(7, 3);

        var contexto = new ContextoEmpresaHttp(new HttpContextAccessor(), fundo);

        Assert.Null(contexto.Papel);
        Assert.False(contexto.Pode(Permissao.VerNumerosDaEquipe));
    }

    [Fact]
    public void QUEM_ESTA_LOGADO_TEM_O_PAPEL_DO_TOKEN_E_NUNCA_O_DO_JOB()
    {
        var fundo = new ContextoDeFundo();
        fundo.Assumir(7, 3, "dono");
        var cracha = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ContextoEmpresaHttp.ClaimEmpresa, "7"),
            new Claim("sub", "9"),
            new Claim(ClaimTypes.Role, "vendedor")
        ], "teste"));
        var acessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = cracha } };

        var contexto = new ContextoEmpresaHttp(acessor, fundo);

        Assert.Equal("vendedor", contexto.Papel);
        Assert.Equal(9, contexto.UsuarioId);
    }
}
