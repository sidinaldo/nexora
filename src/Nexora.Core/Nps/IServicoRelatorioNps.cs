using Nexora.Core.Servicos;

namespace Nexora.Core.Nps;

/// <summary>===================== OS NUMEROS DE UM PERIODO =====================
///
/// ⚠️ O EIXO E `data_envio`, E E A DECISAO MAIS CONSEQUENTE DESTE RELATORIO. Havia duas opcoes:
///
///   · `data_resposta` — "o NPS de quem opinou neste periodo". Mas aí "taxa de resposta" fica sem
///     denominador: a pesquisa enviada e NAO respondida nao tem `data_resposta`, e nao entraria em
///     conta nenhuma. A taxa seria sempre 100%;
///   · `data_envio` — a COORTE. De tudo que saiu no periodo, quanto voltou e com que notas. A taxa
///     de resposta passa a ser uma razao de verdade, e os dois numeros falam do mesmo conjunto.
///
/// Escolhi a coorte, e ela tem um PRECO que a tela precisa dizer:
///
/// ⚠️ A PESQUISA ENVIADA ANTEONTEM AINDA PODE SER RESPONDIDA. Num periodo que termina hoje, parte
/// das pesquisas esta no prazo e ainda nao virou nada — e contar isso como "nao respondeu" faria a
/// taxa da semana atual parecer pessima. `AindaAbertas` existe para a tela explicar: "12 ainda
/// podem responder". Sem esse numero, o dono conclui que a pesquisa nao funciona.
/// ================================================================</summary>
public record TotaisNps(
    /// <summary>Tudo que SAIU no periodo, qualquer que seja o destino.</summary>
    int Enviadas,

    int Respondidas,
    int Expiradas,

    /// <summary>Canceladas DEPOIS de enviadas — a mao, pelo vendedor. As canceladas antes do envio
    /// nao tem `data_envio` e nao entram em nada.</summary>
    int Canceladas,

    /// <summary>⚠️ O NUMERO QUE SALVA A LEITURA DA TAXA. Ver o comentario da classe.</summary>
    int AindaAbertas,

    int Promotores,
    int Neutros,
    int Detratores)
{
    /// <summary>⚠️ `% promotores − % detratores`, SOBRE AS RESPONDIDAS — nao sobre as enviadas. E a
    /// definicao do NPS, e usar as enviadas daria um numero que cai quando mais gente e
    /// perguntada, o que nao quer dizer nada.
    ///
    /// Nulo sem resposta nenhuma, e nao zero: zero e um NPS REAL (tantos promotores quanto
    /// detratores) e confundi-lo com "nao ha dados" e o tipo de engano que nao se desfaz na tela.</summary>
    public double? Nps =>
        Respondidas == 0
            ? null
            : Math.Round((Promotores - (double)Detratores) * 100 / Respondidas, 1);

    /// <summary>Respondidas ÷ enviadas. Nulo sem envio nenhum.</summary>
    public double? TaxaDeResposta =>
        Enviadas == 0 ? null : Math.Round(Respondidas * 100.0 / Enviadas, 1);
}

/// <summary>Quantas de cada nota. ⚠️ AS ONZE LINHAS SEMPRE, inclusive as de contagem zero: a
/// distribuicao e um grafico de barras, e omitir a nota 4 porque ninguem a deu faria as barras
/// mudarem de posicao entre dois periodos.</summary>
public record FatiaDaNota(short Nota, int Quantas);

/// <summary>O bloco inteiro. `Comparativo` nulo quando nao ha periodo anterior calculavel.</summary>
public record RelatorioNps(
    TotaisNps Totais,
    IReadOnlyList<FatiaDaNota> Distribuicao,
    ComparativoNps? Comparativo = null);

/// <summary>===================== O QUE SE COMPARA, E O SENTIDO DE CADA UM =====================
///
/// ⚠️ TODOS SAO `SentidoBom.Sobe`, inclusive a taxa de resposta — e e por isso que `Detratores` NAO
/// esta aqui. Detrator subindo e noticia ruim, e o `IndicadorComparativo` resolveria isso com
/// `Desce`; mas o NPS JA desce quando o detrator sobe, e ter os dois lado a lado poria a mesma
/// informacao duas vezes, com sinais opostos, na mesma linha de cartoes.
/// ====================================================================================</summary>
public record ComparativoNps(
    IndicadorComparativo Nps,
    IndicadorComparativo Respondidas,
    IndicadorComparativo TaxaDeResposta,
    IndicadorComparativo Promotores,
    DateOnly De,
    DateOnly Ate,
    bool EmAndamento);

