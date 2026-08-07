namespace Nexora.Core.Whatsapp;

/// <summary>===================== O QUE O ARQUIVO E DE VERDADE (MID-1) =====================
///
/// Extensao e nome de arquivo sao texto que o cliente escolhe. `orcamento.pdf` pode ser
/// qualquer coisa, e o `Content-Type` do multipart tambem vem do navegador — nenhum dos dois
/// prova nada.
///
/// Aqui olha-se o COMECO DO CONTEUDO, que e o que os formatos de fato carregam. Nao e defesa
/// absoluta (um PDF pode conter JavaScript, e um JPEG pode ser enorme), mas fecha o caso
/// barato e comum: arquivo renomeado para passar pela whitelist.
///
/// Nao ha biblioteca: sao quatro formatos e um punhado de bytes.
/// ==============================================================================</summary>
public static class AssinaturaArquivo
{
    /// <summary>O MIME que o CONTEUDO indica, ou null quando nao e nenhum dos que aceitamos.
    ///
    /// Devolver o mime — em vez de um booleano — permite a quem chama IGNORAR o que o cliente
    /// declarou e usar o que os bytes dizem. E o unico jeito de a whitelist significar alguma
    /// coisa.</summary>
    public static string? Detectar(ReadOnlySpan<byte> conteudo)
    {
        if (Comeca(conteudo, [0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (Comeca(conteudo, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (Comeca(conteudo, [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'])) return "application/pdf";

        // WEBP e um contêiner RIFF: "RIFF" + 4 bytes de tamanho + "WEBP".
        if (Comeca(conteudo, "RIFF"u8) && conteudo.Length >= 12 && conteudo[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";

        return null;
    }

    private static bool Comeca(ReadOnlySpan<byte> conteudo, ReadOnlySpan<byte> assinatura) =>
        conteudo.Length >= assinatura.Length && conteudo[..assinatura.Length].SequenceEqual(assinatura);
}
