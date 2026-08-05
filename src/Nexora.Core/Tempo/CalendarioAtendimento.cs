namespace Nexora.Core.Tempo;

/// <summary>Regras de DIA do atendimento: quais dias a empresa atende, e para qual dia útil um
/// follow-up desliza quando cai num dia fechado. Funções PURAS (sem I/O, testáveis).
///
/// Dia bloqueado = dia da semana desligado no bitmask OU feriado. O HORÁRIO não entra aqui —
/// fica no reserve-defer do motor e no cálculo do semáforo.
///
/// Portado do CalendarioRegua do Recupera; só o nome mudou (lá era régua de cobrança).</summary>
public static class CalendarioAtendimento
{
    /// <summary>A empresa atende nesta data? `diasSemanaMask` = bitmask por DayOfWeek do .NET
    /// (Dom=bit0 .. Sab=bit6).</summary>
    public static bool DiaPermitido(DateOnly d, short diasSemanaMask, IReadOnlySet<DateOnly> feriados)
    {
        var diaLigado = (diasSemanaMask & (1 << (int)d.DayOfWeek)) != 0;
        return diaLigado && !feriados.Contains(d);
    }

    /// <summary>A primeira data permitida a partir de `d` (inclusive): enquanto bloqueada, +1 dia.
    /// Deslize INDIVIDUAL — cada follow-up desliza sozinho, sem reposicionar os demais.</summary>
    public static DateOnly ProximaDataPermitida(
        DateOnly d, short diasSemanaMask, IReadOnlySet<DateOnly> feriados)
    {
        // Trava de seguranca: se NENHUM dia da semana estiver ligado (bitmask 0, dado ruim), o
        // laco nao termina nunca. Um ano e mais que suficiente para qualquer calendario valido;
        // passando disso, devolve a data e deixa o problema visivel em vez de travar o motor.
        for (var i = 0; i < 370; i++)
        {
            if (DiaPermitido(d, diasSemanaMask, feriados)) return d;
            d = d.AddDays(1);
        }
        return d;
    }
}