public enum FaixaNps
{
    Promotor,
    Neutro,
    Detrator
}

/// <summary>===================== OS DOIS ATALHOS DA LISTA (NPS-1 3.4) =====================
///
/// `PromotoresQueNaoVoltaram` — nota 9 ou 10 e a ULTIMA compra mais velha que `DiasSemCompra`
/// ("sem nova venda ha X dias", como o pedido define). E a lista de quem gostou e sumiu: o cliente
/// mais facil de trazer de volta. ⚠️ Quem voltou uma vez e sumiu de novo ENTRA — nao comprou ha X
/// dias, e e justamente quem vale uma ligacao.
///
/// `DetratoresSemRetorno` — nota 0 a 6 e NENHUMA mensagem humana enviada depois da nota. O
/// lembrete do detrator foi criado; isto mostra se alguem de fato falou com ele.
///
/// ⚠️ OS DOIS IGNORAM O PERIODO DA BARRA, e de proposito. "Promotor sem compra ha 60 dias" dentro
/// de uma janela de 30 dias seria vazio POR CONSTRUCAO: a resposta chega dias depois da compra, e
/// a compra de quem respondeu nos ultimos 30 dias tem menos de 60. Atalho e lista de "com quem
/// agir agora", nao relatorio de periodo.
/// ====================================================================================</summary>
public enum AtalhoRespostas
{
    Nenhum,
    PromotoresQueNaoVoltaram,
    DetratoresSemRetorno
}

/// <summary>Os filtros PROPRIOS da lista. Periodo e responsavel vem do `FiltroRelatorio` da barra
/// — a mesma barra do relatorio, para os dois numeros baterem.</summary>
public record FiltroRespostasNps(
    FaixaNps? Faixa = null,
    bool? ComprouDeNovo = null,
    AtalhoRespostas Atalho = AtalhoRespostas.Nenhum,
    int DiasSemCompra = FiltroRespostasNps.DiasSemCompraPadrao)
{
    /// <summary>Dois meses: o ciclo de recompra de quem vende servico ou produto de uso
    /// frequente. A tela deixa trocar.</summary>
    public const int DiasSemCompraPadrao = 60;
}

/// <summary>Uma resposta, com o que o vendedor precisa para decidir se liga.
///
/// `UltimaCompraEm` e a compra MAIS RECENTE do contato, qualquer uma. `ComprouDeNovoEm` e a
/// PRIMEIRA compra depois da avaliada — nula quando ele nao voltou. ⚠️ "Compra" e negociacao
/// `ganha` ou `concluida`: a cancelada GUARDA o `ganha_em` (medido no LPA-1), e conta-la faria um
/// cliente que desistiu aparecer como quem voltou.</summary>
public record LinhaRespostaNps(
    long PesquisaId,
    long ContatoId,
    string Cliente,
    short Nota,
    DateTime DataResposta,
    string? Comentario,
    long? ResponsavelId,
    string? Responsavel,
    DateTime? UltimaCompraEm,
    DateTime? ComprouDeNovoEm);

public interface IServicoRelatorioNps
{
    /// <summary>⚠️ RECORTA POR PESSOA como todo relatorio: quem nao tem `ver_numeros_da_equipe` ve
    /// so as pesquisas das vendas DELE, e o `responsavelId` que o cliente mandar e DESCARTADO —
    /// mesma forma do `ServicoRelatorios.ResponsavelEfetivo`.</summary>
    Task<RelatorioNps> LerAsync(FiltroRelatorio filtro, CancellationToken ct);

    /// <summary>As respostas uma a uma (NPS-1 3.3). ⚠️ MESMO EIXO E MESMO RECORTE do `LerAsync`:
    /// filtrar "promotor" em setembro devolve tantas linhas quanto o cartao de promotores de
    /// setembro. E o que faz o relatorio e a lista baterem, e ha teste disso.</summary>
    Task<PaginaComTotal<LinhaRespostaNps>> RespostasAsync(
        FiltroRelatorio periodo, FiltroRespostasNps filtro, int pagina, int tamanho, CancellationToken ct);
}
