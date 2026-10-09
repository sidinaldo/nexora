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

    /// <summary>Duracao do audio, em segundos (BLOCO 13). NULL para qualquer outra midia.
    ///
    /// GUARDADA DESDE JA, mesmo antes de existir transcricao: ela aparece no player (o vendedor
    /// decide se ouve 8 ou 90 segundos antes de clicar) e e o que permitiria estimar o custo de
    /// uma transcricao futura sem varrer os arquivos.
    ///
    /// ⚠️ O AUDIO NAO E SUBSTITUIDO POR TRANSCRICAO. O arquivo e o registro do que aconteceu;
    /// transcricao erra nome, valor e endereco, e sem o original ninguem confere.</summary>
    public int? MidiaDuracaoSegundos { get; set; }

    /// <summary>ACK numerico do WhatsApp: 0=erro, 1=enviado, 2=servidor, 3=entregue, 4=lido.
    ///
    /// SO AVANCA (o UPDATE tem `WHERE ack IS NULL OR ack &lt; novo`), porque os webhooks chegam
    /// fora de ordem e um DELIVERY_ACK atrasado nao pode sobrescrever um READ ja recebido.</summary>
    public short? Ack { get; set; }
    public DateTime? AckEm { get; set; }

    /// <summary>Quem enviou. NULL quando e entrada ou quando e lembrete automatico.
    ///
    /// ⚠️ NAO USE ESTA COLUNA PARA SABER SE A MENSAGEM E AUTOMATICA. Ela e nula em toda ENTRADA
    /// tambem, e quem responde isso agora e `Origem`. Foi essa confusao que fez o backfill do
    /// NPS-1 precisar de `direcao = 'saida'` junto.</summary>
    public long? EnviadoPor { get; set; }

    public long? LembreteId { get; set; }

    // ===================== NPS-1: PESSOA OU ROBO =====================
    /// <summary>⚠️ AUTOMATICA NAO MEXE NO ESTADO DO ATENDIMENTO. Ela nao zera `aguardando_desde`,
    /// nao zera `nao_lidas`, nao entra no tempo de resposta e nao rejuvenesce um lead parado.
    ///
    /// A conversa e UMA por contato e o contato pode ter varios cards: sem esta coluna, o lembrete
    /// de um card apagava a espera de outro — e o cliente que aguardava resposta sumia da fila de
    /// urgencia porque um robo falou com ele.</summary>
    public OrigemMensagem Origem { get; set; } = OrigemMensagem.Humana;

    /// <summary>Qual automacao disparou. NULL quando `Origem` e humana.</summary>
    public TipoAutomacao? TipoAutomacao { get; set; }

    /// <summary>De qual card a mensagem saiu, quando saiu de um.
    ///
    /// FK COMPOSTA com `empresa_id`: uma negociacao de outra empresa nao pode ser citada por uma
    /// mensagem desta. Mesmo molde de `fk_etapas_pipeline` e `fk_usuarios_permissoes_usuario`.</summary>
    public long? NegociacaoId { get; set; }

    /// <summary>O template aprovado que esta mensagem enviou (INT-XX), ou nulo. O `Texto` e o template
    /// JA PREENCHIDO — o que o cliente leu —, e e por este id que o reenvio sabe que tem de mandar o
    /// template de novo, e nao texto livre (que a Meta recusaria fora da janela).
    ///
    /// FK COMPOSTA com `empresa_id`, e `Restrict`: template usado nao se apaga.</summary>
    public long? ModeloId { get; set; }

    /// <summary>===================== ESTA ENTRADA FOI CONSUMIDA POR UMA AUTOMACAO =====================
    ///
    /// Verdadeiro quando a mensagem do cliente foi LIDA por um robo e nao precisa de pessoa — hoje,
    /// a nota da pesquisa de NPS. "10" nao e pergunta: ninguem tem de responder.
    ///
    /// ⚠️ ELA NAO ACENDE O SEMAFORO E NAO CONTA COMO NAO LIDA, e e so isso que a coluna muda. A
    /// mensagem continua APARECENDO na conversa — ela aconteceu, e esconde-la faria o vendedor ver
    /// a nota no relatorio e nao achar de onde veio.
    ///
    /// ⚠️ A ETAPA 1 DEIXOU ESTA COLUNA DE FORA DE PROPOSITO: "coluna sem leitor e coluna que
    /// ninguem sabe se esta certa". Agora ha leitor — o `LeitorDeNota` — e ela entra.</summary>
    public bool TratadaPorAutomacao { get; set; }

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

    /// <summary>===== O INSTANTE EM QUE ESTA MENSAGEM ATRASADA FOI GRAVADA (REC-1) =====
    ///
    /// NULL = chegou em tempo real, que e o caso normal. Preenchido = entrou com atraso
    /// relevante, porque a API estava fora do ar (a Evolution reentrega o webhook por ate ~20
    /// minutos) ou porque a instancia caiu e o WhatsApp so entregou ao reconectar.
    ///
    /// POR QUE UMA COLUNA, e nao derivar: `criado_em` recebe o timestamp DA MENSAGEM, nao o da
    /// gravacao (ver InserirMensagemAsync) — de proposito, para a ordenacao da thread bater com o
    /// que o cliente viu no celular dele. O efeito colateral e que o instante em que NOS a vimos
    /// nao fica registrado em lugar nenhum, e sem ele nao da para dizer ao vendedor "estas dez
    /// mensagens sao do periodo em que o WhatsApp esteve fora".
    ///
    /// A diferenca `RecuperadaEm - RecebidaEm` e o tamanho do atraso.</summary>
    public DateTime? RecuperadaEm { get; set; }

    /// <summary>===================== EDITADA NO WHATSAPP =====================
    ///
    /// NULL = nunca foi editada, que e o caso normal. Preenchido = alguem editou esta mensagem no
    /// celular, e o valor e o instante da edicao.
    ///
    /// O WhatsApp manda a edicao como uma mensagem nova e cifrada (ver `EdicaoMensagem`). Quando
    /// da para abrir, `Texto` vira o texto NOVO e o antigo vai para `TextoOriginal`. Quando nao da
    /// — o contato ainda nao tem LID —, o `Texto` fica o antigo e a tela avisa que o novo esta no
    /// celular.
    ///
    /// Antes desta coluna a edicao virava uma linha propria com
    /// "[mensagem nao suportada: secretEncryptedMessage]" — e, por ser entrada, acendia o semaforo
    /// e somava nao lida, como se o cliente tivesse escrito de novo.</summary>
    public DateTime? EditadaEm { get; set; }

    /// <summary>O texto de ANTES da primeira edicao que conseguimos abrir. NULL quando nunca foi
    /// trocado — inclusive na edicao que nao abriu, e e isso que a tela usa para saber se o
    /// `Texto` ja e o atual.
    ///
    /// Guardado porque a troca muda o sentido: "quero 2" virar "quero 20" e informacao, e o
    /// vendedor pode ter respondido ao primeiro.</summary>
    public string? TextoOriginal { get; set; }

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

    /// <summary>O que deu errado com esta mensagem — no DESPACHO (saida) ou na RECEPCAO DA MIDIA
    /// (entrada, REC-2).
    ///
    /// A linha FICA com o erro gravado: apagar liberaria a invariante de dedupe, e um POST que na
    /// verdade chegou (mas deu timeout na resposta) viraria mensagem duplicada no reenvio.
    ///
    /// Na entrada, e o que distingue "nunca chegou" de "chegou e se perdeu". Antes do REC-2 a
    /// falha de download nao deixava rastro nenhum, e tres mensagens sumiram sem explicacao.</summary>
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
