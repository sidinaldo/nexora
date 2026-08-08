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
    IReadOnlyList<OrigemDto> Origens,
    /// <summary>===================== QUAL CAMPANHA TROUXE DINHEIRO (NEG-3) =====================
    ///
    /// A rosca acima conta LEADS; esta lista conta RECEITA, no mes corrente, por campanha.
    ///
    /// Sao perguntas diferentes e as respostas divergem: a campanha que traz muita gente costuma
    /// nao ser a que traz muito dinheiro. Uma so das duas na tela faria o dono decidir onde
    /// gastar com metade da informacao.
    ///
    /// Vazia quando nenhuma venda do mes tem campanha — que e o caso comum, e a tela diz isso
    /// com todas as letras em vez de sumir.</summary>
    IReadOnlyList<CampanhaDto> Campanhas);

/// <summary>Uma campanha e o que ela faturou no mes. `Nome` nunca e nulo: a linha "sem campanha"
/// nao entra aqui — no dashboard o espaco e curto e o interessante e o ranking de quem trouxe.
/// O total sem campanha aparece inteiro no relatorio 3b.</summary>
public record CampanhaDto(string Nome, int Vendas, decimal Valor);

/// <summary>Quantos contatos e quanto valor há em cada etapa — a leitura do funil.</summary>
public record EtapaFunilDto(long EtapaId, string Nome, short Ordem, string Cor, int Contatos, decimal Valor);

/// <summary>De onde vêm os leads. `Origem` sai em minúsculas, como todo enum desta API.
///
/// SEM cor: a paleta é decisão de apresentação e mora no cliente. Diferente da etapa do funil,
/// que tem `cor` porque o DONO escolhe a cor dela no cadastro — aqui não há nada a escolher, e
/// mandar hex do servidor obrigaria uma migration para mudar um tom.</summary>
/// <summary>`Campanha` e o nome do canal que capturou o lead (`contatos.origem_detalhe`), ou
/// nulo para quem chegou sem codigo. A tela mostra a campanha quando existe e cai no rotulo da
/// origem quando nao — "Promocao de Julho" diz mais que "instagram", e as duas sao verdade.</summary>
public record OrigemDto(string Origem, int Leads, string? Campanha);

public interface IServicoDashboard
{
    /// <summary>O payload RICO, sob demanda. O barato (badge e banner do shell) é o
    /// /api/painel/status — o shell faz polling dele de 45s e não pode carregar isto.</summary>
    Task<DashboardDto> DashboardAsync(CancellationToken ct);
}
