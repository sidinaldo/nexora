namespace Nexora.Core.Entidades;

/// <summary>===================== O CICLO DE UMA PESQUISA =====================
///
/// `Agendada`   nasce quando a venda e concluida, com data futura
/// `Enviada`    a mensagem saiu; a partir daqui a resposta do cliente e lida
/// `Respondida` nota confirmada — pelo cliente, ou por um humano a partir de `PossivelNota`
/// `PossivelNota` ha um numero na resposta e o leitor nao se arrisca; o vendedor decide
/// `Expirada`   passou `NpsDiasExpiracao` sem nota. ⚠️ SEM REENVIO: quem nao respondeu em tres
///              dias nao responde ao quarto lembrete, e insistir queima o numero
/// `Cancelada`  a venda foi cancelada antes do envio, ou o adiamento estourou o limite
/// ======================================================================</summary>
public enum StatusPesquisaNps
{
    Agendada,
    Enviada,
    Respondida,
    PossivelNota,
    Expirada,
    Cancelada
}

/// <summary>===================== A PESQUISA POS-VENDA (NPS) =====================
///
/// Depois que a venda e concluida, o cliente recebe no WhatsApp "de 0 a 10, quanto voce nos
/// recomendaria?". A nota entra no relatorio, e nota baixa gera acao.
///
/// ⚠️ `NegociacaoId`, E NAO `VendaId`. O prompt pedia `venda_id` com indice unico por venda, e a
/// tabela `vendas` NAO EXISTE NESTE PROJETO — a venda E a negociacao desde o E4b, que reinseriu as
/// linhas dela como negociacoes. Escrevi um teste contra `db.Vendas` no LPA-1 e o compilador
/// recusou; aqui o nome ja nasce certo.
///
/// ⚠️ UMA PESQUISA POR VENDA, E A GARANTIA E DO BANCO: `uq_pesquisas_nps_negociacao`, unico sobre
/// `negociacao_id`. O agendamento e por RECONCILIACAO — a rodada diaria procura negociacao
/// concluida sem pesquisa e insere — justamente porque os dois caminhos de conclusao
/// (`ServicoVendas.ConcluirAsync` e `ConclusaoAutomatica`) sao baseados em CONJUNTO
/// (`ExecuteUpdate` e SQL cru) e nao carregam entidade onde pendurar um gancho. Sem o indice
/// unico, duas rodadas sobrepostas criariam duas pesquisas para a mesma venda.
///
/// ⚠️ `DataLimite` E GUARDADA, NAO DERIVADA. "Reagendamento maximo de 7 dias" poderia sair de
/// `negociacoes.concluida_em + nps_dias_apos_conclusao + 7`, mas `nps_dias_apos_conclusao` e
/// configuracao e PODE MUDAR — e aí o limite de uma pesquisa viva se moveria para tras ou para
/// frente sozinho. A decisao congela no nascimento, como a `DataAgendada`.
///
/// E um contador de adiamentos nao serve no lugar dela: se a rodada nao rodar por tres dias, o
/// contador nao avanca e a pesquisa vive alem do limite que alguem prometeu.
/// ================================================================================</summary>
public class PesquisaNps : IEntidadeCriada
{
    public long Id { get; set; }

    public long EmpresaId { get; set; }

    /// <summary>A venda. FK COMPOSTA com `empresa_id`: negociacao de outra empresa nao pode ser
    /// pesquisada por esta — a mesma disciplina de `fk_negociacoes_etiquetas_negociacao`.</summary>
    public long NegociacaoId { get; set; }

    /// <summary>⚠️ GUARDADO, mesmo saindo da negociacao. A `LiberacaoDeCiclo` nao mexe nisto, e a
    /// leitura da resposta chega pelo CONTATO (o webhook so sabe o telefone): procurar a pesquisa
    /// aberta por `contato_id` e o caminho quente, e um join na negociacao a cada mensagem
    /// recebida seria trabalho por nada.</summary>
    public long ContatoId { get; set; }

    /// <summary>A mensagem que fez a pergunta. ⚠️ E O QUE LIGA A RESPOSTA CITADA A PESQUISA: o
    /// `contextInfo.stanzaId` do payload de entrada casa com o `wa_message_id` DESTA linha. Nulo
    /// enquanto `Agendada`.</summary>
    public long? MensagemEnvioId { get; set; }

    public StatusPesquisaNps Status { get; set; } = StatusPesquisaNps.Agendada;

    /// <summary>O DIA em que a pergunta deve sair, no fuso da empresa. DATA e nao instante, pela
    /// mesma razao de `Mensagem.DataDisparo`: o envio e por dia, e a hora quem decide e a janela
    /// de atendimento.</summary>
    public DateOnly DataAgendada { get; set; }

    /// <summary>Depois deste dia, `Cancelada`. Ver o comentario da classe para por que e guardada
    /// em vez de derivada.</summary>
    public DateOnly DataLimite { get; set; }

    public DateTime? DataEnvio { get; set; }
    public DateTime? DataResposta { get; set; }

    /// <summary>0 a 10, com check no banco. ⚠️ PREENCHIDA TAMBEM EM `PossivelNota`, e ali e uma
    /// SUSPEITA, nao um resultado: o relatorio le `status = 'respondida'`, nunca `nota IS NOT
    /// NULL`. Confundir os dois poria no NPS um numero que ninguem confirmou.</summary>
    public short? Nota { get; set; }

    /// <summary>O que o cliente escreveu junto da nota. ⚠️ APAGADO PELA ANONIMIZACAO — e texto
    /// livre, logo pode conter qualquer coisa sobre a pessoa. A nota numerica FICA: ela e um
    /// numero sem vinculo identificavel, e e o padrao que o projeto ja adota.</summary>
    public string? Comentario { get; set; }

    /// <summary>A mensagem de entrada que trouxe a nota.</summary>
    public long? MensagemRespostaId { get; set; }

    /// <summary>Quem confirmou a `PossivelNota` na mao. NULO quando o cliente respondeu de forma
    /// que o leitor aceitou sozinho — e e essa distincao que permite medir depois se o leitor esta
    /// bom: muita confirmacao manual quer dizer que as regras dele estao apertadas.</summary>
    public long? ConfirmadaPorUsuarioId { get; set; }

    public DateTime CriadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Negociacao Negociacao { get; set; } = null!;
    public Contato Contato { get; set; } = null!;
}
