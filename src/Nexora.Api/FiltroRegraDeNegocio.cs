using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Nexora.Core.Servicos;
using Npgsql;

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
            //
            // `StatusHttp` e a saida para o que nao e nem um nem outro — hoje so o 422 do teto de
            // etiquetas. Quando vem preenchido, vence os dois.
            case RegraDeNegocioException ex:
                log.LogInformation("Regra de negocio: {Mensagem}", ex.Message);
                Responder(
                    ctx,
                    ex.StatusHttp
                        ?? (ex.Conflito ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest),
                    ex.Message, ex.Codigo);
                break;

            // ===================== CORRIDA E CLIQUE DUPLO (BUG-XX) =====================
            // Dois pedidos iguais ao mesmo tempo esbarram na versao da linha ou num indice unico, e
            // o banco recusa o segundo — o que e CORRETO. Sem esta traducao a recusa saia como 500
            // ("erro no servidor"), e a pessoa achava que nada tinha sido salvo e tentava de novo.
            //
            // ⚠️ A CONCORRENCIA VEM ANTES: `DbUpdateConcurrencyException` e um `DbUpdateException`.
            // ========================================================================
            case DbUpdateConcurrencyException ex:
                log.LogInformation("Conflito de concorrencia: {Mensagem}", ex.Message);
                Responder(ctx, StatusCodes.Status409Conflict, AlteradoAoMesmoTempo);
                break;

            case DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } ex:
                log.LogInformation("Indice unico recusou: {Mensagem}", ex.InnerException!.Message);
                Responder(ctx, StatusCodes.Status409Conflict, FeitoAoMesmoTempo);
                break;

            // SQL cru (`ExecuteSqlRaw`) nao embrulha: a recusa chega como `PostgresException`.
            case PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ex:
                log.LogInformation("Indice unico recusou: {Mensagem}", ex.Message);
                Responder(ctx, StatusCodes.Status409Conflict, FeitoAoMesmoTempo);
                break;

            // Evolution API fora do ar / respondeu erro: 502 Bad Gateway (o upstream falhou).
            // Sem isto, a excecao vazaria como 500 com stack trace.
            case IntegracaoWhatsAppException ex:
                log.LogWarning("Falha na Evolution API: {Mensagem}", ex.Message);
                Responder(ctx, StatusCodes.Status502BadGateway, ex.Message);
                break;
        }
    }

    public const string AlteradoAoMesmoTempo =
        "Outra pessoa alterou isto ao mesmo tempo. Atualize a tela e confira.";

    public const string FeitoAoMesmoTempo =
        "Isto já foi feito — por um clique duplo ou por outra pessoa ao mesmo tempo. "
        + "Atualize a tela e confira.";

    private static void Responder(ExceptionContext ctx, int status, string mensagem, string? codigo = null)
    {
        // O `codigo` so aparece quando ha um (INT-XX): o corpo das outras respostas fica igual.
        object corpo = codigo == null ? new { erro = mensagem } : new { erro = mensagem, codigo };
        ctx.Result = new ObjectResult(corpo) { StatusCode = status };
        ctx.ExceptionHandled = true;
    }
}
