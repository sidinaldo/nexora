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
    StatusVenda? Status = null,
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
    decimal ValorCancelado);

/// <summary>O rodapé. Vem do SQL, não de somar os pontos em memória — e não é preciosismo: com
/// agrupamento por mês os pontos são 12 e a soma daria certo, mas a mesma consulta serve para o
/// CSV de um ano em dias, e ali seriam 365 linhas trafegadas para produzir sete números.</summary>
public record TotaisVendas(
    int Vendas,
    decimal Faturamento,
    int Concluidas,
    decimal ValorConcluido,
    int Canceladas,
    decimal ValorCancelado,
    decimal TicketMedio);

public record RelatorioVendas(IReadOnlyList<PontoVendas> Pontos, TotaisVendas Totais);

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
    /// <summary>Ganhos ÷ (ganhos + perdidos). Contato ainda em negociação NÃO entra — incluí-lo
    /// faria a taxa despencar sempre que entrasse lead novo, que é o oposto do que a métrica
    /// deve mostrar. Mesma conta do dashboard.</summary>
    double Conversao);

// ==================================================================== 3 · origem
public record LinhaOrigem(string Origem, int Leads, int Vendas, decimal Valor, double Conversao);

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
/// <summary>Quantos ENTRARAM na etapa durante o período. Sai da trilha (AUD-1) — ver
/// `IServicoRelatorios.FunilNoPeriodoAsync` para o que isso implica.</summary>
public record EntradaEtapa(long EtapaId, string Nome, short Ordem, string Cor, int Entradas);

/// <summary>Quantos ESTÃO na etapa agora. Pergunta diferente da de cima, e por isso um tipo
/// diferente: misturar as duas numa linha só é o que produz o rótulo mentiroso.</summary>
public record EtapaAgora(long EtapaId, string Nome, short Ordem, string Cor, int Contatos, decimal Valor);

/// <summary>As duas metades, lado a lado e nomeadas.</summary>
public record RelatorioFunil(
    IReadOnlyList<EntradaEtapa> Entradas,
    IReadOnlyList<EtapaAgora> Agora,
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

/// <summary>O que a barra de filtros precisa para se desenhar, numa chamada só.
///
/// ⚠️ EXISTE PORQUE `equipe` E `etapas` SÃO `[Authorize(Roles="dono")]`. O gestor pode ver o
/// relatório inteiro e não pode listar a equipe — montar o seletor a partir daquelas rotas daria
/// 403 para ele, e a tela ficaria sem filtro justamente para quem mais usa.
///
/// Aqui as listas saem recortadas pelo MESMO papel do relatório: vendedor recebe só a si mesmo,
/// e o seletor dele nasce travado sem que a tela precise saber por quê.</summary>
public record OpcoesRelatorio(
    IReadOnlyList<OpcaoFiltro> Responsaveis,
    IReadOnlyList<OpcaoFiltro> Etapas,
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
