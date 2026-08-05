using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nexora.Core.Servicos;

namespace Nexora.Api;

/// <summary>Traduz excecoes esperadas para HTTP num lugar so, para os servicos de
/// Nexora.Core lancarem sem conhecer HTTP e os controllers ficarem sem try/catch.</summary>
public class FiltroRegraDeNegocio(ILogger<FiltroRegraDeNegocio> log) : IExceptionFilter
{
    public void OnException(ExceptionContext ctx)
    {
        switch (ctx.Exception)
        {
            // Regra de negocio: 409 se o ESTADO atual impede (ja existe conversa aberta);
            // 400 se a ENTRADA esta errada (telefone em branco).
            case RegraDeNegocioException ex:
                log.LogInformation("Regra de negocio: {Mensagem}", ex.Message);
                Responder(ctx, ex.Conflito ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest, ex.Message);
                break;

            // Evolution API fora do ar / respondeu erro: 502 Bad Gateway (o upstream falhou).
            // Sem isto, a excecao vazaria como 500 com stack trace.
            case IntegracaoWhatsAppException ex:
                log.LogWarning("Falha na Evolution API: {Mensagem}", ex.Message);
                Responder(ctx, StatusCodes.Status502BadGateway, ex.Message);
                break;
        }
    }

    private static void Responder(ExceptionContext ctx, int status, string mensagem)
    {
        ctx.Result = new ObjectResult(new { erro = mensagem }) { StatusCode = status };
        ctx.ExceptionHandled = true;
    }
}
