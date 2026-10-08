using Nexora.Core.Entidades;

namespace Nexora.Core.Servicos;

/// <summary>===================== O FILTRO ÚNICO (BLOCO 14) =====================
///
/// Uma barra só na tela, um record só aqui. Cada relatório aplica o que faz sentido para ele e
/// IGNORA o resto — "motivo de perda" não diz nada num relatório de vendas fechadas.
///
/// ⚠️ NÃO EXISTE FAIXA DE VALOR GLOBAL, e a ausência é deliberada. `contatos.valor` é ESTIMATIVA
/// em aberto; `vendas.valor` é O QUE FECHOU. Um campo só na barra passaria por cima dessa
/// diferença e produziria dois relatórios com o mesmo rótulo respondendo perguntas diferentes.
/// `ValorMin`/`ValorMax` valem só onde o relatório declara sobre qual grandeza eles agem.
///
/// ⚠️ NÃO EXISTE FILTRO DE CONEXÃO. `uq_conexoes_empresa` garante uma conexão por empresa — o
/// seletor teria uma opção só. Quando multi-número existir, aí sim.
/// ======================================================================</summary>
public record FiltroRelatorio(
    /// <summary>Datas em hora LOCAL da empresa, inclusivas nas duas pontas. A conversão para o
    /// corte semi-aberto em UTC acontece no serviço — o cliente pensa em dia, não em instante.</summary>
    DateOnly De,
    DateOnly Ate,
    AgrupamentoSerie Agrupamento = AgrupamentoSerie.Dia,

    /// <summary>⚠️ DESCARTADO quando o papel é Vendedor: ali o próprio usuário é imposto. Ver
    /// `IServicoRelatorios`.</summary>
    long? ResponsavelId = null,

    OrigemLead? Origem = null,
    long? EtapaId = null,
    StatusNegociacao? Status = null,
    string? MotivoPerda = null,

    /// <summary>A faixa vale sobre a grandeza do relatório que a recebe, e o rótulo da tela diz
    /// qual. Ver a nota acima.</summary>
    decimal? ValorMin = null,
    decimal? ValorMax = null);

// ==================================================================== 1 · vendas por período
/// <summary>Um ponto da série de vendas.
///
/// `Vendas`/`Faturamento` são o número de cima: tudo que NÃO foi cancelado. `Concluidas` é um
/// SUBCONJUNTO deles — o pedido acabou, e o dinheiro continua contando. `Canceladas` fica FORA do
/// total e aparece à parte: a linha não some do relatório, porque faturamento que desaparece sem
/// rastro é pior que faturamento errado.</summary>
public record PontoVendas(
    DateOnly Periodo,
    int Vendas,
    decimal Faturamento,
    int Concluidas,
    decimal ValorConcluido,
    int Canceladas,
    decimal ValorCancelado,
    /// <summary>A média móvel do faturamento (`MediaMovel`), só no agrupamento por dia (AUD-XX, #23).</summary>
    decimal? MediaFaturamento);

/// <summary>O rodapé, somado sobre os pontos que já vieram.
///
/// ⚠️ ESTE COMENTÁRIO DIZIA O CONTRÁRIO — que o rodapé "vem do SQL, não de somar os pontos em
/// memória" — e o código nunca fez isso (`ServicoRelatorios.LerVendasAsync`). A intenção era
/// evitar trafegar 365 linhas para produzir sete números no CSV de um ano em dias; ela não foi
/// implementada, e a soma em memória é defensável porque os pontos JÁ existem para desenhar o
/// gráfico e a rota tem teto de 400.
///
/// Corrigido no CMP-1, que é quando alguém finalmente veio olhar aqui — exatamente o custo de um
/// comentário que descreve o que se pretendia e não o que está escrito.</summary>
public record TotaisVendas(
    int Vendas,
    decimal Faturamento,
    int Concluidas,
    decimal ValorConcluido,
    int Canceladas,
    decimal ValorCancelado,
    decimal TicketMedio);

