namespace Nexora.Core.Servicos;

/// <summary>Os dois recortes que o relatório compara.
///
/// `De`/`Ate` é o recorte EFETIVO do período atual — igual ao que o usuário pediu, exceto quando o
/// período está em andamento: aí ele para em HOJE, porque é com esses dias que o anterior se
/// compara.</summary>
public readonly record struct PeriodoComparado(
    DateOnly De,
    DateOnly Ate,
    DateOnly AnteriorDe,
    DateOnly AnteriorAte,
    bool EmAndamento);

/// <summary>===================== O PERÍODO ANTERIOR =====================
///
/// Função PURA sobre datas locais, sem banco e sem relógio próprio — `hoje` entra por parâmetro. É
/// o que a torna testável nas bordas que de fato quebram: mês curto, virada de ano, mês em
/// andamento.
///
/// ⚠️ SÃO DUAS REGRAS DE DESLOCAMENTO, e confundi-las é o erro provável:
///
///   · mês ou semana CHEIOS deslocam por unidade de CALENDÁRIO — março compara com fevereiro, e
///     não com "os 31 dias anteriores", que cairia no meio de fevereiro;
///   · intervalo qualquer desloca por N DIAS.
///
/// ⚠️ E O PERÍODO EM ANDAMENTO COMPARA DIAS DECORRIDOS. Em 04/10, "outubro" é 01–04/10 contra
/// 01–04/09 — nunca contra setembro inteiro. Comparar quatro dias com trinta mostraria uma queda de
/// 87% no dia 4 de todo mês, e o número estaria "certo" em cada conta e errado na pergunta.
/// ==============================================================</summary>
public static class PeriodoAnterior
{
    /// <summary>`hoje` em hora LOCAL da empresa — quem resolve o fuso é quem chama, porque é lá que
    /// a empresa é conhecida.</summary>
    public static PeriodoComparado Calcular(DateOnly de, DateOnly ate, DateOnly hoje)
    {
        var emAndamento = ate >= hoje;

        // O recorte efetivo: em andamento, para em hoje. Note que o relatório ATUAL não é cortado
        // por isso — não existe venda com data futura, então o total é o mesmo. O corte serve para
        // saber QUANTOS dias o anterior deve cobrir.
        var fim = emAndamento ? hoje : ate;

        if (EhMesInteiro(de, ate, fim, emAndamento))
        {
            var anteriorDe = de.AddMonths(-1);
            var ultimoDia = DateTime.DaysInMonth(anteriorDe.Year, anteriorDe.Month);

            // ⚠️ O DIA DECORRIDO VALE SÓ COM O MÊS EM ANDAMENTO. Num mês FECHADO o anterior é o mês
            // anterior INTEIRO: setembro (30 dias) compara com agosto até o dia 31, não até o 30.
            // Usar `fim.Day` nos dois casos comia o último dia de todo mês de 31 comparado com um
            // de 30 — e a diferença apareceria como uma queda que não existiu.
            //
            // ⚠️ E O CLAMP É O CASO 31/03 → FEVEREIRO: montar o dia 31 de fevereiro estoura.
            int dia;
            if (emAndamento) dia = fim.Day < ultimoDia ? fim.Day : ultimoDia;
            else dia = ultimoDia;

            return new PeriodoComparado(
                de, fim,
                anteriorDe,
                new DateOnly(anteriorDe.Year, anteriorDe.Month, dia),
                emAndamento);
        }

        // ⚠️ A SEMANA PRECISA DE RAMO PRÓPRIO SÓ QUANDO ESTÁ EM ANDAMENTO. Numa semana cheia o
        // deslocamento de 7 dias e o de N dias dão o mesmo resultado; numa semana em curso, não:
        // na quarta, "N dias" compararia seg–qua com sex–dom da semana passada.
        if (EhSemanaInteira(de, ate))
            return new PeriodoComparado(de, fim, de.AddDays(-7), fim.AddDays(-7), emAndamento);

        var dias = fim.DayNumber - de.DayNumber + 1;
        var anteriorAte = de.AddDays(-1);

        return new PeriodoComparado(
            de, fim, anteriorAte.AddDays(-(dias - 1)), anteriorAte, emAndamento);
    }

    /// <summary>Começa no dia 1 e vai até o fim do mês — ou até hoje, se o mês é o corrente.</summary>
    private static bool EhMesInteiro(DateOnly de, DateOnly ate, DateOnly fim, bool emAndamento)
    {
        if (de.Day != 1) return false;

        // Em andamento, o que importa é o recorte efetivo cair no MESMO mês do início: um pedido
        // de 01/09 a 31/10 no dia 04/10 não é "um mês", é um intervalo que atravessa dois.
        if (emAndamento) return de.Year == fim.Year && de.Month == fim.Month;

        return de.Year == ate.Year
            && de.Month == ate.Month
            && ate.Day == DateTime.DaysInMonth(de.Year, de.Month);
    }

    /// <summary>Segunda a domingo — a mesma semana que o `date_trunc('week')` do Postgres usa.</summary>
    private static bool EhSemanaInteira(DateOnly de, DateOnly ate) =>
        de.DayOfWeek == DayOfWeek.Monday && ate.DayNumber - de.DayNumber == 6;
}
