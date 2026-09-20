using Nexora.Infra.Servicos;

namespace Nexora.Api.Servicos;

public class OpcoesAgendadorImportacoes
{
    /// <summary>De quanto em quanto tempo a fila de importações é olhada.
    ///
    /// Cinco segundos, e não os trinta do webhook: aqui tem GENTE ESPERANDO. O dono clicou em
    /// importar e está olhando a barra de progresso — meio minuto parado antes de o primeiro
    /// número se mexer é tempo suficiente para ele achar que travou e clicar de novo.
    ///
    /// A pergunta "há importação na fila?" bate no índice PARCIAL `ix_importacoes_fila`, que é
    /// vazio quase o tempo todo.</summary>
    public TimeSpan Intervalo { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Desligar o processamento. Para o teste, e para uma parada de emergência.</summary>
    public bool Habilitado { get; set; } = true;
}

/// <summary>Processa as importações grandes, uma por rodada. Ver `MotorImportacoes`.
///
/// As proteções são as mesmas do `AgendadorWebhooks`, e pelas mesmas razões: `try/catch` em volta
/// da rodada (exceção que sobe DERRUBA o BackgroundService, e o processamento pararia em silêncio
/// até o próximo deploy), o log do catch também protegido, e `Task.Delay` com o `TimeProvider`
/// injetado para o teste não esperar de verdade.</summary>
public class AgendadorImportacoes(
    IServiceProvider provedor,
    OpcoesAgendadorImportacoes opcoes,
    TimeProvider relogio,
    ILogger<AgendadorImportacoes> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opcoes.Habilitado)
        {
            log.LogWarning("Processamento de importações DESLIGADO por configuração.");
            return;
        }

        log.LogInformation("Processando importações a cada {Intervalo}.", opcoes.Intervalo);

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
            // ⚠️ UM ESCOPO POR RODADA, e ele é o que isola o tenant: o `ContextoDeFundo` que o
            // motor preenche morre com o escopo, então a empresa de uma importação nunca vaza para
            // a próxima.
            using var escopo = provedor.CreateScope();
            await escopo.ServiceProvider.GetRequiredService<MotorImportacoes>().ExecutarAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Registrar(ex, "A rodada de importações falhou. O agendador segue de pé.");
        }
    }

    /// <summary>Log que NÃO pode lançar — ver o bloco do `AgendadorWebhooks`: perder uma linha de
    /// log é aceitável; perder o agendador não é.</summary>
    private void Registrar(Exception ex, string mensagem)
    {
        try { log.LogError(ex, "{Mensagem}", mensagem); }
        catch
        {
            try { Console.Error.WriteLine($"[importacoes] {mensagem} {ex}"); } catch { /* nada a fazer */ }
        }
    }
}