/// <summary>===================== OS SETE NÚMEROS CONTRA O PERÍODO ANTERIOR (CMP-1) =====================
///
/// O relatório dizia QUANTO e não dizia se era bom. "R$ 18 mil em setembro" não ajuda ninguém
/// sozinho; "R$ 18 mil, −25% contra agosto" manda o dono procurar onde.
///
/// ⚠️ OS DOIS PERÍODOS SÃO CALCULADOS NA HORA, nunca lidos de valor guardado. É o que faz cancelar
/// uma venda de agosto corrigir o número de agosto retroativamente — e um snapshot não corrigiria,
/// deixando o passado mentir para sempre.
///
/// `De`/`Ate` é o recorte EFETIVO do atual: em período em andamento ele para em hoje, e a tela
/// mostra isso ("01–04/out vs 01–04/set"). Sem esse rótulo, quatro dias contra trinta pareceriam
/// uma queda de 87% no dia 4 de todo mês.
///
/// ⚠️ "Cancelado" É `SentidoBom.Desce`, e é ele que prova a regra de cor: subindo, seta para CIMA e
/// vermelho; caindo, seta para BAIXO e verde. Se cor e seta andassem juntas, a tela pintaria de
/// vermelho a melhor notícia do mês.</summary>
public record ComparativoVendas(
    IndicadorComparativo Vendas,
    IndicadorComparativo Faturamento,
    IndicadorComparativo Concluidas,
    IndicadorComparativo ValorConcluido,
    IndicadorComparativo Canceladas,
    IndicadorComparativo ValorCancelado,
    IndicadorComparativo TicketMedio,
    DateOnly De,
    DateOnly Ate,
    bool EmAndamento);

/// <summary>⚠️ `Comparativo` É O TERCEIRO PARÂMETRO E TEM PADRÃO, para as construções que já
/// existiam seguirem valendo — inclusive as dos testes.</summary>
public record RelatorioVendas(
    IReadOnlyList<PontoVendas> Pontos,
    TotaisVendas Totais,
    ComparativoVendas? Comparativo = null);

// ==================================================================== 2 · desempenho
/// <summary>`UsuarioId` nulo = "sem dono". Contato sem responsável existe e vende; jogá-lo fora
/// faria a soma das linhas não bater com o total do relatório 1, e ninguém saberia por quê.</summary>
public record LinhaVendedor(
    long? UsuarioId,
    string Nome,
    int LeadsAtendidos,
    int Vendas,
    decimal Valor,
    decimal TicketMedio,
    /// <summary>Ganhos ÷ (ganhos + perdidos), de 0 a 100 com 2 casas (`Percentual`); `null` sem
    /// nada decidido no período. Negócio ainda em negociação NÃO entra — incluí-lo faria a taxa
    /// despencar sempre que entrasse lead novo, que é o oposto do que a métrica deve mostrar.
    ///
    /// ⚠️ OS DOIS LADOS SÃO NEGÓCIOS DECIDIDOS NO PERÍODO, PELO DONO DO NEGÓCIO (AUD-XX, B10). O
    /// denominador era a perda pelo dono do CONTATO, de lead criado no período, sem olhar a data
    /// da perda — duas perguntas diferentes na mesma fração. Agora é a conta do painel inicial.</summary>
    decimal? ConversaoPercentual);

// ==================================================================== 3 · origem
/// <summary>`ConversaoPercentual`: vendas ÷ leads do canal, de 0 a 100 com 2 casas
/// (`Percentual`, AUD-XX).</summary>
public record LinhaOrigem(string Origem, int Leads, int Vendas, decimal Valor, decimal? ConversaoPercentual);

/// <summary>===================== QUAL CAMPANHA TROUXE DINHEIRO (NEG-3) =====================
///
/// A leitura de cima responde "de onde vieram os LEADS". Esta responde "de onde veio o
/// FATURAMENTO", que é a pergunta que o dono realmente faz — e as duas dão respostas diferentes
/// com frequência: o canal que traz muita gente costuma não ser o que traz muito dinheiro.
///
/// ⚠️ SÃO CHAVES DIFERENTES, e por isso são duas tabelas e não duas colunas. `LinhaOrigem` agrupa
/// por `contatos.origem` (o CANAL do cadastro: whatsapp, indicação, qrcode…); esta agrupa pelo
/// canal de captação nomeado ("Panfleto Julho"). Espremer as duas numa tabela só exigiria um
/// rótulo que mentisse sobre uma delas.
///
/// ⚠️ E O RECORTE TAMBÉM É OUTRO. Ali o período filtra a CRIAÇÃO do lead; aqui, o FECHAMENTO da
/// venda. "O que o canal trouxe de gente em agosto" e "o que entrou de dinheiro em agosto" são
/// perguntas distintas, e somar as duas colunas lado a lado produziria uma conversão inventada.
///
/// `Canal` vem NULO quando a venda não tem canal identificado — que é o caso comum, e a linha
/// aparece assim mesmo: escondê-la faria a fatia atribuída parecer o total.</summary>
public record LinhaCanalVenda(string? Canal, int Vendas, decimal Valor);

