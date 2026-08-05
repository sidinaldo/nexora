using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Unidade;

/// <summary>A fábrica de design-time, usada só por `dotnet ef migrations` e `database update`.
///
/// ===================== O QUE ESTE TESTE GUARDA =====================
/// Havia uma connection string PADRÃO aqui, apontando para um banco chamado `nexora`, enquanto o
/// de desenvolvimento é outro. Quem rodasse `dotnet ef database update` sem definir `NEXORA_CONN`
/// criava um banco VAZIO com o schema aplicado, sem erro nenhum, e passava meia hora se
/// perguntando por que a aplicação não via as tabelas novas. Aconteceu neste projeto, e sobrou
/// um banco órfão no ambiente.
///
/// O default foi removido no PI-1, mas ninguém escreveu o teste — e um default silencioso é
/// exatamente o tipo de "conveniência" que alguém readiciona de boa-fé para destravar o próprio
/// dia. O teste é o que transforma a decisão em regra.
/// ===================================================================
///
/// ⚠️ Estes testes MEXEM numa variável de ambiente do processo, que é estado global: por isso a
/// coleção desabilita o paralelismo. Sem isso, um teste que roda em paralelo enxergaria a
/// variável limpa (ou suja) no meio da própria execução.</summary>
[Collection("ambiente")]
public class FabricaDbContextDesignTimeTests
{
    private const string Variavel = "NEXORA_CONN";

    [Fact]
    public void SEM_NEXORA_CONN_FALHA_ALTO_EM_VEZ_DE_USAR_UM_PADRAO()
    {
        var original = Environment.GetEnvironmentVariable(Variavel);
        try
        {
            Environment.SetEnvironmentVariable(Variavel, null);

            var erro = Assert.Throws<InvalidOperationException>(
                () => new FabricaDbContextDesignTime().CreateDbContext([]));

            // A mensagem tem que ENSINAR o caminho, não só reclamar: quem esbarra nela está no
            // meio de um comando de migration e precisa saber o que fazer, não abrir o código.
            Assert.Contains("NEXORA_CONN", erro.Message);
            Assert.Contains("user-secrets list", erro.Message);
            Assert.Contains("database update", erro.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variavel, original);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void STRING_VAZIA_TAMBEM_FALHA(string valor)
    {
        // `IsNullOrWhiteSpace`, não `IsNullOrEmpty`: `export NEXORA_CONN=` deixa a variável
        // DEFINIDA e vazia, e um teste só do caso nulo não pegaria isso.
        var original = Environment.GetEnvironmentVariable(Variavel);
        try
        {
            Environment.SetEnvironmentVariable(Variavel, valor);
            Assert.Throws<InvalidOperationException>(
                () => new FabricaDbContextDesignTime().CreateDbContext([]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variavel, original);
        }
    }

    [Fact]
    public void COM_A_VARIAVEL_DEFINIDA_MONTA_O_CONTEXTO()
    {
        // Não abre conexão — `CreateDbContext` só monta as opções. O que se prova aqui é que a
        // fábrica não tem OUTRA exigência escondida além da connection string.
        var original = Environment.GetEnvironmentVariable(Variavel);
        try
        {
            Environment.SetEnvironmentVariable(
                Variavel, "Host=localhost;Database=nao_conecta;Username=x;Password=y");

            using var db = new FabricaDbContextDesignTime().CreateDbContext([]);
            Assert.NotNull(db);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variavel, original);
        }
    }
}

/// <summary>Serializa os testes que mexem em variável de ambiente — estado global do processo.</summary>
[CollectionDefinition("ambiente", DisableParallelization = true)]
public sealed class ColecaoAmbiente;
