using Nexora.Infra.Servicos;

namespace Nexora.Api.Servicos;

public class OpcoesAgendadorConexoes
{
    /// <summary>De quanto em quanto tempo os números são conferidos na Evolution.
    ///
    /// 5 minutos: é quanto um número caído pode passar como "conectado" no pior caso. O custo é um
    /// GET na Evolution por número por rodada — com cem empresas de três números, um por segundo.</summary>
    public TimeSpan Intervalo { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Desligar a conferência. Para o teste e para uma parada de emergência — uma
    /// Evolution sobrecarregada se alivia aqui, sem deploy.</summary>
    public bool Habilitado { get; set; } = true;
}

/// <summary>Confere os números de WhatsApp na Evolution periodicamente.
///
/// Agendador próprio pelo mesmo motivo dos outros: ritmo próprio. A rodada diária roda às 8h, e
/// um número que cai às 9h ficaria "conectado" no banner até o dia seguinte.
///
/// ⚠️ O PRIMEIRO GIRO É DEPOIS DO INTERVALO, NÃO NO BOOT. Num deploy que reinicia a Evolution
/// junto, a conferência no boot acharia a Evolution ainda subindo, marcaria todos os números como
/// `offline` e acenderia o banner de todo mundo por cinco minutos à toa.
///
/// As proteções são as do `AgendadorConversoes`: `try/catch` em volta da rodada (exceção que
/// sobe derruba o BackgroundService), o log dentro do catch também protegido, e `Task.Delay` com o
/// `TimeProvider` injetado.</summary>
public class AgendadorConexoes(
    IServiceProvider provedor,
    OpcoesAgendadorConexoes opcoes,
    TimeProvider relogio,
    ILogger<AgendadorConexoes> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opcoes.Habilitado)
        {
            log.LogWarning("Conferência dos números DESLIGADA por configuração.");
            return;
        }

        log.LogInformation("Conferência dos números a cada {Intervalo}.", opcoes.Intervalo);

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
            // O verificador é Scoped (usa DbContext); este BackgroundService é Singleton.
            using var escopo = provedor.CreateScope();
            await escopo.ServiceProvider.GetRequiredService<VerificadorConexoes>().ExecutarAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Registrar(ex, "A conferência dos números falhou. O agendador segue de pé.");
        }
    }

    /// <summary>Log que NÃO pode lançar: perder uma linha de log é aceitável, perder o agendador
    /// não é.</summary>
    private void Registrar(Exception ex, string mensagem)
    {
        try { log.LogError(ex, "{Mensagem}", mensagem); }
        catch
        {
            try { Console.Error.WriteLine($"[conexoes] {mensagem} {ex}"); } catch { /* nada a fazer */ }
        }
    }
}
