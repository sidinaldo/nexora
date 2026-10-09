using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.CloudApi;

/// <summary>===================== A RODADA DA FILA DE ENTRADA (INT-XX) =====================
///
/// Reserva um lote do que chegou, processa na ordem de chegada, e marca o resultado.
///
/// ⚠️ A RESERVA E UM UPDATE COM `FOR UPDATE SKIP LOCKED`: duas instancias da API, ou duas rodadas
/// sobrepostas, nunca pegam a mesma linha. Reserva com mais de 5 minutos e de uma rodada que morreu
/// no meio, e volta para a fila.
///
/// Falhou? A linha volta para a fila com o `erro`, ate 5 tentativas; depois fica parada para
/// investigar. Processada, e apagada em 7 dias — o registro do que aconteceu esta em `mensagens`.
///
/// Reprocessar e seguro: a mensagem repetida e barrada pelo `ON CONFLICT` da `RecepcaoMensagem`,
/// e o status so avanca.
/// ==============================================================================</summary>
public class MotorWebhooksMeta(
    NexoraDbContext db,
    ProcessadorWebhookCloudApi processador,
    TimeProvider relogio,
    ILogger<MotorWebhooksMeta> log)
{
    public const int Lote = 50;
    public const short MaximoTentativas = 5;

    /// <summary>Quantas linhas a rodada pegou. `Lote` cheio = ha mais esperando.</summary>
    public async Task<int> DrenarAsync(CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;
        var reservaVelha = agora.AddMinutes(-5);

        var ids = await db.Database.SqlQueryRaw<long>("""
            UPDATE webhooks_meta_recebidos
               SET processando_desde = {0}, tentativas = tentativas + 1
             WHERE id IN (SELECT id
                            FROM webhooks_meta_recebidos
                           WHERE processado_em IS NULL
                             AND tentativas < {2}
                             AND (processando_desde IS NULL OR processando_desde < {1})
                           ORDER BY id
                           LIMIT {3}
                           FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """, agora, reservaVelha, MaximoTentativas, Lote).ToListAsync(ct);

        // Na ordem de chegada: a mensagem e o status dela, ou duas mensagens seguidas do cliente,
        // nao podem trocar de lugar.
        ids.Sort();

        foreach (var id in ids)
        {
            var linha = await db.WebhooksMetaRecebidos.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(x => x.Id == id, ct);

            string? erro = null;
            try
            {
                await processador.ProcessarAsync(linha, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                erro = ex.Message;
                log.LogError(ex, "Webhook da Meta {Id} falhou na tentativa {Tentativa}.", id, linha.Tentativas);
                // O que ficou a meio caminho no rastreador nao pode vazar para a proxima linha.
                db.ChangeTracker.Clear();
            }

            var fim = relogio.GetUtcNow().UtcDateTime;
            if (erro == null)
            {
                await db.WebhooksMetaRecebidos.IgnoreQueryFilters().Where(x => x.Id == id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.ProcessadoEm, fim)
                        .SetProperty(x => x.ProcessandoDesde, (DateTime?)null)
                        .SetProperty(x => x.Erro, (string?)null), ct);
            }
            else
            {
                await db.WebhooksMetaRecebidos.IgnoreQueryFilters().Where(x => x.Id == id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.ProcessandoDesde, (DateTime?)null)
                        .SetProperty(x => x.Erro, erro), ct);
            }
        }

        var limite = agora.AddDays(-7);
        await db.WebhooksMetaRecebidos.IgnoreQueryFilters()
            .Where(x => x.ProcessadoEm != null && x.ProcessadoEm < limite)
            .ExecuteDeleteAsync(ct);

        return ids.Count;
    }
}
