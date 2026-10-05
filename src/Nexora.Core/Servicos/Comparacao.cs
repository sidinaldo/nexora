namespace Nexora.Core.Servicos;

/// <summary>Para que lado é BOM este indicador crescer.
///
/// ⚠️ EXISTE PORQUE COR E SETA NÃO SÃO A MESMA COISA. "Cancelado" caindo é uma seta para baixo e uma
/// notícia boa; faturamento caindo é a mesma seta e o contrário. Sem este sentido, a tela pintaria
/// de vermelho a melhor coisa que aconteceu no mês.</summary>
public enum SentidoBom
{
    Sobe,
    Desce
}

/// <summary>Um número contra o mesmo número do período anterior.
///
/// `Tendencia` é o MOVIMENTO (`subiu`/`caiu`/`estavel`) e governa a seta. `Avaliacao` é o JUÍZO
/// (`melhor`/`pior`/`neutro`) e governa a cor. Os dois são campos separados de propósito — ver
/// <see cref="SentidoBom"/>.
///
/// `VariacaoPercentual` é NULO quando não havia nada antes: dividir por zero não é −100% nem
/// infinito, e inventar um dos dois é pior que não responder. A tela diz o que isso significa
/// naquele indicador, que não é a mesma frase em todos ("sem cancelamento em ago" não é "novo").</summary>
public record IndicadorComparativo(
    decimal Atual,
    decimal Anterior,
    decimal VariacaoAbsoluta,
    decimal? VariacaoPercentual,
    string Tendencia,
    string Avaliacao,
    DateOnly AnteriorDe,
    DateOnly AnteriorAte);

/// <summary>===================== A CONTA, NUM LUGAR SÓ =====================
///
/// Classe estática e não serviço injetado: é função pura, não precisa de DI para ser usada nem de
/// contêiner para ser testada. É o mesmo padrão de `RegrasLembrete` e `RegrasNegociacao`, que
/// existem por esta razão — a regra escrita em dois lugares diverge, e este projeto já pagou por
/// isso (o painel dizia 72 e o quadro tinha 69).
/// ==============================================================</summary>
public static class Comparacao
{
    public static IndicadorComparativo De(
        decimal atual, decimal anterior, SentidoBom bom, DateOnly anteriorDe, DateOnly anteriorAte)
    {
        var absoluta = atual - anterior;

        decimal? percentual;
        if (anterior == 0m)
        {
            percentual = null;
        }
        else
        {
            // Sobre o MÓDULO do anterior: com anterior negativo (não acontece hoje em nenhum destes
            // indicadores, mas a conta não deve depender disso) o sinal da variação se inverteria.
            var baseDoCalculo = anterior < 0m ? -anterior : anterior;
            percentual = decimal.Round(absoluta / baseDoCalculo * 100m, 1);
        }

        string tendencia;
        if (absoluta > 0m) tendencia = "subiu";
        else if (absoluta < 0m) tendencia = "caiu";
        else tendencia = "estavel";

        string avaliacao;
        if (absoluta == 0m)
        {
            avaliacao = "neutro";
        }
        else if (bom == SentidoBom.Sobe)
        {
            if (absoluta > 0m) avaliacao = "melhor";
            else avaliacao = "pior";
        }
        else
        {
            if (absoluta > 0m) avaliacao = "pior";
            else avaliacao = "melhor";
        }

        return new IndicadorComparativo(
            atual, anterior, absoluta, percentual, tendencia, avaliacao, anteriorDe, anteriorAte);
    }
}
