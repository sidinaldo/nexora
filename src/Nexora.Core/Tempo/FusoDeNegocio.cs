namespace Nexora.Core.Tempo;

/// <summary>Resolve o fuso de NEGÓCIO — nunca o do servidor.
///
/// ===================== O BUG QUE ISSO EVITA =====================
/// O agendamento tem que cair no horário de Brasília. Um servidor rodando em UTC dispara a
/// rodada das 9h às 6h BRT — FORA da janela de atendimento. O motor então só RESERVA e nunca
/// posta: os follow-ups se acumulam na fila e ninguém entende por quê, porque não há erro
/// nenhum no log. O Recupera já pagou por esse diagnóstico.
/// ================================================================
///
/// O fallback é UTC-3 FIXO, e é correto: o Brasil não tem horário de verão desde 2019. Ele
/// existe porque o id `America/Sao_Paulo` depende do tzdata do host — em container alpine sem
/// o pacote `tzdata` instalado, `FindSystemTimeZoneById` lança.</summary>
public static class FusoDeNegocio
{
    public const string PadraoBrasil = "America/Sao_Paulo";

    private static readonly TimeZoneInfo BrasilFixo = TimeZoneInfo.CreateCustomTimeZone(
        "br-fixo", TimeSpan.FromHours(-3), "Horario de Brasilia (fixo)", "BRT");

    public static TimeZoneInfo Resolver(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return BrasilFixo;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return BrasilFixo; }
        catch (InvalidTimeZoneException) { return BrasilFixo; }
    }

    /// <summary>===================== O NOME QUE VAI PARA O POSTGRES =====================
    ///
    /// ⚠️ NUNCA `TimeZoneInfo.Id`. Quando o `Resolver` cai no fuso de reserva — empresa sem fuso, ou
    /// servidor sem tzdata, que e justamente por que a reserva existe —, o id e `br-fixo`, e o
    /// Postgres o recusa ("time zone br-fixo not recognized"). O agendamento do NPS mandava esse id
    /// e falhava para TODA empresa, todo dia, com uma linha de log so (revisao NPS-1).
    ///
    /// O Postgres embarca o proprio tzdata, entao o nome IANA e seguro mesmo quando o .NET nao o
    /// acha. E nunca um "UTC-03" montado a mao: em sintaxe POSIX o sinal e INVERTIDO.
    /// ==========================================================================</summary>
    public static string NomeIana(string? id) =>
        string.IsNullOrWhiteSpace(id) ? PadraoBrasil : id;

    /// <summary>"Agora" no fuso da empresa. TUDO na rodada sai daqui: a data civil "hoje" e a
    /// hora da janela vêm da MESMA base, evitando o off-by-one entre UTC e local.</summary>
    public static DateTime AgoraNo(TimeProvider relogio, TimeZoneInfo fuso) =>
        TimeZoneInfo.ConvertTimeFromUtc(relogio.GetUtcNow().UtcDateTime, fuso);
}
