namespace Nexora.Core.Servicos;

/// <summary>Como os pontos são agrupados. O valor vira `date_trunc` no SQL, e a lista é fechada
/// de propósito — texto livre do cliente virando unidade de `date_trunc` é injeção com outro
/// nome.</summary>
public enum AgrupamentoSerie
{
    Dia,
    Semana,
    Mes
}

/// <summary>Um ponto da série.
///
/// `TempoRespostaMinutos` é NULLABLE e os outros três não, e a diferença é deliberada:
///
///   • contagem e dinheiro em período vazio valem ZERO — é um fato ("não entrou lead nenhum");
///   • MÉDIA em período vazio não vale zero. Zero minuto diria "respondeu instantaneamente", e a
///     métrica passaria a mostrar seu melhor número justamente nos dias em que ninguém trabalhou.
///
/// O período em si NUNCA falta — é isso que impede o gráfico de mentir sobre a tendência.</summary>
public record PontoSerie(
    DateOnly Data,
    int Leads,
    int Vendas,
    decimal Faturamento,
    decimal? TempoRespostaMinutos,
    /// <summary>A média móvel de cada métrica (`MediaMovel`), só no agrupamento por DIA (AUD-XX,
    /// #23). A do tempo de resposta é sobre os períodos MEDIDOS — o período sem medição não entra
    /// na janela nem ganha média.</summary>
    decimal? MediaLeads,
    decimal? MediaVendas,
    decimal? MediaFaturamento,
    decimal? MediaTempoResposta);

/// <summary>`PeriodosSemMedicao`: quantos períodos ficaram sem tempo de resposta medido — a tela
/// diz quantos ficaram de fora do gráfico de tempo, em vez de contar (AUD-XX, #22).</summary>
public record SerieTemporalDto(
    DateOnly De,
    DateOnly Ate,
    string Agrupamento,
    IReadOnlyList<PontoSerie> Pontos,
    int PeriodosSemMedicao);

public interface IServicoSerie
{
    /// <summary>Evolução no período. `ate` é INCLUSIVO na intenção do usuário ("até 31/08") e o
    /// serviço converte para o corte exclusivo que o SQL usa.</summary>
    Task<SerieTemporalDto> ObterAsync(
        DateOnly de, DateOnly ate, AgrupamentoSerie agrupamento, CancellationToken ct);
}
