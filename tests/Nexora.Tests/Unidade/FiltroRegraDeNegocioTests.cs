using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Api;
using Npgsql;

namespace Nexora.Tests.Unidade;

/// <summary>BUG-XX: clique duplo e corrida viravam 500. O banco recusar o segundo pedido é o certo; o
/// que estava errado era a pessoa ler "erro no servidor" e achar que nada tinha sido salvo.</summary>
public class FiltroRegraDeNegocioTests
{
    private static ExceptionContext Traduzir(Exception e)
    {
        var ctx = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [])
        {
            Exception = e
        };
        new FiltroRegraDeNegocio(NullLogger<FiltroRegraDeNegocio>.Instance).OnException(ctx);
        return ctx;
    }

    private static PostgresException Unico() =>
        new("duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);

    [Fact]
    public void INDICE_UNICO_VIRA_409_COM_FRASE_CLARA()
    {
        var ctx = Traduzir(new DbUpdateException("falhou", Unico()));

        Assert.True(ctx.ExceptionHandled);
        var r = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(409, r.StatusCode);
        Assert.Contains("clique duplo", r.Value!.ToString());
    }

    [Fact]
    public void INDICE_UNICO_DE_SQL_CRU_TAMBEM()
    {
        var ctx = Traduzir(Unico());
        Assert.Equal(409, Assert.IsType<ObjectResult>(ctx.Result).StatusCode);
    }

    [Fact]
    public void CONCORRENCIA_VIRA_409()
    {
        var ctx = Traduzir(new DbUpdateConcurrencyException("0 linhas"));
        var r = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(409, r.StatusCode);
        Assert.Contains("ao mesmo tempo", r.Value!.ToString());
    }

    [Fact]
    public void OUTRO_ERRO_DE_BANCO_CONTINUA_SENDO_ERRO()
    {
        var fk = new PostgresException("fk", "ERROR", "ERROR", PostgresErrorCodes.ForeignKeyViolation);
        var ctx = Traduzir(new DbUpdateException("falhou", fk));
        Assert.False(ctx.ExceptionHandled);
    }
}
