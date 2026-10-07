using System.Text.Json.Serialization;

namespace Nexora.Infra.Evolution;

/// <summary>Payload do webhook da Evolution API. So os campos que usamos — o resto
/// vai inteiro para mensagens.payload_raw (auditoria/replay).</summary>
public class EventoEvolution
{
    [JsonPropertyName("event")] public string? Evento { get; set; }
    [JsonPropertyName("instance")] public string? Instance { get; set; }
    [JsonPropertyName("data")] public DadosEvento? Data { get; set; }
}

public class DadosEvento
{
    [JsonPropertyName("key")] public ChaveMensagem? Key { get; set; }
    [JsonPropertyName("message")] public ConteudoMensagem? Message { get; set; }
    [JsonPropertyName("messageTimestamp")] public long? MessageTimestamp { get; set; }

    /// <summary>Nome do perfil do WhatsApp de quem mandou. Vira o NOME do contato criado
    /// automaticamente — sem ele, cai para o telefone formatado.</summary>
    [JsonPropertyName("pushName")] public string? PushName { get; set; }

    [JsonPropertyName("messageType")] public string? MessageType { get; set; }

    /// <summary>messages.update: PENDING | SERVER_ACK | DELIVERY_ACK | READ | PLAYED | ERROR</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }

    /// <summary>connection.update: open | connecting | close.</summary>
    [JsonPropertyName("state")] public string? State { get; set; }

    /// <summary>===================== A MENSAGEM CITADA =====================
    ///
    /// Quando o cliente responde CITANDO uma mensagem, o `stanzaId` daqui e o `wa_message_id` da
    /// citada. E o sinal mais forte que existe para ler a nota do NPS: a resposta e deliberadamente
    /// sobre AQUELA pergunta.
    ///
    /// ⚠️ O CAMINHO E `data.contextInfo`, E NAO `data.message.contextInfo`. Medi no `nexora_dev`: as
    /// tres mensagens com `stanzaId` resolvem por este caminho e nenhuma pelo outro — a forma
    /// padrao do WhatsApp poria dentro de `extendedTextMessage`, e a Evolution sobe um nivel.
    ///
    /// ⚠️ E CITAR NAO QUER DIZER CITAR A PESQUISA: das tres medidas, duas citam uma saida nossa e
    /// UMA cita outra entrada (o cliente citando a propria mensagem). Quem le tem de conferir QUAL
    /// mensagem foi citada, nao so que houve citacao.
    /// ==========================================================</summary>
    [JsonPropertyName("contextInfo")] public ContextoMensagem? ContextInfo { get; set; }
}

public class ContextoMensagem
{
    /// <summary>O `wa_message_id` da mensagem citada.</summary>
    [JsonPropertyName("stanzaId")] public string? StanzaId { get; set; }
}

public class ChaveMensagem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("remoteJid")] public string? RemoteJid { get; set; }
    [JsonPropertyName("fromMe")] public bool FromMe { get; set; }
}

public class ConteudoMensagem
{
    [JsonPropertyName("conversation")] public string? Conversation { get; set; }
    [JsonPropertyName("extendedTextMessage")] public TextoEstendido? ExtendedTextMessage { get; set; }
    [JsonPropertyName("imageMessage")] public MidiaMensagem? ImageMessage { get; set; }
    [JsonPropertyName("documentMessage")] public MidiaMensagem? DocumentMessage { get; set; }
    [JsonPropertyName("audioMessage")] public MidiaMensagem? AudioMessage { get; set; }
    [JsonPropertyName("videoMessage")] public MidiaMensagem? VideoMessage { get; set; }

    /// <summary>O texto/legenda, venha do formato simples, do com contexto, ou da legenda da midia.</summary>
    public string? Texto => Conversation ?? ExtendedTextMessage?.Text ?? Midia?.Caption;

    /// <summary>A midia, se houver. mime/nome definitivos vem do getBase64FromMediaMessage no
    /// download; aqui usamos so a legenda.</summary>
    public MidiaMensagem? Midia =>
        ImageMessage ?? DocumentMessage ?? AudioMessage ?? VideoMessage;
}

public class TextoEstendido
{
    [JsonPropertyName("text")] public string? Text { get; set; }
}

public class MidiaMensagem
{
    [JsonPropertyName("mimetype")] public string? Mimetype { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }
}
