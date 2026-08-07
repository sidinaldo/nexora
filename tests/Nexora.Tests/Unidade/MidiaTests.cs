using System.Globalization;
using System.Text;
using Nexora.Core.Whatsapp;

namespace Nexora.Tests.Unidade;

/// <summary>MID-1 — o corte da prévia e a identificação do arquivo pelo conteúdo.
///
/// Os dois defeitos que estes testes fixam são invisíveis em revisão: `texto[..120]` parece
/// obviamente certo, e "confere a extensão" parece obviamente suficiente.</summary>
public class MidiaTests
{
    // ==================================================================== prévia
    /// <summary>Emoji que ocupam MUITAS unidades UTF-16 — os que quebram de verdade.
    ///
    /// A família são 4 pares substitutos ligados por ZWJ (11 unidades); a bandeira são 2
    /// indicadores regionais (4 unidades); o polegar com tom de pele é emoji + modificador
    /// (4 unidades). Cortar no meio de qualquer um produz meio par substituto — que a lista da
    /// caixa de entrada mostra como o losango preto de interrogação.</summary>
    public static TheoryData<string, string> EmojiCompostos() => new()
    {
        { "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466", "família" },
        { "\U0001F1E7\U0001F1F7", "bandeira do Brasil" },
        { "\U0001F44D\U0001F3FE", "polegar com tom de pele" },
        { "❤️", "coração com seletor de variação" }
    };

    [Theory]
    [MemberData(nameof(EmojiCompostos))]
    public void PREVIA_COM_EMOJI_COMPOSTO_NO_LIMITE_NAO_QUEBRA_O_CARACTERE(string emoji, string qual)
    {
        // O emoji cai EXATAMENTE em cima do corte: 119 letras + ele. Cortar por unidade de
        // código partiria o emoji ao meio.
        var texto = new string('a', PreviaTexto.Tamanho - 1) + emoji + "resto que deve sair";

        var previa = PreviaTexto.Cortar(texto)!;

        // 1. Nenhum caractere QUEBRADO.
        //
        // ⚠️ Não dá para afirmar "sem substitutos": emoji VÁLIDO é feito de pares substitutos —
        // foi assim que a primeira versão deste teste reprovou código correto. O que denuncia o
        // corte no meio é o substituto ÓRFÃO, e `EnumerateRunes` o decodifica como U+FFFD.
        Assert.DoesNotContain(Rune.ReplacementChar, previa.EnumerateRunes());

        // 2. O emoji entrou INTEIRO, não pela metade. `qual` entra no contexto: sem ele, a
        //    saída do runner mostraria só bytes e ninguém saberia QUAL caso quebrou.
        Assert.True(previa.EndsWith(emoji), $"o {qual} foi cortado ao meio");

        // 3. E o corte aconteceu: o resto ficou de fora.
        Assert.DoesNotContain("resto", previa);

        // 4. Exatamente 120 caracteres no sentido humano.
        Assert.Equal(PreviaTexto.Tamanho, new StringInfo(previa).LengthInTextElements);
    }

    [Fact]
    public void O_corte_ANTIGO_por_unidade_de_codigo_quebrava_mesmo()
    {
        // O contrapeso: sem ele, os testes acima passariam mesmo se `Cortar` não fizesse nada de
        // especial, e ninguém saberia que havia defeito.
        var emoji = "\U0001F1E7\U0001F1F7";
        var texto = new string('a', PreviaTexto.Tamanho - 1) + emoji + "resto";

        var antigo = texto[..PreviaTexto.Tamanho];

        // O corte antigo deixa um substituto órfão no fim -> U+FFFD ao decodificar.
        Assert.Contains(Rune.ReplacementChar, antigo.EnumerateRunes());
        Assert.DoesNotContain(Rune.ReplacementChar, PreviaTexto.Cortar(texto)!.EnumerateRunes());
    }

