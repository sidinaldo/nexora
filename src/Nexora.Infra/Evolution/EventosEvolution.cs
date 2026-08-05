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
