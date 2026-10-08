namespace Nexora.Core.Tempo;

/// <summary>Quantos meses de CALENDÁRIO completos há entre duas datas (AUD-XX, #28).
///
/// A Evolução calculava "há N meses" na tela com meses de 30,44 dias, e Leads parados com meses de
/// 30 — a mesma pessoa podia estar "há 2 meses" numa tela e "há 1 mês" na outra. Agora as duas leem
/// do servidor, e a conta é uma só: de 14/03 a 13/05 é 1 mês; a 14/05, 2.</summary>
public static class MesesCompletos
{
    public static int Entre(DateOnly de, DateOnly ate)
    {
        var meses = (ate.Year - de.Year) * 12 + (ate.Month - de.Month);
        if (ate.Day < de.Day)
        {
            meses -= 1;
        }

        if (meses < 0)
        {
            return 0;
        }

        return meses;
    }
}
