namespace Nexora.Core.Tempo;

/// <summary>Tempo decorrido contando SÓ o expediente.
///
/// ===================== POR QUE O DESCONTO EXISTE =====================
/// O semáforo mede "há quanto tempo este cliente espera resposta". Sem descontar as horas
/// fechadas, uma mensagem que chegou às 23h aparece VERMELHA às 8h da manhã seguinte — e o
/// vendedor abre o sistema todo dia com a tela inteira vermelha por algo que ninguém poderia
/// ter respondido.
///
/// O efeito é o pior possível: ele para de olhar para o semáforo. Um alerta que sempre acende
/// não é alerta, e essa é a única forma de o semáforo deixar de funcionar.
///
/// Uma mensagem das 19h50, com janela até 20h, tem 10 MINUTOS de espera às 8h do dia
/// seguinte — não 12 horas.
/// =====================================================================
///
/// Função pura: sem I/O, sem relógio implícito. O cliente tem um espelho disto em TypeScript
/// (nucleo/semaforo.ts) porque a cor precisa envelhecer entre requisições, sem novo fetch.</summary>
public static class TempoUtil
{
    /// <summary>Minutos decorridos entre `inicio` e `fim` contando apenas o tempo DENTRO da
    /// janela de atendimento. Ambos os instantes já devem estar no fuso de negócio.</summary>
    public static int MinutosUteis(
        DateTime inicio, DateTime fim, JanelaAtendimento janela, IReadOnlySet<DateOnly> feriados)
    {
        if (fim <= inicio) return 0;

        double total = 0;
        var cursor = inicio;

        // Percorre dia a dia. A trava de 400 iterações é a mesma ideia do CalendarioAtendimento:
        // com bitmask zerado (dado ruim) o laço não terminaria nunca.
        for (var i = 0; i < 400 && cursor < fim; i++)
        {
            var dia = DateOnly.FromDateTime(cursor);

            if (CalendarioAtendimento.DiaPermitido(dia, janela.DiasSemana, feriados))
            {
                var abre = dia.ToDateTime(new TimeOnly(janela.HoraInicio, 0));
                var fecha = janela.HoraFim >= 24
                    ? dia.ToDateTime(TimeOnly.MinValue).AddDays(1)
                    : dia.ToDateTime(new TimeOnly(janela.HoraFim, 0));

                var de = cursor > abre ? cursor : abre;
                var ate = fim < fecha ? fim : fecha;
                if (ate > de) total += (ate - de).TotalMinutes;
            }

            // Próximo dia, já na abertura.
            cursor = dia.AddDays(1).ToDateTime(new TimeOnly(janela.HoraInicio, 0));
        }

        return (int)Math.Floor(total);
    }
}
