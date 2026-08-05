using System.Globalization;
using Nexora.Tests.Unidade;

namespace Nexora.Tests.Integracao;

/// <summary>PARIDADE do TERCEIRO lado: a função `nexora_minutos_uteis` no Postgres.
///
/// ===================== POR QUE EXISTE UMA TERCEIRA CÓPIA =====================
/// A regra de minutos úteis já vivia em `TempoUtil.MinutosUteis` (C#, para o Meu Dia) e em
/// `minutosUteis` (TypeScript, para a cor do semáforo envelhecer sem novo fetch). A série
/// temporal exigiu a terceira: a média de tempo de resposta de um mês inteiro não pode subir do
/// banco linha a linha para ser descontada no C# — isso é agregar em memória, que é justamente
/// o que o projeto proíbe.
///
/// Três implementações da mesma regra é dívida. Ela é paga aqui: os MESMOS casos de
/// `tests/paridade/minutos-uteis.json` que rodam no C# e no TypeScript rodam contra o SQL. Mexer
/// num lado só deixa este arquivo vermelho.
/// =============================================================================
///
/// FUSO: os casos são hora de PAREDE, sem zona. Aqui eles entram como UTC e a função é chamada
/// com `p_fuso = 'UTC'`, então a hora de parede atravessa intacta — igual ao `Kind Unspecified`
/// do C# e ao `new Date('...')` sem sufixo do JavaScript. Passar o fuso da empresa aqui
/// transformaria um teste de REGRA num teste de conversão de fuso.</summary>
[Collection("banco")]
public class ParidadeMinutosUteisSqlDbTests(BancoTeste banco)
{
    public static TheoryData<ParidadeMinutosUteisTests.CasoParidade> Casos() =>
        ParidadeMinutosUteisTests.Casos();

    [Theory]
    [MemberData(nameof(Casos))]
    public async Task O_SQL_CONCORDA_COM_O_C_SHARP_E_COM_O_TYPESCRIPT(
        ParidadeMinutosUteisTests.CasoParidade caso)
    {
        await using var conn = await banco.Fonte.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();

        cmd.CommandText =
            "SELECT nexora_minutos_uteis($1, $2, 'UTC', $3, $4, $5, $6)";

        cmd.Parameters.Add(new() { Value = Instante(caso.Inicio) });
        cmd.Parameters.Add(new() { Value = Instante(caso.Fim) });
        cmd.Parameters.Add(new() { Value = (int)caso.HoraInicio });
        cmd.Parameters.Add(new() { Value = (int)caso.HoraFim });
        cmd.Parameters.Add(new() { Value = (int)caso.DiasSemana });
        cmd.Parameters.Add(new()
        {
            Value = caso.Feriados
                .Select(f => DateOnly.ParseExact(f, "yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToArray()
        });

        var obtido = (int)(await cmd.ExecuteScalarAsync())!;

        Assert.Equal(caso.Esperado, obtido);
    }

    [Fact]
    public async Task A_TRAVA_DE_400_ITERACOES_EXISTE_NO_SQL_TAMBEM()
    {
        // Bitmask zerado = nenhum dia permitido. Sem a trava o laço não termina — e travar
        // DENTRO do banco é muito pior do que travar num processo que se reinicia: a conexão
        // fica presa segurando transação. Este teste terminar já é o resultado.
        await using var conn = await banco.Fonte.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT nexora_minutos_uteis(
                '2026-01-01T00:00:00Z'::timestamptz,
                '2030-01-01T00:00:00Z'::timestamptz,
                'UTC', 8, 20, 0, ARRAY[]::date[])
            """;

        Assert.Equal(0, (int)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Nulo_entra_nulo_sai()
    {
        // Conversa sem resposta tem `fim` NULL. A função devolver 0 ali seria pior que devolver
        // NULL: 0 minuto entra na média como "respondeu na hora", e a métrica passa a premiar
        // justamente quem não respondeu.
        await using var conn = await banco.Fonte.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT nexora_minutos_uteis(
                '2026-08-06T10:00:00Z'::timestamptz, NULL,
                'UTC', 8, 20, 126, ARRAY[]::date[])
            """;

        Assert.Equal(DBNull.Value, await cmd.ExecuteScalarAsync());
    }

    private static DateTime Instante(string s) =>
        DateTime.SpecifyKind(
            DateTime.ParseExact(s, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeKind.Utc);
}