// ==================================================================== 4 · funil
/// <summary>===================== UMA ETAPA, AS DUAS PERGUNTAS (AUD-XX) =====================
/// `Entradas`: quantos ENTRARAM na etapa durante o período — sai da trilha (AUD-1), ver
/// `IServicoRelatorios.FunilNoPeriodoAsync` para o que isso implica. `ContatosAgora` e
/// `ValorAgora`: quantos ESTÃO nela agora, e quanto somam.
///
/// Eram duas listas (`Entradas` e `Agora`), e a tela juntava as duas por etapa — com 0 inventado
/// quando a etapa faltava numa delas. Agora vem uma linha por etapa, já juntada no servidor.
///
/// ⚠️ AS DUAS PERGUNTAS CONTINUAM SEPARADAS, em campos com nome. O que produzia o rótulo mentiroso
/// era UMA coluna respondendo as duas; uma linha com dois campos nomeados não mistura nada.
/// ==================================================================================</summary>
public record EtapaDoFunil(
    long EtapaId,
    string Nome,
    short Ordem,
    string Cor,
    long PipelineId,
    string PipelineNome,
    int Entradas,
    int ContatosAgora,
    decimal ValorAgora);

/// <summary>As etapas de todos os funis, na ordem do menu e depois da etapa.</summary>
public record RelatorioFunil(
    IReadOnlyList<EtapaDoFunil> Etapas,
    /// <summary>O instante do evento mais ANTIGO da trilha desta empresa, ou nulo se não há
    /// nenhum. A tela mostra "movimentação registrada desde 07/08/2026" — sem isso, um cliente
    /// que usa o sistema há um ano veria zero entradas e concluiria que o relatório está quebrado.</summary>
    DateTime? TrilhaComecaEm);

// ==================================================================== 5 · tempo de resposta
/// <summary>Em minutos ÚTEIS, descontando fora-de-janela e feriado — mesma regra do semáforo.
///
/// MÉDIA E MEDIANA juntas, e o par é o ponto: um atendimento esquecido puxa a média e não mexe na
/// mediana. Quem lê só a média não sabe se o time é lento ou se houve um caso solto.</summary>
public record LinhaTempoResposta(
    long? UsuarioId, string Nome, int Respostas, double MediaMinutos, double MedianaMinutos);

// ==================================================================== 6 · motivos de perda
public record LinhaMotivoPerda(string Motivo, int Contatos, decimal ValorPerdido);

// ==================================================================== 7 · recorrentes
/// <summary>Só existe por causa do NEG-1: antes dele a segunda compra sobrescrevia a primeira, e
/// "quem compra de novo" não tinha resposta no banco.</summary>
public record LinhaClienteRecorrente(
    long ContatoId, string Nome, string Telefone, int Compras, decimal Total, DateTime UltimaEm);

// ==================================================================== opções da barra
public record OpcaoFiltro(long Id, string Nome);

/// <summary>Uma etapa E O FUNIL A QUE ELA PERTENCE.
///
/// ===================== POR QUE NAO SERVE UM `OpcaoFiltro` =====================
/// O mesmo nome de etapa pode existir em funis diferentes, DE PROPOSITO
/// (`EtapasDbTests.O_MESMO_NOME_DE_ETAPA_VALE_EM_FUNIS_DIFERENTES`). Numa lista chata, "Primeiro
/// Atendimento" aparece duas vezes e nada diz qual e qual.
///
/// ⚠️ E ESTE SELETOR NAO SO CONFUNDE: ELE ENGANA. O relatorio inteiro sai recortado pela etapa
/// escolhida, entao pegar a errada devolve numeros de OUTRO processo — sem erro, sem aviso, e com
/// valores plausiveis demais para alguem desconfiar. Os outros tres lugares do FUN-1 confundem a
/// leitura; este troca a resposta.
///
/// O funil vem junto para a tela agrupar, como a de Contatos ja faz (`contatos.html`).
/// =============================================================================</summary>
public record OpcaoEtapa(long Id, string Nome, long PipelineId, string PipelineNome);

