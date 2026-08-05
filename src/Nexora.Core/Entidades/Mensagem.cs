namespace Nexora.Core.Entidades;

/// <summary>Cada mensagem trocada com o contato, nas duas direcoes. TAMBEM E A OUTBOX.
///
/// ===================== O PROTOCOLO (nao inverter) =====================
/// GRAVA a linha  ->  SO ENTAO chama o WhatsApp  ->  confirma (ou registra a falha).
///
/// Disparar antes de gravar significa que um crash entre as duas etapas reenvia a mensagem na
/// proxima rodada. Na falha a linha FICA, com o erro gravado: apagar transformaria um POST que
/// na verdade chegou (mas deu timeout na resposta) em mensagem duplicada.
/// ======================================================================
///
/// INBOUND DE NUMERO DESCONHECIDO: ConversaId e ContatoId sao NOT NULL, entao nao existe
/// mensagem orfa. A consequencia e deliberada — quando chega mensagem de um numero fora da
/// base, a aplicacao CRIA o contato e a conversa na mesma transacao, antes de inserir a
/// mensagem. Num CRM de vendas isso e o certo: inbound de desconhecido E um lead novo. Some,
/// junto, a aba "sem cadastro" que o Recupera precisava ter.</summary>
public class Mensagem : IEntidadeCriada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }
    public long ConversaId { get; set; }
    public long ContatoId { get; set; }
    public long ConexaoId { get; set; }

    /// <summary>Redundante com Conexao.InstanceName, DE PROPOSITO: e a chave do indice de
    /// dedupe, que precisa funcionar sem join no caminho quente do webhook.
    ///
    /// (Esta e a unica redundancia intencional do schema. A do Recupera —
    /// `empresas.instance_name` orfa depois que o desenho migrou para `conexoes` — e do tipo
    /// que se evita.)</summary>
    public string InstanceName { get; set; } = null!;

    public DirecaoMensagem Direcao { get; set; }

    /// <summary>Id da mensagem no WhatsApp (key.id). Correlaciona o ACK que chega depois pelo
    /// webhook, e e metade do indice de dedupe. NULL enquanto a linha esta reservada e ainda
    /// nao foi postada.</summary>
    public string? WaMessageId { get; set; }

    public string? Texto { get; set; }

    public TipoMidia TipoMidia { get; set; } = TipoMidia.Nenhum;
    public string? MidiaChave { get; set; }
    public string? MidiaMime { get; set; }
    public string? MidiaNome { get; set; }
    public int? MidiaBytes { get; set; }

    /// <summary>ACK numerico do WhatsApp: 0=erro, 1=enviado, 2=servidor, 3=entregue, 4=lido.
    ///
    /// SO AVANCA (o UPDATE tem `WHERE ack IS NULL OR ack &lt; novo`), porque os webhooks chegam
    /// fora de ordem e um DELIVERY_ACK atrasado nao pode sobrescrever um READ ja recebido.</summary>
    public short? Ack { get; set; }
    public DateTime? AckEm { get; set; }

    /// <summary>Quem enviou. NULL quando e entrada ou quando e lembrete automatico.</summary>
    public long? EnviadoPor { get; set; }

    public long? LembreteId { get; set; }

    /// <summary>Data-alvo do envio, so para SAIDA (ck_msg_data_disparo).
    ///
    /// Para lembrete automatico e a data para a qual foi reservada. O reserve-defer carimba o
    /// proximo dia permitido quando a janela de atendimento esta fechada ou a conexao caiu,
    /// preservando a data-alvo exata sem duplicar a linha.</summary>
    public DateOnly? DataDisparo { get; set; }

    public DateTime ReservadoEm { get; set; }

    /// <summary>NULL = reservada mas ainda nao postada (fora da janela, ou conexao caida).
    /// E o que a drenagem de pendentes procura.</summary>
    public DateTime? EnviadaEm { get; set; }

    public DateTime? RecebidaEm { get; set; }

    /// <summary>Quantas vezes o despacho foi TENTADO. Nao existe no Recupera, onde uma reserva
    /// que nunca sai apenas para de ser varrida depois de N dias — em silencio, sem deixar
    /// rastro de quantas vezes tentou.</summary>
    public short Tentativas { get; set; }

    /// <summary>ESTADO TERMINAL: a reserva passou da janela de reenvio e nao sera mais tentada.
    ///
    /// Existe porque "parar de tentar" e "nunca ter tentado" precisam ser distinguiveis. No
    /// Recupera a mensagem simplesmente sai do alcance da varredura e some do radar: o alerta do
    /// painel conta pendentes, mas nao separa "vai ser tentada" de "expirou". Aqui a linha fica,
    /// marcada, e o endpoint de saude mostra as duas contas em separado.</summary>
    public DateTime? ExpiradaEm { get; set; }

    /// <summary>Ultimo erro do despacho. A linha FICA com o erro gravado: apagar liberaria a
    /// invariante de dedupe, e um POST que na verdade chegou (mas deu timeout na resposta)
    /// viraria mensagem duplicada no reenvio.</summary>
    public string? Erro { get; set; }

    /// <summary>Payload completo do webhook (jsonb), para auditoria e replay. So os campos
    /// usados viram coluna; o resto fica aqui.</summary>
    public string? PayloadRaw { get; set; }

    public DateTime CriadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Conversa Conversa { get; set; } = null!;
    public Contato Contato { get; set; } = null!;
    public Conexao Conexao { get; set; } = null!;
    public Usuario? UsuarioEnviou { get; set; }
    public Lembrete? Lembrete { get; set; }
}
