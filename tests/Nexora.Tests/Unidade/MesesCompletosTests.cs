using Nexora.Core.Tempo;

namespace Nexora.Tests.Unidade;

/// <summary>Meses de calendário completos (AUD-XX, #28): uma conta só para a Evolução e Leads
/// parados, que usavam meses de 30,44 e de 30 dias.</summary>
public class MesesCompletosTests
{
    [Theory]
    [InlineData("2026-03-14", "2026-05-13", 1)]
    [InlineData("2026-03-14", "2026-05-14", 2)]
    [InlineData("2026-01-31", "2026-02-28", 0)]
    [InlineData("2025-08-06", "2026-08-06", 12)]
    [InlineData("2026-08-06", "2026-08-01", 0)]
    public void CONTA_MESES_DE_CALENDARIO_COMPLETOS(string de, string ate, int esperado)
    {
        Assert.Equal(esperado, MesesCompletos.Entre(DateOnly.Parse(de), DateOnly.Parse(ate)));
    }
}
