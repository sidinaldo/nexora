namespace Nexora.Core.Entidades;

// Espelham os enums nativos do Postgres (ver docs/SCHEMA-NEXORA.sql).
// O Npgsql traduz o nome do membro para snake_case: Vendedor -> 'vendedor'.
// NAO renomear membro sem alterar o enum no banco (ALTER TYPE ... RENAME VALUE).

/// <summary>Papel do usuario na equipe. dono = a empresa cliente (acesso total, gerencia
/// equipe e conexao); gestor = coordena a operacao; vendedor = atende e vende.</summary>
public enum PapelUsuario
{
    Dono,
    Gestor,
    Vendedor
}

/// <summary>Ciclo de vida do usuario. Tres estados, nao um booleano: 'convidado' ocupa
/// vaga mas ainda nao definiu senha, e 'inativo' e desligado e NAO ocupa vaga. Com um
/// booleano os dois seriam indistinguiveis, e a regra da tela de Equipe
/// ("vagas ocupadas = ativos + convidados") nao se escreve.</summary>
public enum StatusUsuario
{
    Ativo,
    Convidado,
    Inativo
}

/// <summary>Estado da instancia da Evolution.
///
/// 'offline' e distinto de 'desconectado' de proposito: offline = a Evolution API nao
/// respondeu (problema NOSSO, o cliente nao tem o que fazer); desconectado = a instancia
/// existe mas o numero caiu (o cliente precisa reparear). Colapsar os dois faz o painel
/// mandar escanear QR quando a culpa e da nossa infraestrutura.</summary>
public enum StatusConexao
{
    NaoCriada,
    Conectando,
    Conectado,
    Desconectado,
    Offline
}

public static class StatusConexaoExtensoes
{
    /// <summary>O rotulo que sai NA API.
    ///
    /// ===================== POR QUE NAO E `ToString().ToLower()` =====================
    /// Porque `NaoCriada` viraria "naocriada", e o rotulo desse estado em todo o resto do sistema
    /// e `nao_criada`: e assim no enum do Postgres (`status_conexao_enum`), no que a Evolution
    /// devolve em `connectionState`, e no tipo `StatusConexao` do frontend.
    ///
    /// A divergencia existia desde o bloco 3 e nunca apareceu porque NADA lia esse campo — a tela
    /// de conexao so olhava `conectado`. O ARQ-2 passou a mostrar o status de cada numero na
    /// lista, e ai o `default` do switch da tela pegaria justamente a conexao recem-criada, que e
    /// a mais comum de estar nesse estado.
    ///
    /// Um lugar so, usado pela lista e pelo evento de tempo real, para os dois nao divergirem.
    /// ==============================================================================</summary>
    public static string ParaApi(this StatusConexao status) => status switch
    {
        StatusConexao.NaoCriada => "nao_criada",
        _ => status.ToString().ToLowerInvariant()
    };
}

/// <summary>De onde o lead veio. E a ORIGEM do contato, nao o canal de conversa: alguem que
/// viu um anuncio no Instagram e mandou mensagem no WhatsApp tem origem 'instagram'.</summary>
public enum OrigemLead
{
    Instagram,
    Facebook,
    Whatsapp,
    Google,
    Site,
    Qrcode,
    Indicacao,
    Manual,
    Outro
}

/// <summary>O que o Nexora avisa para fora (INT-3).
///
/// Cinco eventos, escolhidos porque são os que um sistema externo consegue AGIR em cima: criar o
/// cadastro no ERP, mover um card no quadro dele, emitir a nota, registrar a perda, arquivar a
/// conversa. Evento que ninguém consegue usar é ruído com custo de entrega.</summary>
public enum EventoWebhook
{
    LeadCriado,
    LeadMovido,
    VendaFechada,
    VendaPerdida,
    MensagemRecebida,

    /// <summary>O evento do botão "Enviar evento de teste". NÃO é assinável — `Assina` devolve
    /// falso para ele, então nada no sistema o dispara sozinho.
    ///
    /// Tipo próprio em vez de um `lead.criado` de mentira: o receptor precisa conseguir distinguir
    /// o teste do real, senão o primeiro clique no botão cria um lead fantasma no ERP do cliente.</summary>
    Teste
}

public enum StatusEntregaWebhook
{
    /// <summary>Na fila. Tem `proxima_tentativa_em` preenchido.</summary>
    Pendente,
    Entregue,

    /// <summary>Esgotou as tentativas. NÃO volta sozinha — só por reenvio manual.</summary>
    Falhou
}

public static class EventoWebhookExtensoes
{
    /// <summary>O nome que vai NO CORPO e no header, com ponto: `lead.criado`.
    ///
    /// ===================== POR QUE NÃO `ToString().ToLower()` =====================
    /// Daria `leadcriado`, e o nome do evento é PARTE DO CONTRATO com o cliente — ele escreve um
    /// `switch` em cima disso do lado dele. Mudar depois quebra a integração de quem já ligou.
    ///
    /// O ponto separa objeto de ação, que é a convenção de todo webhook que o cliente já viu
    /// (Stripe, GitHub, Shopify). Não é estética: é o formato que ele espera sem ler documentação.
    /// =============================================================================</summary>
    public static string ParaApi(this EventoWebhook evento) => evento switch
    {
        EventoWebhook.LeadCriado => "lead.criado",
        EventoWebhook.LeadMovido => "lead.movido",
        EventoWebhook.VendaFechada => "venda.fechada",
        EventoWebhook.VendaPerdida => "venda.perdida",
        EventoWebhook.MensagemRecebida => "mensagem.recebida",
        EventoWebhook.Teste => "webhook.teste",
        _ => evento.ToString().ToLowerInvariant()
    };
}

public enum DirecaoMensagem
{
    Entrada,
    Saida
}

public enum TipoMidia
{
    Nenhum,
    Imagem,
    Documento,
    Audio,
    Video
}

public enum StatusConversa
{
    Aberta,
    Resolvida
}

public enum StatusLembrete
{
    Pendente,
    Concluido,
    Cancelado
}

/// <summary>Quem criou o lembrete. So o 'automatico' dispara mensagem e entra no teto
/// diario anti-spam; o 'manual' e lembrete de acao para o vendedor (ligar, visitar).</summary>
public enum OrigemLembrete
{
    Automatico,
    Manual
}
