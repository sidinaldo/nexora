namespace Nexora.Core.Entidades;

/// <summary>===================== O ESTADO DA NEGOCIACAO =====================
///
/// Cinco estados de UMA coisa. Ate aqui eles moravam em dois lugares: tres na `vendas`
/// (`StatusVenda`) e dois em colunas do contato (`ganho_em`, `perdido_em`).
///
///   Aberta     esta numa etapa do funil, aparece no quadro
///   Ganha      virou pedido; aparece na coluna de ganho e CONTA no faturamento
///   Concluida  o pedido acabou; SAI do quadro e CONTINUA contando
///   Perdida    o negocio acabou sem venda; sai do quadro
///   Cancelada  nunca deveria ter sido registrada; sai RETROATIVAMENTE do relatorio
///
/// ⚠️ A EQUIVALENCIA COM O QUE EXISTIA, para a migracao e para os relatorios:
///   `StatusVenda.Fechada`   → Ganha
///   `StatusVenda.Concluida` → Concluida
///   `StatusVenda.Cancelada` → Cancelada
///   `contatos.perdido_em`   → Perdida
///   nenhum dos dois         → Aberta
///
/// ⚠️ CONCLUIDA E CANCELADA CONTINUAM NAO SENDO PARENTES (NEG-2). Concluir e sobre a COLUNA — o
/// pedido acabou, o dinheiro fica. Cancelar e sobre o RELATORIO — aquilo nao aconteceu, e o mes
/// corrige. Um `status >= Ganha` que tratasse as duas igual quebraria o faturamento.
///
/// Continua FALTANDO a devolucao, pelo mesmo motivo do NEG-2: ela precisa preservar o mes em que
/// a venda fechou e descontar no mes em que ocorreu, e forcar isso dentro de `cancelada` faria o
/// faturamento de um mes ja fechado mudar sozinho.
/// ====================================================================</summary>
public enum StatusNegociacao
{
    Aberta,
    Ganha,
    Concluida,
    Perdida,
    Cancelada
}