/// <summary>O que a barra de filtros precisa para se desenhar, numa chamada só.
///
/// ⚠️ EXISTE PORQUE `equipe` E `etapas` EXIGEM `ConfigurarEmpresa`/`GerenciarEquipe` — só o dono. O gestor pode ver o
/// relatório inteiro e não pode listar a equipe — montar o seletor a partir daquelas rotas daria
/// 403 para ele, e a tela ficaria sem filtro justamente para quem mais usa.
///
/// Aqui as listas saem recortadas pelo MESMO papel do relatório: vendedor recebe só a si mesmo,
/// e o seletor dele nasce travado sem que a tela precise saber por quê.</summary>
public record OpcoesRelatorio(
    IReadOnlyList<OpcaoFiltro> Responsaveis,
    IReadOnlyList<OpcaoEtapa> Etapas,
    /// <summary>Os motivos REALMENTE usados, não uma lista fixa: o campo é texto livre, e um
    /// seletor com opções que ninguém escreveu produz filtro que nunca casa.</summary>
    IReadOnlyList<string> MotivosPerda);

// ====================================================================
public interface IServicoRelatorios
{
    /// <summary>As listas da barra de filtros, já recortadas por papel.</summary>
    Task<OpcoesRelatorio> OpcoesAsync(CancellationToken ct);

    /// <summary>===================== O CORTE DE PAPEL VIVE AQUI =====================
    ///
    /// Para o papel VENDEDOR, `Filtro.ResponsavelId` é DESCARTADO e o próprio usuário é imposto,
    /// em todos os relatórios. Fica no serviço e não num `[Authorize]` no controller porque a
    /// regra não é "pode chamar a rota" — é "estes são os seus números".
    ///
    /// A tela esconder o seletor não protege nada: o vendedor troca o parâmetro na requisição.
    /// ======================================================================</summary>
    Task<RelatorioVendas> VendasPorPeriodoAsync(FiltroRelatorio filtro, CancellationToken ct);

    Task<IReadOnlyList<LinhaVendedor>> DesempenhoVendedoresAsync(
        FiltroRelatorio filtro, CancellationToken ct);

    Task<IReadOnlyList<LinhaOrigem>> OrigemLeadsAsync(FiltroRelatorio filtro, CancellationToken ct);

    /// <summary>3b (NEG-3) · faturamento por canal de captação, pelo FECHAMENTO da venda.</summary>
    Task<IReadOnlyList<LinhaCanalVenda>> VendasPorCanalAsync(
        FiltroRelatorio filtro, CancellationToken ct);

    /// <summary>===================== POR QUE A TRILHA, E O QUE ELA NÃO COBRE =====================
    ///
    /// "Quantos entraram em Proposta este mês" precisa de histórico de movimentação, que não
    /// existe como tabela própria. Mas o `InterceptorTrilha` grava `etapaId: {antes, depois}` no
    /// `jsonb` de QUALQUER evento que mude a etapa — não só do arrastar. Então `Moveu`, `Ganhou`
    /// (registrar venda move o card sem passar pelo `MoverAsync`), `Reabriu` e a criação do
    /// contato entram todos, sem construir nada.
    ///
    /// ⚠️ Filtrar por `acao = 'Moveu'` seria o caminho óbvio e estaria ERRADO: "entraram em
    /// Venda" viria sempre zero, porque aquela porta declara `Ganhou`. O predicado é sobre a
    /// PRESENÇA da chave `etapaId`, não sobre o verbo.
    ///
    /// TRÊS LIMITES, e os três vão para a tela:
    ///   1. a trilha só existe desde o deploy do AUD-1 — antes disso não há movimentação nenhuma;
    ///   2. `ExpurgoTrilha` apaga além de 12 meses, então o relatório não vai mais fundo;
    ///   3. escrita em lote por SQL cru (semeadores) não passa pelo interceptor.
    ///
    /// Por isso `Agora` vem junto: a foto atual é sempre verdadeira, e a série é a que tem
    /// ressalva. Rotular foto como período seria mentir.
    /// ==============================================================================</summary>
    Task<RelatorioFunil> FunilNoPeriodoAsync(FiltroRelatorio filtro, CancellationToken ct);

    Task<IReadOnlyList<LinhaTempoResposta>> TempoRespostaAsync(
        FiltroRelatorio filtro, CancellationToken ct);

    Task<IReadOnlyList<LinhaMotivoPerda>> MotivosPerdaAsync(
        FiltroRelatorio filtro, CancellationToken ct);

    /// <summary>PAGINADO no banco, ao contrário dos outros. Os demais são limitados pela
    /// natureza do dado — o time tem dez pessoas, a origem tem nove valores, o motivo de perda
    /// meia dúzia. Cliente recorrente não tem teto: uma padaria com dois anos de uso tem
    /// milhares, e trazer todos para cortar no C# é agregar em memória com outro nome.</summary>
    Task<Pagina<LinhaClienteRecorrente>> ClientesRecorrentesAsync(
        FiltroRelatorio filtro, int pagina, int tamanho, CancellationToken ct);
}
