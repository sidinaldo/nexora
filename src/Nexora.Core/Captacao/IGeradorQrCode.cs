namespace Nexora.Core.Captacao;

/// <summary>Desenha um QR Code. Interface no Core, biblioteca na Infra — mesma disciplina de
/// `IClienteWhatsApp` e `IArmazenamentoMidia`: o domínio sabe que existe "um jeito de virar QR",
/// não qual pacote faz isso.
///
/// ===================== NADA DE SERVIÇO EXTERNO =====================
/// Não existe (e não pode passar a existir) uma implementação que chame API de terceiro. Um QR
/// gerado por serviço externo põe o número de WhatsApp do cliente no servidor de outra pessoa,
/// e transforma a impressão de um panfleto em dependência de disponibilidade alheia.
/// ===================================================================</summary>
public interface IGeradorQrCode
{
    /// <summary>SVG. É o formato que IMPORTA: panfleto e placa são impressos em tamanho que
    /// nenhum PNG de tela aguenta, e QR pixelado não escaneia.</summary>
    string Svg(string conteudo);

    /// <summary>PNG, para colar em post, story ou apresentação — onde SVG não entra.</summary>
    byte[] Png(string conteudo, int pixelsPorModulo = 12);
}
