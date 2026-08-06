using Nexora.Core.Captacao;
using QRCoder;

namespace Nexora.Infra.Captacao;

/// <summary>Desenha o QR com a QRCoder.
///
/// ===================== POR QUE ESTA BIBLIOTECA =====================
/// Codificar QR à mão é Reed-Solomon sobre GF(256), oito máscaras, tabela de versões e modos de
/// codificação — algumas centenas de linhas cujo modo de falha é um código que PARECE certo na
/// tela e não escaneia no papel. Não é lugar para código próprio.
///
/// A QRCoder foi escolhida por uma razão concreta e não por popularidade: `PngByteQRCode` e
/// `SvgQRCode` são 100% gerenciados. As alternativas comuns desenham via `System.Drawing.Common`
/// ou SkiaSharp — a primeira exige `libgdiplus` fora do Windows (e a API roda em contêiner
/// Linux), a segunda arrasta binário nativo por arquitetura. Aqui não entra nada nativo.
///
/// `QRCodeGenerator` é `IDisposable` e NÃO é thread-safe entre chamadas concorrentes; por isso é
/// criado por chamada, e não guardado em campo de um serviço singleton. Gerar QR não é caminho
/// quente — acontece quando alguém abre a tela de canais.
/// ===================================================================</summary>
public class GeradorQrCoder : IGeradorQrCode
{
    /// <summary>Correção de erro Q (~25%).
    ///
    /// Não é o padrão M por um motivo físico: este QR vai para panfleto, adesivo de balcão e
    /// cartão — superfícies que dobram, sujam e recebem dedo em cima. Q recupera o dobro de M e
    /// custa ~15% a mais de área, que num papel não faz diferença.
    ///
    /// H (30%) não entra: cresceria a matriz o bastante para exigir mais espaço impresso sem
    /// ganho prático nesse tipo de mídia.</summary>
    private const QRCodeGenerator.ECCLevel Correcao = QRCodeGenerator.ECCLevel.Q;

    public string Svg(string conteudo)
    {
        using var gerador = new QRCodeGenerator();
        using var dados = gerador.CreateQrCode(conteudo, Correcao);

        // `pixelsPerModule: 10` no SVG vira a unidade do viewBox, não pixel de tela — o SVG é
        // vetorial e escala sozinho. O que ele fixa é a proporção da quiet zone.
        return new SvgQRCode(dados).GetGraphic(10);
    }

    public byte[] Png(string conteudo, int pixelsPorModulo = 12)
    {
        using var gerador = new QRCodeGenerator();
        using var dados = gerador.CreateQrCode(conteudo, Correcao);
        return new PngByteQRCode(dados).GetGraphic(pixelsPorModulo);
    }
}
