using Nexora.Core.Conversoes;
using Nexora.Infra.Conversoes;

namespace Nexora.Api.Servicos;

public class OpcoesAgendadorConversoes
{
    /// <summary>De quanto em quanto tempo a fila de conversões é drenada.
    ///
    /// ===================== 60 s, E NÃO OS 30 DO WEBHOOK =====================
    /// O webhook corre porque do outro lado há um sistema esperando: o pedido tem de aparecer no ERP
    /// antes de o cliente ir olhar.
    ///
    /// Aqui não há ninguém esperando, e a Meta atribui pelo `event_time` do FATO — atrasar não custa
    /// atribuição. O que se ganha dobrando o intervalo é metade da pressão numa API de terceiro que
    /// tem limite de taxa, e o que se perde é nada.
    /// =======================================================================</summary>
    public TimeSpan Intervalo { get; set; } = PoliticaConversao.Intervalo;

    /// <summary>Desligar a drenagem. Existe para o teste e para uma parada de emergência: um bug
    /// nosso martelando o pixel dos clientes se resolve aqui, sem deploy.</summary>
    public bool Habilitado { get; set; } = true;
}

/// <summary>Drena a fila de conversões de anúncio (INT-4).
///
/// Terceiro agendador do sistema, e o motivo de ser separado é o mesmo que separou o de webhooks do
/// de follow-up: ritmos diferentes. O de follow-up roda uma vez por dia; este, a cada minuto.
///
/// O que é reaproveitado da rodada DIÁRIA é o expurgo — trabalho diário mora lá.
///
/// As três proteções são as mesmas do `AgendadorWebhooks`, e nenhuma é cópia cega:
///   • `try/catch` em volta da rodada, porque exceção que sobe DERRUBA o BackgroundService e a
///     drenagem pararia em silêncio até o próximo deploy;
///   • o log dentro do catch também protegido — no `AgendadorFollowUp` isso custou um diagnóstico
///     de verdade: o provider de EventLog do Windows lança no desligamento, a exceção escapa do
///     catch e derruba justamente o mecanismo que existia para o serviço não cair;
///   • `Task.Delay` com o `TimeProvider` injetado, para o teste não esperar um minuto de verdade.</summary>
public class AgendadorConversoes(
    IServiceProvider provedor,
    OpcoesAgendadorConversoes opcoes,
    TimeProvider relogio,
    ILogger<AgendadorConversoes> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opcoes.Habilitado)
        {
            log.LogWarning("Drenagem de conversões DESLIGADA por configuração. Nada será enviado.");
            return;
        }

        log.LogInformation("Drenagem de conversões a cada {Intervalo}.", opcoes.Intervalo);

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
            await escopo.ServiceProvider.GetRequiredService<MotorConversoes>().ExecutarAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Registrar(ex, "A rodada de conversões falhou. O agendador segue de pé.");
        }
    }

    /// <summary>Log que NÃO pode lançar: perder uma linha de log é aceitável, perder o agendador
    /// não é.</summary>
    private void Registrar(Exception ex, string mensagem)
    {
        try { log.LogError(ex, "{Mensagem}", mensagem); }
        catch
        {
            try { Console.Error.WriteLine($"[conversoes] {mensagem} {ex}"); } catch { /* nada a fazer */ }
        }
    }
}
