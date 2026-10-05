using System.Text.Json;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>===================== O TETO DE FUNIS É UMA GARANTIA DE DUAS PONTAS =====================
///
/// `ServicoPipelines.MaximoPipelines` não é uma opinião de produto: é o número que faz a barra
/// lateral caber em 768px sem rolar. A outra ponta da garantia é `lateral.spec.ts`, que monta o
/// teto de propósito e MEDE.
///
/// ⚠️ AS DUAS PONTAS NÃO SE ENXERGAM. Uma é um `const` em C#, a outra é um literal em TypeScript —
/// e o número já caiu TRÊS vezes (8 → 6 → 5 → 4), sempre por causa de um item fixo novo no menu,
/// nunca por causa de funil. Nas três, quem mudou um lado tinha de lembrar do outro.
///
/// Lembrar não é mecanismo. Este teste e o espelho dele em `lateral.spec.ts` leem o MESMO
/// `tests/paridade/limites-da-barra.json`, no molde que o projeto já usa para `minutos-uteis.json`:
///
///   · subir o `const` sem mexer no JSON  → este teste fica vermelho;
///   · mexer no JSON sem subir o `const`  → este teste fica vermelho;
///   · mexer nos dois sem a barra caber   → `lateral.spec.ts` fica vermelho, dizendo os pixels.
///
/// É o laço que faltava: antes do EVO-1 não havia teste NENHUM sobre o teto, e subi-lo de volta
/// num refactor deixaria tudo verde — o sintoma seria a barra rolando no cliente com muitos
/// funis, descoberto por ele.
/// ============================================================================================</summary>
public class LimitesDaBarraTests
{
    private sealed record Limites(int MaximoPipelines);

    [Fact]
    public void O_TETO_DE_FUNIS_E_O_MESMO_QUE_A_BARRA_LATERAL_MEDE()
    {
        var caminho = CaminhoDoArquivo();
        var lido = JsonSerializer.Deserialize<Limites>(
            File.ReadAllText(caminho),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(lido);

        // A ordem dos argumentos é imposta pelo analisador (xUnit2000): a constante vai em
        // `expected`. A asserção é simétrica — qualquer um dos dois lados que mude deixa isto
        // vermelho, que é o ponto.
        Assert.Equal(ServicoPipelines.MaximoPipelines, lido!.MaximoPipelines);
    }

    /// <summary>Sobe a partir do assembly até achar a raiz do repositório — mesma razão do
    /// `ParidadeMinutosUteisTests`: caminho relativo fixo quebraria no dia em que o
    /// TargetFramework mudasse, porque a profundidade de `bin/Debug/net8.0` muda junto.</summary>
    private static string CaminhoDoArquivo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var alvo = Path.Combine(dir.FullName, "tests", "paridade", "limites-da-barra.json");
            if (File.Exists(alvo)) return alvo;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "tests/paridade/limites-da-barra.json não encontrado a partir de " +
            AppContext.BaseDirectory + ". Ele é lido pelo C# E pelo TypeScript; mover um lado " +
            "sem o outro desfaz o laço entre o teto de funis e a medição da barra.");
    }
}