    [Fact]
    public void Texto_curto_passa_inteiro_e_null_continua_null()
    {
        Assert.Equal("oi 👋", PreviaTexto.Cortar("oi 👋"));
        Assert.Null(PreviaTexto.Cortar(null));
        Assert.Equal("", PreviaTexto.Cortar(""));
    }

    // ==================================================================== assinatura
    [Theory]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 })]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 })]
    [InlineData("application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 })]
    public void Detecta_o_tipo_pelos_BYTES(string esperado, byte[] conteudo) =>
        Assert.Equal(esperado, AssinaturaArquivo.Detectar(conteudo));

    [Fact]
    public void Webp_precisa_do_RIFF_E_do_WEBP()
    {
        byte[] webp = [.. "RIFF"u8, 0x20, 0x00, 0x00, 0x00, .. "WEBP"u8, 0x56, 0x50];
        Assert.Equal("image/webp", AssinaturaArquivo.Detectar(webp));

        // RIFF sozinho é um contêiner genérico — WAV e AVI também são RIFF. Aceitar só pelo
        // "RIFF" deixaria passar formato que não está na whitelist.
        byte[] wav = [.. "RIFF"u8, 0x20, 0x00, 0x00, 0x00, .. "WAVE"u8];
        Assert.Null(AssinaturaArquivo.Detectar(wav));
    }

    [Fact]
    public void EXECUTAVEL_RENOMEADO_PARA_PDF_NAO_ENGANA()
    {
        // O caso que a checagem de extensão deixa passar inteiro: o nome diz `.pdf`, o
        // Content-Type do multipart diz `application/pdf`, e o conteúdo é um `.exe`.
        byte[] executavel = [0x4D, 0x5A, 0x90, 0x00, 0x03];   // "MZ" — cabeçalho PE do Windows

        Assert.Null(AssinaturaArquivo.Detectar(executavel));
    }

    [Fact]
    public void Arquivo_curto_demais_nao_estoura()
    {
        Assert.Null(AssinaturaArquivo.Detectar([]));
        Assert.Null(AssinaturaArquivo.Detectar([0xFF]));
        Assert.Null(AssinaturaArquivo.Detectar([.. "RIF"u8]));
    }

    // ==================================================================== whitelist de envio
    [Fact]
    public void ENVIAR_aceita_menos_que_RECEBER_e_pela_MESMA_lista()
    {
        // Áudio e vídeo continuam sendo RECEBIDOS — é conteúdo real de negociação. Mas não
        // saem nesta fase. O filtro é sobre a mesma whitelist, não uma segunda lista.
        Assert.True(ValidadorMidia.MimePermitido("audio/ogg"));
        Assert.False(ValidadorMidia.PermitidoParaEnvio("audio/ogg"));

        Assert.True(ValidadorMidia.MimePermitido("video/mp4"));
        Assert.False(ValidadorMidia.PermitidoParaEnvio("video/mp4"));

        Assert.True(ValidadorMidia.PermitidoParaEnvio("image/jpeg"));
        Assert.True(ValidadorMidia.PermitidoParaEnvio("image/png"));
        Assert.True(ValidadorMidia.PermitidoParaEnvio("image/webp"));
        Assert.True(ValidadorMidia.PermitidoParaEnvio("application/pdf"));

        Assert.False(ValidadorMidia.PermitidoParaEnvio("application/zip"));
        Assert.False(ValidadorMidia.PermitidoParaEnvio(null));
    }

    [Fact]
    public void O_teto_de_envio_e_o_MESMO_do_recebimento()
    {
        // 16 MB, o teto do próprio WhatsApp. Um segundo número aqui divergiria na primeira
        // mudança, e a divergência apareceria como "recebi um arquivo que não consigo devolver".
        Assert.Equal(16L * 1024 * 1024, ValidadorMidia.TamanhoMaximoBytes);
        Assert.True(ValidadorMidia.TamanhoOk(ValidadorMidia.TamanhoMaximoBytes));
        Assert.False(ValidadorMidia.TamanhoOk(ValidadorMidia.TamanhoMaximoBytes + 1));
        Assert.False(ValidadorMidia.TamanhoOk(0));
    }
}
