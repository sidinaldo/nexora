namespace Nexora.Core.Whatsapp;

/// <summary>Gateway de WhatsApp. Implementado na Infra pelo cliente HTTP da Evolution API.
/// O Core nao sabe que existe Evolution — so que existe "um jeito de mandar mensagem".</summary>
public interface IClienteWhatsApp
{
    /// <summary>Envia texto e devolve o id da mensagem no WhatsApp (key.id), que e o que
    /// correlaciona o envio com o ACK que chega depois pelo webhook.
    /// Lanca <see cref="Servicos.IntegracaoWhatsAppException"/> se o envio falhar.</summary>
    Task<string> EnviarTextoAsync(string instanceName, string telefone, string texto, CancellationToken ct);

    /// <summary>Envia um arquivo (base64) e devolve o wa_message_id.
    /// mediatype: "document" | "image" | "audio" | "video".</summary>
    Task<string> EnviarMidiaAsync(string instanceName, string telefone, string base64,
        string mediatype, string mimeType, string fileName, string? legenda, CancellationToken ct);

    /// <summary>Baixa o conteudo (base64) de uma mensagem de MIDIA recebida, pelo wa_message_id.
    /// Null se a Evolution nao devolver o arquivo.</summary>
    Task<MidiaRecebida?> ObterMidiaAsync(string instanceName, string waMessageId, CancellationToken ct);

    /// <summary>Estado ao vivo da instancia: open|connecting|close|nao_criada|offline.
    /// NUNCA lanca — offline significa que a propria Evolution nao respondeu.</summary>
    Task<string> StatusInstanciaAsync(string instanceName, CancellationToken ct);

    /// <summary>Cria a instancia se preciso e devolve o QR (ou o codigo de pareamento, quando
    /// <paramref name="numeroPareamento"/> vem preenchido).</summary>
    Task<RespostaQr> ConectarInstanciaAsync(string instanceName, string? numeroPareamento, CancellationToken ct);

    /// <summary>Dados da instancia conectada (ownerJid/perfil), para carimbar o numero real ao
    /// parear. Null se a Evolution nao responder ou nao houver dados. Nunca lanca.</summary>
    Task<DetalhesInstancia?> ObterDetalhesInstanciaAsync(string instanceName, CancellationToken ct);

    /// <summary>Desconecta o numero (mantem a instancia).</summary>
    Task DesconectarInstanciaAsync(string instanceName, CancellationToken ct);
}

/// <summary>Conteudo de uma midia recebida, baixada da Evolution.</summary>
public record MidiaRecebida(string Base64, string? MimeType, string? FileName);

/// <summary>Detalhes da instancia (fetchInstances): o numero conectado (ownerJid, ex.
/// "5584...@s.whatsapp.net"), o nome/foto do perfil e o estado.</summary>
public record DetalhesInstancia(string? OwnerJid, string? PerfilNome, string? PerfilFotoUrl, string Estado);

/// <summary>QR devolvido pela Evolution. Base64 = a imagem (data:image/png;base64,...);
/// PairingCode = codigo alternativo de pareamento por numero.</summary>
public record RespostaQr(string? Base64, string? Codigo, string? PairingCode, string Estado);