/// <summary>UM NEGOCIO com um contato. E o card do funil.
///
/// ===================== O QUE ESTA TABELA DESFAZ =====================
/// O NEG-1 dividiu de proposito: o CARIMBO do estado atual ficou em `contatos.ganho_em` e
/// `contatos.valor`, e o HISTORICO na tabela `vendas`. A divisao existia porque havia UM CARD POR
/// CONTATO — o carimbo tinha de morar em algum lugar, e o unico lugar era o contato.
///
/// O NEG-2 ja tinha concluido que o estado pertence a VENDA e nao ao contato: "um contato pode ter
/// tres compras, uma entregue, uma a caminho e uma com pendencia". So nao pode mover a POSICAO no
/// quadro junto, porque o quadro era o contato.
///
/// Esta tabela junta as duas metades. A negociacao E o card E a venda: uma linha, cinco estados.
/// ====================================================================
///
/// ===================== O QUE ISSO PERMITE QUE ANTES ERA IMPOSSIVEL =====================
/// A mesma pessoa em dois funis ao mesmo tempo — "Padaria Estrela" negociando uma encomenda em
/// Vendas e um contrato em Atacado. Com `contatos.etapa_id` sendo UMA coluna, isso nao existia.
/// ====================================================================================
///
/// ⚠️ O que NAO vem para ca: `contatos.origem` e `origem_detalhe`. A origem e da PESSOA e nao se
/// reescreve (NEG-1) — o cliente que voltou pelo panfleto de julho continua sendo o lead do
/// Instagram de marco. O que e DESTA rodada e `canal_ciclo_id`, e esse vem.</summary>
public class Negociacao : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    /// <summary>De quem e o negocio. O contato continua existindo e continua sendo a PESSOA —
    /// nome, telefone, origem, LGPD. O que saiu dele foi a posicao no funil.</summary>
    public long ContatoId { get; set; }

    /// <summary>Em qual funil. Redundante com a etapa (a etapa ja sabe a pipeline dela) e gravado
    /// assim de proposito: sem esta coluna, "todas as negociacoes desta pipeline" viraria um join
    /// com `etapas_funil` em toda consulta do quadro e do relatorio.</summary>
    public long PipelineId { get; set; }

    /// <summary>Em qual etapa. ⚠️ Enquanto ABERTA e a posicao VIVA no quadro; a partir de `Ganha`
    /// ela para de se mover e vira o registro de ONDE o negocio fechou — que e o papel que
    /// `vendas.etapa_id` tinha.</summary>
    public long EtapaId { get; set; }

    /// <summary>Como o negocio se chama: "Orcamento de 200 salgados".
    ///
    /// Anulavel porque o lead que chega pelo WhatsApp nao tem titulo nenhum — o card mostra o nome
    /// do contato ate alguem dar um. Exigir titulo na entrada faria o webhook inventar um.</summary>
    public string? Titulo { get; set; }

    /// <summary>⚠️ DOIS SIGNIFICADOS NA MESMA COLUNA, e e o preco de juntar as duas tabelas:
    /// enquanto ABERTA e a ESTIMATIVA (anulavel, era `contatos.valor`); a partir de `Ganha` e o
    /// valor FECHADO, obrigatorio e maior que zero (era `vendas.valor`, com `ck_vendas_valor`).
    ///
    /// O CHECK `ck_negociacoes_valor` expressa exatamente isso, e e ele que impede uma negociacao
    /// ganha sem valor — que entraria no faturamento como zero.</summary>
    public decimal? Valor { get; set; }

    /// <summary>A posicao dentro da coluna. `numeric` sem escala declarada: o arrasto calcula o
    /// ponto medio entre dois vizinhos, e o limite real e o `decimal` do C# (~90 divisoes), nao a
    /// coluna.</summary>
    public decimal OrdemKanban { get; set; }

    public long? ResponsavelId { get; set; }

    public StatusNegociacao Status { get; set; } = StatusNegociacao.Aberta;

    public DateTime? GanhaEm { get; set; }
    public DateTime? ConcluidaEm { get; set; }
    public long? ConcluidaPor { get; set; }
    public DateTime? PerdidaEm { get; set; }
    public DateTime? CanceladaEm { get; set; }
    public long? CanceladaPor { get; set; }
    public string? MotivoPerda { get; set; }

    public string? Observacao { get; set; }

    /// <summary>A campanha que trouxe ESTA rodada (NEG-3).
    ///
    /// Vivia em `conversas.canal_ciclo_id`, com a documentacao descrevendo exatamente o ciclo de
    /// vida de uma negociacao: "o codigo de campanha detectado numa mensagem recebida desde a
    /// ultima venda concluida... copiado para a venda no fechamento e LIMPO ao concluir. Proxima
    /// volta, proximo ciclo."
    ///
    /// Era uma negociacao sem tabela. Agora tem onde morar.
    ///
    /// ⚠️ Anulavel E com FK `SetNull`, como `vendas.canal_id`: o canal pode ser apagado, e apagar
    /// uma campanha nao pode ser impedido pelo historico de negocios.</summary>
    public long? CanalCicloId { get; set; }

    // ⚠️ `venda_id` MORREU AQUI (E4e/5), como o comentario dela prometia. Ela existia para
    // `ConcluirAsync` e `CancelarAsync` — que recebiam IDS DE VENDA — acharem a negociacao
    // espelho, e existia porque nao havia regra derivavel: um contato podia ter varias vendas, e
    // cancelar aceitava cancelar uma antiga.
    //
    // A licao dela sobrevive ao codigo, e vale para o proximo elo que alguem for inventar: NAO
    // ADIANTA CASAR POR TIMESTAMP. A primeira versao de `CancelarAsync` comparava
    // `contato.GanhoEm == venda.FechadaEm`, e um teste a derrubou — duas vendas no mesmo instante
    // casavam as duas. Timestamp nao e chave.

    /// <summary>⚠️ O `xmin` DO POSTGRES, nao uma coluna.
    ///
    /// E o que faz o arrasto detectar conflito: o cliente devolve a versao que pintou na tela, e
    /// se outra pessoa moveu o card no meio do caminho a API recusa com 409 em vez de sobrescrever.
    ///
    /// Migrou de `Contato` junto com a posicao no quadro — ela e que precisa da protecao. Uma
    /// coluna `versao` de verdade exigiria que TODO caminho de escrita lembrasse de incrementa-la.</summary>
    public uint Versao { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Contato Contato { get; set; } = null!;
    public Pipeline Pipeline { get; set; } = null!;
    public EtapaFunil Etapa { get; set; } = null!;
    public Usuario? Responsavel { get; set; }
    public CanalCaptacao? CanalCiclo { get; set; }
}
