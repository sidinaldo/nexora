namespace Nexora.Core.Servicos;

/// <summary>Os QUATRO números da fase 1, mais faturamento e conversão.</summary>
public record DashboardDto(
    int LeadsHoje,
    int AguardandoResposta,
    int FollowUpsPendentes,
    int VendasDoMes,
    decimal FaturamentoDoMes,
    double TaxaConversao,
    IReadOnlyList<EtapaFunilDto> Funil,
    IReadOnlyList<OrigemDto> Origens);

/// <summary>Quantos contatos e quanto valor há em cada etapa — a leitura do funil.</summary>
public record EtapaFunilDto(long EtapaId, string Nome, short Ordem, string Cor, int Contatos, decimal Valor);

/// <summary>De onde vêm os leads. `Origem` sai em minúsculas, como todo enum desta API.
///
/// SEM cor: a paleta é decisão de apresentação e mora no cliente. Diferente da etapa do funil,
/// que tem `cor` porque o DONO escolhe a cor dela no cadastro — aqui não há nada a escolher, e
/// mandar hex do servidor obrigaria uma migration para mudar um tom.</summary>
public record OrigemDto(string Origem, int Leads);

public interface IServicoDashboard
{
    /// <summary>O payload RICO, sob demanda. O barato (badge e banner do shell) é o
    /// /api/painel/status — o shell faz polling dele de 45s e não pode carregar isto.</summary>
    Task<DashboardDto> DashboardAsync(CancellationToken ct);
}
