using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexora.Core.Tempo;

namespace Nexora.Tests.Unidade;

/// <summary>PARIDADE entre `TempoUtil.MinutosUteis` (C#) e `minutosUteis` (nucleo/semaforo.ts).
///
/// ===================== POR QUE UM ARQUIVO COMPARTILHADO =====================
/// A mesma regra existe duas vezes, em linguagens diferentes, porque a cor do semáforo precisa
/// envelhecer no cliente sem novo fetch. Duas implementações da mesma regra divergem — não é
/// hipótese, é o caminho natural quando alguém mexe em uma só.
///
/// O sintoma da divergência é ruim de rastrear: o Meu Dia ordena pelo cálculo do servidor e a
/// caixa pinta pelo do cliente, então a lista "pula" quando o vendedor troca de tela, sem erro
/// em lugar nenhum.
///
/// Cada lado com seus próprios casos NÃO pega isso: os dois ficam verdes discordando. Um
/// conjunto único, lido pelos dois, pega. O arquivo é `tests/paridade/minutos-uteis.json`, e o
/// espelho deste teste é `semaforo.paridade.spec.ts`.
/// ============================================================================</summary>
public class ParidadeMinutosUteisTests
{
    public sealed record CasoParidade(
        string Nome, string Inicio, string Fim,
        short HoraInicio, short HoraFim, short DiasSemana,
        string[] Feriados, int Esperado);

    private sealed record Arquivo([property: JsonPropertyName("casos")] CasoParidade[] Casos);

    /// <summary>Os casos entram no xUnit UM A UM: com `[Theory]` cada caso vira uma linha de
    /// resultado com o nome dele. Um laço dentro de um `[Fact]` pararia no primeiro erro e
    /// esconderia os outros.</summary>
    public static TheoryData<CasoParidade> Casos()
    {
        var dados = new TheoryData<CasoParidade>();
        foreach (var c in Carregar()) dados.Add(c);
        return dados;
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void C_SHARP_CONCORDA_COM_O_TYPESCRIPT(CasoParidade caso)
    {
        var janela = new JanelaAtendimento(caso.HoraInicio, caso.HoraFim, caso.DiasSemana);
        var feriados = caso.Feriados
            .Select(f => DateOnly.ParseExact(f, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToHashSet();

        var obtido = TempoUtil.MinutosUteis(Instante(caso.Inicio), Instante(caso.Fim), janela, feriados);

        Assert.Equal(caso.Esperado, obtido);
    }

    [Fact]
    public void O_ARQUIVO_DE_CASOS_EXISTE_E_NAO_ESTA_VAZIO()
    {
        // Sem isto, apagar ou mover o JSON deixaria a Theory com ZERO casos — e zero caso passa
        // em silêncio. A paridade morreria sem nenhum teste ficar vermelho.
        Assert.True(Carregar().Length >= 10);
    }

    /// <summary>Hora de PAREDE, sem zona. `DateTimeStyles.None` mantém o Kind Unspecified: o
    /// mesmo 19h50 que o `new Date('...')` do JavaScript enxerga. Converter para UTC aqui
    /// quebraria a paridade em toda máquina fora de UTC.</summary>
    private static DateTime Instante(string s) =>
        DateTime.ParseExact(s, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None);

    private static CasoParidade[] Carregar()
    {
        var json = File.ReadAllText(CaminhoDoArquivo());
        var arquivo = JsonSerializer.Deserialize<Arquivo>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return arquivo?.Casos ?? [];
    }

    /// <summary>Sobe a partir do assembly até achar a raiz do repositório. O caminho relativo
    /// fixo ("../../../../paridade/...") funcionaria hoje e quebraria no dia em que alguém
    /// mudasse o TargetFramework — a profundidade de `bin/Debug/net8.0` mudaria junto.</summary>
    private static string CaminhoDoArquivo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var alvo = Path.Combine(dir.FullName, "tests", "paridade", "minutos-uteis.json");
            if (File.Exists(alvo)) return alvo;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "tests/paridade/minutos-uteis.json não encontrado a partir de " +
            AppContext.BaseDirectory + ". Ele é lido pelo C# E pelo TypeScript; mover um lado " +
            "sem o outro derruba a paridade.");
    }
}
