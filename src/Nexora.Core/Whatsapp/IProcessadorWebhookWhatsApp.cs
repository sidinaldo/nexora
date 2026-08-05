namespace Nexora.Core.Whatsapp;

/// <summary>Recebe o corpo CRU do webhook da Evolution. O controller nao conhece o formato —
/// quem faz o parse e a Infra, que e o unico lugar que sabe como a Evolution fala.</summary>
public interface IProcessadorWebhookWhatsApp
{
    /// <summary>NUNCA lanca. A Evolution reentrega ate receber 2xx: uma excecao que subisse
    /// viraria loop eterno de reentrega do mesmo payload.</summary>
    Task ProcessarAsync(string payloadJson, CancellationToken ct);
}
