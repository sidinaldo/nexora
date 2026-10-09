using Nexora.Infra.CloudApi;

namespace Nexora.Api.Servicos;

/// <summary>Esvazia a fila do webhook da Meta (INT-XX). Acorda pelo aviso de cada entrega gravada
/// e, sem aviso, a cada 15 segundos — a rede de seguranca para o que ficou para tras (reserva de uma
/// rodada que morreu, falha que volta para a fila).
///
/// Lote cheio = ha mais esperando, e a rodada seguinte comeca na hora, sem esperar o relogio.</summary>
public class AgendadorWebhooksMeta(
    IServiceProvider provedor,
    SinalWebhooksMeta sinal,
    ILogger<AgendadorWebhooksMeta> log) : BackgroundService
{
    private static readonly TimeSpan Teto = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await sinal.EsperarAsync(Teto, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                int pegas;
                do
                {
                    // O motor e Scoped (usa DbContext); este BackgroundService e Singleton.
                    using var escopo = provedor.CreateScope();
                    pegas = await escopo.ServiceProvider.GetRequiredService<MotorWebhooksMeta>().DrenarAsync(ct);
                }
                while (pegas == MotorWebhooksMeta.Lote && !ct.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "A rodada da fila do webhook da Meta falhou. O agendador segue de pé.");
            }
        }
    }
}
