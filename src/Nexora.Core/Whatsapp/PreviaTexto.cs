using System.Globalization;

namespace Nexora.Core.Whatsapp;

/// <summary>===================== O CORTE DA PREVIA (MID-1) =====================
///
/// `ultima_mensagem_previa` alimenta a lista da caixa de entrada. O corte era
/// `texto[..120]` — 120 UNIDADES DE CODIGO UTF-16, que nao e o mesmo que 120 caracteres.
///
/// Emoji ocupa duas unidades (par substituto). Emoji COMPOSTO ocupa muitas: a familia
/// 👨‍👩‍👧‍👦 sao quatro pares ligados por ZWJ; a bandeira 🇧🇷 sao dois indicadores regionais;
/// tom de pele e um modificador colado no anterior. Cortar no meio de qualquer um deles
/// produz metade de um par substituto — e o que aparece na lista e o losango preto de
/// interrogacao, ou o emoji errado (a bandeira do Brasil virando a letra B).
///
/// O corte agora e por CLUSTER DE GRAFEMA: a unidade que uma pessoa chama de "caractere".
/// `StringInfo.GetTextElementEnumerator` implementa a segmentacao do Unicode e ja conhece
/// ZWJ, indicadores regionais e modificadores.
///
/// UM LUGAR SO, de proposito: a regra estava duplicada no `ProcessadorEventoEvolution` e no
/// `ServicoConversas`, e as duas copias precisariam ser corrigidas — a segunda seria
/// descoberta quando alguem visse emoji quebrado so nas mensagens que ELE mandou.
/// =====================================================================</summary>
public static class PreviaTexto
{
    /// <summary>120 "caracteres" no sentido humano. O numero e o mesmo de antes; o que mudou e
    /// a unidade contada.</summary>
    public const int Tamanho = 120;

    public static string? Cortar(string? texto, int limite = Tamanho)
    {
        if (texto is null) return null;

        // Atalho: se cabe ate no pior caso (uma unidade por grafema), nao ha o que segmentar.
        if (texto.Length <= limite) return texto;

        var enumerador = StringInfo.GetTextElementEnumerator(texto);
        var grafemas = 0;
        var fim = 0;

        while (enumerador.MoveNext())
        {
            if (grafemas == limite) break;
            grafemas++;
            fim = enumerador.ElementIndex + ((string)enumerador.Current).Length;
        }

        return texto[..fim];
    }
}
