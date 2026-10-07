namespace Nexora.Core.Servicos;

/// <summary>Os QUATRO números da fase 1, mais faturamento e conversão.
///
/// ===================== TODO NÚMERO DAQUI CHEGA PRONTO (AUD-1) =====================
/// A tela somava a linha "Todos" do funil, agrupava as origens, cortava as seis maiores e ajustava
/// os percentuais para fechar 100. Tudo isso passou para cá; o painel só desenha e formata.
///
/// ⚠️ E CADA UM VÊ O SEU. Quem não tem `ver_numeros_da_equipe` recebe os números DELE — vendas,
/// conversão, funil, origens, campanhas, leads, conversas e follow-ups. Antes recebia os da empresa
/// inteira, e a permissão diz "sem esta, cada um vê só o seu".
/// =================================================================================</summary>
public record DashboardDto(
    int LeadsHoje,
    int AguardandoResposta,
    int FollowUpsPendentes,
    int VendasDoMes,
    decimal FaturamentoDoMes,

    /// <summary>ganhos ÷ (ganhos + perdidos) no mês, de 0 a 100. Null sem nada decidido no mês —
    /// ver `Percentual`. Era uma fração de 0 a 1 que a tela multiplicava.</summary>
    decimal? TaxaConversaoPercentual,
    IReadOnlyList<FunilNoPainelDto> Funil,

    /// <summary>A linha "Todos" do cartão de funis: a soma das linhas, feita aqui.</summary>
    int TotalEmNegociacao,
    decimal TotalValorEmAberto,

    /// <summary>Quantos leads a rosca representa (o número no topo do cartão).</summary>
    int LeadsTotal,
    IReadOnlyList<FatiaOrigemDto> Origens,
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

/// <summary>===================== UMA LINHA POR FUNIL, NAO POR ETAPA (FUN-1) =====================
///
/// Era `EtapaFunilDto`: uma lista com as etapas de TODOS os funis juntas. Com dois funis a tela
/// mostrava "Novo Lead" e "Novo lead" coladas, sem nada dizendo de quem era qual — e o mesmo nome
/// em funis diferentes e legitimo
/// (`EtapasDbTests.O_MESMO_NOME_DE_ETAPA_VALE_EM_FUNIS_DIFERENTES`).
///
/// ⚠️ PIOR QUE CONFUNDIR, A LISTA NAO ERA SEQUER ORDENAVEL. `Ordem` e unica POR PIPELINE
/// (`uq_etapas_ordem`): as etapas de ordem 1 dos dois funis saiam juntas, as de ordem 2 juntas, e
/// a forma de funil desaparecia — 14, 5, 9, 3, 6, 2, 4, 1, 3, 2 em vez de uma curva que estreita.
///
/// Sem nome de etapa, a confusao DEIXA DE EXISTIR em vez de ser contornada. E a tela passa a
/// responder "qual funil esta funcionando?", que nenhuma tela do Nexora respondia. O detalhe etapa
/// a etapa continua no quadro e no relatorio, que e onde ele e procurado.
///
/// ⚠️ DOIS RECORTES DE TEMPO NA MESMA LINHA, e a tela TEM que declarar qual e qual:
/// `EmNegociacao`/`ValorEmAberto` sao AGORA, `GanhasNoMes`/`Conversao` sao DO MES. Sem o rotulo, o
/// dono compara o total daqui com "Vendas do mes" la em cima e conclui que os numeros nao batem.
/// ==========================================================================================</summary>
public record FunilNoPainelDto(
    long PipelineId, string Nome, string Cor,
    int EmNegociacao, decimal ValorEmAberto,
    int GanhasNoMes,

    /// <summary>A MESMA conta do KPI do topo: ganhas / (ganhas + perdidas) no mes. Nao
    /// "ganhas / entradas" — duas formulas com o mesmo nome na mesma tela e defeito esperando
    /// para acontecer, e a linha "Todos" tem que fechar com o cartao. De 0 a 100; null sem nada
    /// decidido no mes (AUD-1).</summary>
    decimal? ConversaoPercentual);

/// <summary>===================== UMA FATIA DA ROSCA, JÁ AGRUPADA (AUD-1) =====================
///
/// De onde vêm os leads. `Origem` sai em minúsculas, como todo enum desta API. Era uma linha por
/// (origem, campanha), e a TELA somava por origem, ordenava, cortava as seis maiores, juntava o
/// resto em "Outros" e ajustava os percentuais para fechar 100. Agora chega assim:
///
///   · uma fatia por origem, da maior para a menor;
///   · passando de `ServicoDashboard.MaximoDeFatias`, as cinco maiores e uma fatia `Agrupada` com
///     o resto (`Origem = "outros"`, sem campanhas);
///   · `Percentual` de 0 a 100, com 2 casas, e as fatias SOMAM 100 (`Percentual.Fatias`).
///
/// SEM cor: a paleta é decisão de apresentação e mora no cliente. Diferente da etapa do funil,
/// que tem `cor` porque o DONO escolhe a cor dela no cadastro.
/// ====================================================================================</summary>
public record FatiaOrigemDto(
    string Origem, bool Agrupada, int Leads, decimal Percentual,

    /// <summary>As campanhas nomeadas dentro da origem (`contatos.origem_detalhe`), da maior para
    /// a menor. Quem chegou sem código não vira linha: a diferença para o total já diz.</summary>
    IReadOnlyList<CampanhaDaOrigemDto> Campanhas);

public record CampanhaDaOrigemDto(string Nome, int Leads);

public interface IServicoDashboard
{
    /// <summary>O payload RICO, sob demanda. O barato (badge e banner do shell) é o
    /// /api/painel/status — o shell faz polling dele de 45s e não pode carregar isto.</summary>
    Task<DashboardDto> DashboardAsync(CancellationToken ct);
}
