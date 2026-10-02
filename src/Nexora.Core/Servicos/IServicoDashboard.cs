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
    IReadOnlyList<CampanhaDto> Campanhas,

    /// <summary>===================== OS DOIS SINAIS DE ESTREIA (POS-1) =====================
    ///
    /// Respondem "esta empresa ainda não saiu do zero?", e existem porque a tela respondia isso
    /// somando os cards do quadro. Não é a mesma pergunta: empresa que vendeu tudo e concluiu tudo
    /// tem o quadro vazio, e era chamada de nova — com os números dela escondidos atrás de um aviso
    /// de boas-vindas que ocupava a página inteira.
    ///
    /// ⚠️ A TELA SÓ ESTREIA QUANDO OS DOIS SÃO FALSOS, e o `E` é a decisão. O aviso OCUPA A
    /// PÁGINA INTEIRA, então ele só pode aparecer quando não há literalmente nada para mostrar
    /// embaixo dele. Empresa que cadastrou três contatos na mão e nunca recebeu mensagem TEM o que
    /// mostrar — e quem a cutuca sobre a primeira mensagem é o checklist de Primeiros passos, que
    /// é um widget e não esconde nada.
    ///
    /// `TemContato` conta TODA linha de `contatos`, inclusive anonimizada, igual ao resto deste
    /// serviço. É de propósito: contato apagado por LGPD é prova de que a empresa operou, e o que
    /// se pergunta aqui é se ela já saiu do zero — não quantos clientes ela tem hoje.
    ///
    /// NÃO HÁ UM TERCEIRO sobre vendas: venda implica negociação, que implica contato. Sinal que
    /// não muda a resposta é sinal que confunde quem lê.
    ///
    /// Vêm daqui e não do `/api/painel/status`: aquele endpoint é batido a cada 45s por cada aba de
    /// cada usuário, e a versão honesta de `RecebeuMensagem` toca `mensagens`. Isto é decisão de
    /// abertura de página, e o dashboard é chamado uma vez.
    /// ============================================================================</summary>
    bool RecebeuMensagem,
    bool TemContato);

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
