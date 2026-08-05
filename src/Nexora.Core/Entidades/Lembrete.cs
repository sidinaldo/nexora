namespace Nexora.Core.Entidades;

/// <summary>Follow-up: o que precisa ser feito com um contato, e quando.
///
/// Duas naturezas na mesma tabela (ver <see cref="OrigemLembrete"/>): o AUTOMATICO dispara
/// mensagem e entra no teto diario anti-spam; o MANUAL e lembrete de acao para o vendedor
/// (ligar, visitar) e so aparece no Meu Dia.</summary>
public class Lembrete : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }
    public long ContatoId { get; set; }
    public long? ConversaId { get; set; }

    public OrigemLembrete Origem { get; set; }
    public StatusLembrete Status { get; set; } = StatusLembrete.Pendente;

    /// <summary>Data-alvo, calculada na APLICACAO, nunca no banco.
    ///
    /// A regra que preserva o indice nao e usar igualdade — e NAO APLICAR FUNCAO SOBRE A
    /// COLUNA. `(CURRENT_DATE - data_alvo)` descarta o indice e a varredura vira seq scan
    /// quando a base cresce; `data_alvo &lt;= :hoje` faz range scan normal.
    ///
    /// O motor usa &lt;=, nao =: com igualdade estrita, um dia de indisponibilidade perde o
    /// lembrete para sempre. O teto diario e o indice uq_msg_lembrete impedem que a
    /// recuperacao de atrasados vire enxurrada.</summary>
    public DateOnly DataAlvo { get; set; }

    public TimeOnly? HoraAlvo { get; set; }

    public string Titulo { get; set; } = null!;
    public string? Observacao { get; set; }

    /// <summary>Se dispara mensagem, precisa ter o que enviar (ck_lembretes_texto).</summary>
    public bool EnviaMensagem { get; set; }
    public string? TextoMensagem { get; set; }

    public long? ResponsavelId { get; set; }
    public long? CriadoPor { get; set; }

    public DateTime? ConcluidoEm { get; set; }
    public long? ConcluidoPor { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Contato Contato { get; set; } = null!;
    public Conversa? Conversa { get; set; }
    public Usuario? Responsavel { get; set; }
    public Usuario? UsuarioCriou { get; set; }
    public Usuario? UsuarioConcluiu { get; set; }
}
