namespace Nexora.Core.Servicos;

/// <summary>===================== A MÉDIA MÓVEL DOS GRÁFICOS DE LINHA (AUD-XX, #23) =====================
/// Era calculada no componente do gráfico, sobre qualquer série que ele recebesse. Agora sai daqui,
/// e a tela só desenha.
///
/// A regra é a que a tela usava: janela de `Janela` pontos, PARCIAL no começo (o terceiro ponto é a
/// média dos três primeiros), e nada quando a série tem menos pontos que a janela — sete pontos é o
/// mínimo para a linha suavizar alguma coisa. Duas casas, meio para cima.
/// ==========================================================================================</summary>
public static class MediaMovel
{
    public const int Janela = 7;

    public static IReadOnlyList<decimal?> De(IReadOnlyList<decimal> valores)
    {
        var medias = new decimal?[valores.Count];
        if (valores.Count < Janela)
        {
            return medias;
        }

        for (var i = 0; i < valores.Count; i++)
        {
            var inicio = Math.Max(0, i - Janela + 1);
            var soma = 0m;
            for (var k = inicio; k <= i; k++)
            {
                soma += valores[k];
            }

            medias[i] = decimal.Round(soma / (i - inicio + 1), 2, MidpointRounding.AwayFromZero);
        }

        return medias;
    }
}
