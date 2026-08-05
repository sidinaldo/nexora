using Nexora.Core.FollowUp;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;

namespace Nexora.Api.Servicos;

public class OpcoesAgendador
{
    /// <summary>Hora LOCAL (0-23) da rodada diária, no fuso de negócio. 8h: começo do
    /// expediente — o vendedor abre o sistema e o Meu Dia já está montado.</summary>
    public int HoraDaRodada { get; set; } = 8;

    /// <summary>Roda uma vez no boot. SÓ para desenvolvimento — em produção deixe false, senão
    /// todo deploy dispara follow-up.</summary>
    public bool RodarNoBoot { get; set; }

    /// <summary>Fuso do AGENDAMENTO. Cada empresa ainda tem o seu (usado dentro da rodada); este
    /// decide só a que horas o job acorda.</summary>
    public string FusoHorario { get; set; } = FusoDeNegocio.PadraoBrasil;
}

/// <summary>Dispara a rodada de follow-up uma vez por dia.
///
/// É SEGURO rodar mais de uma vez: as invariantes de banco (teto diário e uq_msg_lembrete)
/// impedem lembrete e mensagem duplicados. Um restart perto da hora não causa estrago.
///
/// ⚠️ LIMITE CONHECIDO: não há lock distribuído. Com DUAS instâncias, o job roda duas vezes.
/// As invariantes protegem contra duplicar mensagem, mas o trabalho é feito em dobro e o
/// espaçamento de 3s deixa de valer entre as instâncias. Quando o Nexora escalar horizontal,
/// isso precisa de resolução (advisory lock do Postgres resolveria em poucas linhas).</summary>
public class AgendadorFollowUp(
    IServiceProvider provedor,
    OpcoesAgendador opcoes,
    TimeProvider relogio,
    ILogger<AgendadorFollowUp> log) : BackgroundService
{
    private readonly TimeZoneInfo _fuso = FusoDeNegocio.Resolver(opcoes.FusoHorario);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Semeia os feriados no boot para a primeira rodada já ter calendário.
        await GarantirFeriadosAsync(ct);

        if (opcoes.RodarNoBoot) await RodarAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var espera = AteAProximaRodada();
            log.LogInformation("Próxima rodada de follow-up em {Espera} (às {Hora}h).",
                espera, opcoes.HoraDaRodada);

            try { await Task.Delay(espera, relogio, ct); }
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

            // Garante os feriados do ano (idempotente) — cobre a virada de ano sem ninguém
            // lembrar de rodar nada em janeiro.
            await escopo.ServiceProvider.GetRequiredService<IServicoFeriados>()
                .GarantirAtualEProximoAsync(ct);

            await escopo.ServiceProvider.GetRequiredService<MotorFollowUp>().ExecutarAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NUNCA deixar a exceção subir: ela derrubaria o BackgroundService, e o follow-up
            // pararia de rodar EM SILÊNCIO até o próximo deploy. Não é zelo excessivo — é o
            // modo de falha mais caro deste componente.
            Registrar(ex, "A rodada de follow-up falhou. O agendador segue de pé.");
        }
    }

    private async Task GarantirFeriadosAsync(CancellationToken ct)
    {
        try
        {
            using var escopo = provedor.CreateScope();
            await escopo.ServiceProvider.GetRequiredService<IServicoFeriados>()
                .GarantirAtualEProximoAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Registrar(ex, "Falha ao semear feriados no boot. Seguindo.");
        }
    }

    /// <summary>Log que NÃO pode lançar.
    ///
    /// ===================== CUSTOU UM DIAGNÓSTICO DE VERDADE =====================
    /// O `catch` acima protege a operação, mas o `LogError` DENTRO dele não estava protegido — e
    /// ele lança. No Windows, o provider de EventLog estoura ObjectDisposedException quando a
    /// aplicação já está desligando; a exceção sai do catch, sobe pelo ExecuteAsync e derruba o
    /// BackgroundService. Ou seja: o mecanismo que existe para o serviço NUNCA cair era
    /// exatamente por onde ele caía.
    ///
    /// O Console.Error é o último recurso: se nem ele funcionar, engolimos. Perder uma linha de
    /// log é aceitável; perder o agendador não é.
    /// ============================================================================</summary>
    private void Registrar(Exception ex, string mensagem)
    {
        try { log.LogError(ex, "{Mensagem}", mensagem); }
        catch
        {
            try { Console.Error.WriteLine($"[follow-up] {mensagem} {ex}"); } catch { /* nada a fazer */ }
        }
    }

    /// <summary>Tempo até a próxima hora-alvo, calculado no HORÁRIO DE BRASÍLIA — não no do
    /// servidor. Um servidor em UTC dispararia às 5h BRT, fora da janela: o motor só reservaria
    /// e nunca postaria, e os follow-ups se acumulariam sem erro nenhum no log.
    ///
    /// O Brasil não tem horário de verão desde 2019, então a diferença de parede é igual à
    /// duração real do Delay.</summary>
    private TimeSpan AteAProximaRodada()
    {
        var agora = FusoDeNegocio.AgoraNo(relogio, _fuso);
        var alvo = new DateTime(agora.Year, agora.Month, agora.Day, opcoes.HoraDaRodada, 0, 0);
        if (alvo <= agora) alvo = alvo.AddDays(1);
        return alvo - agora;
    }
}
