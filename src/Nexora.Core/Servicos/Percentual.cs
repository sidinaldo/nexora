namespace Nexora.Core.Servicos;

/// <summary>===================== O PERCENTUAL, CALCULADO NUM LUGAR SÓ (AUD-XX) =====================
///
/// Regra do produto: todo número da tela vem pronto do servidor, e o painel só formata. Os
/// percentuais saem todos daqui, com a mesma convenção:
///
///   · de 0 a 100, e não fração de 0 a 1 — a tela não multiplica nada;
///   · `decimal`, com 2 casas, arredondado com o meio PARA CIMA (`AwayFromZero`). O padrão do .NET
///     é para o par: 0,125 viraria 0,12, e o mesmo número sairia diferente na planilha do cliente;
///   · ⚠️ NULL QUANDO O DENOMINADOR É ZERO. "Não houve o que medir" e "deu 0%" são fatos
///     diferentes: o primeiro vira "—" na tela, o segundo afirma que alguém tentou e não
///     conseguiu. É a mesma regra que já protegia a Evolução e o NPS.
/// ==========================================================================================</summary>
public static class Percentual
{
    public const int Casas = 2;

    /// <summary>`parte` sobre `total`, em pontos de 0 a 100. Null quando `total` é zero.</summary>
    public static decimal? De(decimal parte, decimal total)
    {
        if (total == 0)
        {
            return null;
        }

        return decimal.Round(parte * 100m / total, Casas, MidpointRounding.AwayFromZero);
    }

    /// <summary>===================== AS FATIAS DE UM TODO, SOMANDO 100 =====================
    ///
    /// Arredondar cada fatia sozinha não fecha: três terços dão 33,33 + 33,33 + 33,33 = 99,99, e a
    /// legenda de um gráfico de rosca que soma 99,99% parece conta errada. Aqui é o método do MAIOR
    /// RESTO, na granularidade das 2 casas: cada fatia recebe o valor truncado, e o centésimo que
    /// sobra vai para quem tinha o maior resto (empate: a primeira da lista).
    ///
    /// Sem total (tudo zero), todas as fatias são zero — não há rosca para desenhar.
    /// ==================================================================================</summary>
    public static IReadOnlyList<decimal> Fatias(IReadOnlyList<decimal> partes)
    {
        var resultado = new decimal[partes.Count];

        var total = 0m;
        foreach (var parte in partes)
        {
            total += parte;
        }

        if (total == 0)
        {
            return resultado;
        }

        // Em centésimos de ponto percentual: 100,00% são 10.000 unidades.
        const decimal unidades = 10_000m;

        var restos = new decimal[partes.Count];
        var distribuidas = 0m;

        for (var i = 0; i < partes.Count; i++)
        {
            var exato = partes[i] * unidades / total;
            var inteiro = decimal.Floor(exato);

            resultado[i] = inteiro;
            restos[i] = exato - inteiro;
            distribuidas += inteiro;
        }

        var sobra = (int)(unidades - distribuidas);

        var ordemDosRestos = Enumerable.Range(0, partes.Count)
            .OrderByDescending(i => restos[i])
            .ThenBy(i => i)
            .Take(sobra);

        foreach (var i in ordemDosRestos)
        {
            resultado[i] += 1;
        }

        for (var i = 0; i < resultado.Length; i++)
        {
            resultado[i] = resultado[i] / 100m;
        }

        return resultado;
    }
}
