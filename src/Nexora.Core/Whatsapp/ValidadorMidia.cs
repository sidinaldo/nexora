using Nexora.Core.Entidades;

namespace Nexora.Core.Whatsapp;

/// <summary>Regras de seguranca da midia: whitelist de MIME e teto de tamanho. Fora daqui
/// nenhum arquivo entra no armazenamento.
///
/// Whitelist FECHADA, nao blacklist: tipo desconhecido e recusado. O contato manda o que
/// quiser pelo WhatsApp; nos escolhemos o que guardamos.</summary>
public static class ValidadorMidia
{
    /// <summary>16 MB — o proprio teto do WhatsApp para midia. Adotar o mesmo limite evita
    /// recusar arquivo que o WhatsApp aceitou entregar.</summary>
    public const long TamanhoMaximoBytes = 16L * 1024 * 1024;

    private static readonly Dictionary<string, (TipoMidia Tipo, string Extensao)> Permitidos =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = (TipoMidia.Documento, "pdf"),
            ["image/jpeg"]      = (TipoMidia.Imagem, "jpg"),
            ["image/png"]       = (TipoMidia.Imagem, "png"),
            ["image/webp"]      = (TipoMidia.Imagem, "webp"),
            // Audio de voz: e o formato que o WhatsApp usa no botao de gravar, e num CRM de
            // vendas o cliente responde por audio o tempo todo. Recusar isso perderia conteudo
            // real de negociacao.
            ["audio/ogg"]       = (TipoMidia.Audio, "ogg"),
            ["audio/mpeg"]      = (TipoMidia.Audio, "mp3"),
            ["video/mp4"]       = (TipoMidia.Video, "mp4")
        };

    /// <summary>O mime do WhatsApp costuma vir com parametros ("audio/ogg; codecs=opus").
    /// Comparar a string inteira contra a whitelist recusaria audio de voz — que e o caso mais
    /// comum de todos.</summary>
    public static string? Normalizar(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return null;
        var corte = mime.IndexOf(';');
        return (corte > 0 ? mime[..corte] : mime).Trim().ToLowerInvariant();
    }

    public static bool MimePermitido(string? mime) =>
        Normalizar(mime) is { } m && Permitidos.ContainsKey(m);

    public static bool TamanhoOk(long bytes) => bytes > 0 && bytes <= TamanhoMaximoBytes;

    public static TipoMidia TipoDe(string? mime) =>
        Normalizar(mime) is { } m && Permitidos.TryGetValue(m, out var v) ? v.Tipo : TipoMidia.Nenhum;

    public static string ExtensaoDe(string? mime) =>
        Normalizar(mime) is { } m && Permitidos.TryGetValue(m, out var v) ? v.Extensao : "bin";

    /// <summary>O mediatype que o sendMedia da Evolution espera.</summary>
    public static string MediatypeDe(string? mime) => TipoDe(mime) switch
    {
        TipoMidia.Imagem => "image",
        TipoMidia.Audio => "audio",
        TipoMidia.Video => "video",
        _ => "document"
    };
}
