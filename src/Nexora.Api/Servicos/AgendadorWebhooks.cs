using Nexora.Infra.Webhooks;

namespace Nexora.Api.Servicos;

public class OpcoesAgendadorWebhooks
{
    /// <summary>De quanto em quanto tempo a fila é drenada.
    ///
    /// Trinta segundos é o compromisso: um lead criado agora chega no ERP do cliente em menos de
    /// meio minuto — o suficiente para parecer imediato —, e a consulta que pergunta "o que
    /// venceu?" bate num índice PARCIAL de poucas linhas, então acordar duas vezes por minuto não
    /// custa nada quando não há nada a fazer.
    ///
    /// Não é fila de tempo real, e não precisa ser: o receptor é um sistema, não uma tela.</summary>
    public TimeSpan Intervalo { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Desligar a drenagem. Existe para o teste e para uma parada de emergência — um
    /// receptor que esteja sendo martelado por um bug nosso se resolve aqui, sem deploy.</summary>
    public bool Habilitado { get; set; } = true;
}

/// <summary>Drena a fila de webhooks de saída.
///
/// ===================== POR QUE UM SEGUNDO AGENDADOR =====================
/// O `AgendadorFollowUp` roda UMA VEZ POR DIA, no começo do expediente — é o ritmo certo para
/// lembrete de follow-up e o ritmo errado para webhook: um lead criado às 9h05 chegaria no ERP do
/// cliente no dia seguinte.
///
/// O que é reaproveitado do outro é a rodada DIÁRIA: o expurgo de entregas com mais de 30 dias
/// mora lá, porque é exatamente isso — trabalho diário.
/// =======================================================================
///
/// ===================== AS PROTEÇÕES SÃO AS MESMAS, E NÃO POR CÓPIA CEGA =====================
///   • `try/catch` em volta da rodada: exceção que sobe DERRUBA o BackgroundService, e a drenagem
///     pararia em silêncio até o próximo deploy;
///   • o log DENTRO do catch também é protegido. Custou um diagnóstico de verdade no outro
///     agendador: o provider de EventLog do Windows lança `ObjectDisposedException` no
///     desligamento, a exceção sai do catch, sobe e derruba o serviço — ou seja, o mecanismo que
///     existe para o serviço nunca cair era por onde ele caía;
///   • `Task.Delay` com o `TimeProvider` injetado, para o teste não esperar 30s de verdade.
///
/// Não há fuso aqui, e a ausência é decisão: este agendador não tem hora do dia — ele roda a cada
/// N segundos. Fuso importa para "às 8h da manhã", que é o problema do outro.
/// ============================================================================================
///
/// ⚠️ LIMITE CONHECIDO, o mesmo do `AgendadorFollowUp`: sem lock distribuído, duas instâncias
/// drenam em paralelo e o receptor pode receber a mesma entrega duas vezes. A defesa que funciona
/// nesse caso é o `id` do evento no corpo — o receptor deduplica por ele.</summary>
public class AgendadorWebhooks(
    IServiceProvider provedor,
    OpcoesAgendadorWebhooks opcoes,
    TimeProvider relogio,
    ILogger<AgendadorWebhooks> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opcoes.Habilitado)
        {
            log.LogWarning("Drenagem de webhooks DESLIGADA por configuração. Nada será entregue.");
            return;
        }

        log.LogInformation("Drenagem de webhooks a cada {Intervalo}.", opcoes.Intervalo);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(opcoes.Intervalo, relogio, ct); }
            catch (OperationCanceledException) { break; }

            await RodarAsync(ct);
        }
    }

    private async Task RodarAsync(CancellationToken ct)
    {
        try
        {
            // O motor é Scoped (usa DbContext); este BackgroundService é Singleton.
            using var escopo = provedor.CreateScope();
            await escopo.ServiceProvider.GetRequiredService<MotorWebhooks>().ExecutarAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Registrar(ex, "A rodada de webhooks falhou. O agendador segue de pé.");
        }
    }

    /// <summary>Log que NÃO pode lançar. Ver o bloco de comentário do `AgendadorFollowUp`: perder
    /// uma linha de log é aceitável; perder o agendador não é.</summary>
    private void Registrar(Exception ex, string mensagem)
    {
        try { log.LogError(ex, "{Mensagem}", mensagem); }
        catch
        {
            try { Console.Error.WriteLine($"[webhooks] {mensagem} {ex}"); } catch { /* nada a fazer */ }
        }
    }
}
