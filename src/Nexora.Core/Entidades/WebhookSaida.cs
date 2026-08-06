namespace Nexora.Core.Entidades;

/// <summary>A SAÍDA: o endereço de um sistema do cliente que o Nexora avisa quando algo acontece.
///
/// ===================== POR QUE ISTO EXISTE =====================
/// Quem compra CRM quase sempre já tem outro sistema — ERP, emissor de nota, planilha, n8n. Sem
/// isto o Nexora é uma ilha. Com isto, em vez de construir um conector por plataforma, o cliente
/// pluga onde quiser.
///
/// UMA por empresa. Não é limitação técnica: duas URLs dobrariam a tela, a tabela de entregas e a
/// conversa de suporte, e quem precisa de mais de um destino já usa um roteador (n8n, Make) — que
/// é exatamente o público desta funcionalidade.
/// ==============================================================</summary>
public class WebhookSaida : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    /// <summary>Para onde o POST vai. **Sempre https**, e nunca para faixa privada ou loopback —
    /// ver `ValidadorUrlWebhook`. Validada no cadastro E antes de cada entrega, porque DNS muda
    /// depois do cadastro.</summary>
    public string Url { get; set; } = null!;

    /// <summary>O segredo do HMAC. Mostrado UMA vez, na criação: depois disso a tela só oferece
    /// gerar outro.
    ///
    /// Guardado em claro de propósito, e a razão importa: ele não é credencial de acesso ao
    /// Nexora — é a chave com que ASSINAMOS o que mandamos. Precisa ser recuperável para assinar
    /// cada entrega, e um hash não assina nada. O que ele protege é o receptor, não nós.</summary>
    public string Segredo { get; set; } = null!;

    public bool Ativo { get; set; } = true;

    /// <summary>Manda só os IDs, sem nome, telefone, e-mail ou texto de mensagem.
    ///
    /// ===================== ISTO É TRATAMENTO DE DADO PESSOAL =====================
    /// Nome e telefone saindo daqui para o servidor de um terceiro é compartilhamento de dado
    /// pessoal, e precisa estar no contrato do cliente com esse terceiro. O Nexora não tem como
    /// saber se está — então dá a opção, e a tela avisa.
    ///
    /// Falso por padrão porque o caso comum (criar o pedido no ERP) precisa do nome. Quem só quer
    /// disparar uma sincronia liga isto e busca o resto pela API.
    /// ============================================================================</summary>
    public bool SomenteIds { get; set; }

    // ===================== OS EVENTOS, UMA COLUNA CADA =====================
    // Colunas booleanas em vez de bitmask: `WHERE em_lead_criado` é legível em consulta de
    // suporte, e a migration diz o nome de cada evento em vez de um número que só o código
    // decodifica. São cinco; o dia em que forem trinta, aí sim vira tabela.
    //
    // `mensagem.recebida` nasce DESMARCADO: é o de maior volume de longe — uma conversa ativa
    // gera dezenas por dia — e a maioria dos clientes não precisa dele.
    // =======================================================================
    public bool EmLeadCriado { get; set; } = true;
    public bool EmLeadMovido { get; set; } = true;
    public bool EmVendaFechada { get; set; } = true;
    public bool EmVendaPerdida { get; set; } = true;
    public bool EmMensagemRecebida { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;

    /// <summary>Este webhook quer saber deste evento?</summary>
    public bool Assina(EventoWebhook evento) => evento switch
    {
        EventoWebhook.LeadCriado => EmLeadCriado,
        EventoWebhook.LeadMovido => EmLeadMovido,
        EventoWebhook.VendaFechada => EmVendaFechada,
        EventoWebhook.VendaPerdida => EmVendaPerdida,
        EventoWebhook.MensagemRecebida => EmMensagemRecebida,
        _ => false
    };
}

/// <summary>Uma tentativa de entrega — e a FILA ao mesmo tempo.
///
/// ===================== A TABELA É A FILA =====================
/// Mesma disciplina de `mensagens`: nada de fila distribuída, nada de broker. A linha nasce
/// `pendente` com `proxima_tentativa_em`, a rodada drena o que venceu, e o resultado fica na
/// própria linha. Uma tabela a menos de infraestrutura para operar, e o histórico e a fila são a
/// mesma coisa — o que torna "o cliente diz que não recebeu" uma consulta, e não uma investigação.
///
/// Diferente de `emails_enviados`, aqui HÁ retry: o receptor é um sistema, não uma pessoa, e não
/// existe link na tela como caminho alternativo. Mas o retry é LIMITADO — três tentativas e para.
/// Repetir para sempre transforma um receptor quebrado numa fila que só cresce.
/// =============================================================</summary>
public class EntregaWebhook
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    /// <summary>O id do EVENTO, não da linha. Vai no corpo para o receptor deduplicar: as três
    /// tentativas carregam o MESMO id, então quem já processou reconhece e ignora.</summary>
    public Guid EventoId { get; set; }

    public EventoWebhook Evento { get; set; }

    /// <summary>O corpo exato que foi (ou vai ser) assinado e postado. Guardado pronto, não
    /// remontado na hora do envio: se o formato mudar entre a criação e a terceira tentativa, o
    /// receptor receberia dois corpos diferentes com o mesmo id de evento.</summary>
    public string Payload { get; set; } = null!;

    /// <summary>A URL do momento em que o evento nasceu. Copiada, não lida do webhook na hora:
    /// o registro precisa dizer para ONDE foi, e o cliente pode ter trocado a URL desde então.</summary>
    public string Url { get; set; } = null!;

    public StatusEntregaWebhook Status { get; set; } = StatusEntregaWebhook.Pendente;

    public short Tentativas { get; set; }

    /// <summary>O HTTP que o receptor devolveu. NULL quando nem chegou a haver resposta (DNS,
    /// timeout, recusa de conexão) — e é essa distinção que separa "meu servidor recusou" de
    /// "seu servidor não me achou".</summary>
    public int? CodigoResposta { get; set; }

    public string? Erro { get; set; }

    /// <summary>Quando a próxima tentativa vence. NULL quando não há próxima — entregue ou
    /// esgotada.</summary>
    public DateTime? ProximaTentativaEm { get; set; }

    public DateTime? EntregueEm { get; set; }
    public DateTime CriadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
}
