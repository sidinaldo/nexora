using System.IO.Compression;
using ZXing;
using ZXing.Common;

namespace Nexora.Tests.Unidade;

/// <summary>Lê de volta o QR Code que a aplicação gerou.
///
/// ===================== POR QUE ISTO EXISTE =====================
/// O critério do bloco é "o QR escaneia de verdade". Escanear com celular é manual e não fica —
/// na próxima mudança ninguém repete. Isto é o mais perto que dá para automatizar: pega o PNG que
/// o endpoint devolve, decodifica os pixels e passa por um leitor de QR INDEPENDENTE (ZXing, outra
/// implementação, outro autor) para conferir se o texto que sai é exatamente o link que entrou.
///
/// O que isso pega, e que uma asserção de formato não pegaria: o `#` do código não escapado na
/// URL (o WhatsApp receberia a frase truncada e o código nunca chegaria), nível de correção
/// trocado por engano, e conteúdo montado com o número errado.
///
/// O que isso NÃO prova: que a câmera de um celular real enxerga o papel impresso. Isso continua
/// sendo teste de campo — ver docs/INT-2.md.
/// ===============================================================
///
/// O decodificador de PNG é mínimo de propósito: cobre exatamente o que a `PngByteQRCode` emite
/// (greyscale, sem entrelaçamento, filtro por linha) e falha alto em qualquer outra coisa. Não é
/// para virar biblioteca — é para este teste não depender de mais um pacote de imagem.</summary>
public static class LeitorPngQr
{
    private static readonly byte[] Assinatura = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>O texto contido no QR de um PNG, ou null se não deu para ler.</summary>
    public static string? Ler(byte[] png)
    {
        var (largura, altura, cinza) = DecodificarGray8(png);

        var fonte = new RGBLuminanceSource(cinza, largura, altura, RGBLuminanceSource.BitmapFormat.Gray8);
        var leitor = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true }
        };

        return leitor.Decode(fonte)?.Text;
    }

    /// <summary>PNG -> um byte de luminância por pixel.</summary>
    public static (int Largura, int Altura, byte[] Cinza) DecodificarGray8(byte[] png)
    {
        if (png.Length < 8 || !png.Take(8).SequenceEqual(Assinatura))
            throw new InvalidDataException("Não é um PNG (assinatura errada).");

        int largura = 0, altura = 0, profundidade = 0, tipoCor = -1;
        var comprimido = new MemoryStream();

        var i = 8;
        while (i + 8 <= png.Length)
        {
            var tamanho = LerInt(png, i);
            var tipo = System.Text.Encoding.ASCII.GetString(png, i + 4, 4);
            var dados = i + 8;

            switch (tipo)
            {
                case "IHDR":
                    largura = LerInt(png, dados);
                    altura = LerInt(png, dados + 4);
                    profundidade = png[dados + 8];
                    tipoCor = png[dados + 9];
                    // Entrelaçamento (Adam7) mudaria o layout das linhas por completo. A QRCoder
                    // não emite; se um dia emitir, é melhor estourar aqui do que decodificar lixo.
                    if (png[dados + 12] != 0) throw new InvalidDataException("PNG entrelaçado.");
                    break;

                case "IDAT":
                    comprimido.Write(png, dados, tamanho);
                    break;
            }

            if (tipo == "IEND") break;
            i = dados + tamanho + 4;   // + CRC
        }

        if (tipoCor != 0)
            throw new InvalidDataException($"Esperado PNG em tons de cinza (colorType 0), veio {tipoCor}.");

        comprimido.Position = 0;
        using var inflado = new MemoryStream();
        using (var zlib = new ZLibStream(comprimido, CompressionMode.Decompress))
            zlib.CopyTo(inflado);

        return (largura, altura, Desfiltrar(inflado.ToArray(), largura, altura, profundidade));
    }

    /// <summary>Desfaz os filtros por linha e expande para um byte por pixel.
    ///
    /// Em greyscale de 1 bit cada byte carrega 8 pixels e o "byte anterior" do filtro é o byte
    /// inteiro, não o pixel — por isso `bpp` é 1 aqui. É a pegadinha clássica do formato.</summary>
    private static byte[] Desfiltrar(byte[] cru, int largura, int altura, int profundidade)
    {
        if (profundidade is not (1 or 8))
            throw new InvalidDataException($"Profundidade {profundidade} não suportada.");

        var bytesPorLinha = (largura * profundidade + 7) / 8;
        var bpp = Math.Max(1, profundidade / 8);
        var linhas = new byte[altura][];

        var pos = 0;
        for (var y = 0; y < altura; y++)
        {
            var filtro = cru[pos++];
            var linha = new byte[bytesPorLinha];
            Array.Copy(cru, pos, linha, 0, bytesPorLinha);
            pos += bytesPorLinha;

            var anterior = y > 0 ? linhas[y - 1] : new byte[bytesPorLinha];

            for (var x = 0; x < bytesPorLinha; x++)
            {
                int a = x >= bpp ? linha[x - bpp] : 0;      // esquerda
                int b = anterior[x];                         // acima
                int c = x >= bpp ? anterior[x - bpp] : 0;    // diagonal

                linha[x] = filtro switch
                {
                    0 => linha[x],
                    1 => (byte)(linha[x] + a),
                    2 => (byte)(linha[x] + b),
                    3 => (byte)(linha[x] + (a + b) / 2),
                    4 => (byte)(linha[x] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"Filtro PNG desconhecido: {filtro}.")
                };
            }

            linhas[y] = linha;
        }

        var cinza = new byte[largura * altura];
        for (var y = 0; y < altura; y++)
            for (var x = 0; x < largura; x++)
                cinza[y * largura + x] = profundidade == 8
                    ? linhas[y][x]
                    : (byte)(((linhas[y][x >> 3] >> (7 - (x & 7))) & 1) == 1 ? 255 : 0);

        return cinza;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int LerInt(byte[] b, int i) =>
        (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
}
