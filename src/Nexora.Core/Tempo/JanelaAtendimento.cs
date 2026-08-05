namespace Nexora.Core.Tempo;

/// <summary>O horário comercial da empresa: em que horas e em que dias ela atende.
///
/// No Recupera a mesma estrutura existe por conformidade CDC (não importunar o consumidor em
/// horário de descanso). Aqui a justificativa é outra — é simplesmente o expediente — mas o
/// cálculo é idêntico.</summary>
/// <param name="DiasSemana">Bitmask por DayOfWeek do .NET: Dom=bit0 .. Sab=bit6. 126 = seg a sáb.</param>
public record JanelaAtendimento(short HoraInicio, short HoraFim, short DiasSemana)
{
    public static readonly JanelaAtendimento Padrao = new(8, 20, 126);

    /// <summary>Este instante está dentro do expediente? Considera dia da semana E hora.</summary>
    public bool Contem(DateTime quando, IReadOnlySet<DateOnly> feriados)
    {
        var dia = DateOnly.FromDateTime(quando);
        return CalendarioAtendimento.DiaPermitido(dia, DiasSemana, feriados)
            && quando.Hour >= HoraInicio && quando.Hour < HoraFim;
    }
}
