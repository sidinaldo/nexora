using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>UMA RODADA DE AUTOMACOES POR VEZ (BUG-XX). A rodada passa de hora em hora; uma que
/// demorou mais de uma hora, ou uma segunda instancia da API, tem de pular a hora.</summary>
[Collection("banco")]
public class TravaDaRodadaDbTests(BancoTeste banco)
{
    [Fact]
    public async Task COM_A_TRAVA_PRESA_A_SEGUNDA_RODADA_PULA_E_DEPOIS_PEGA()
    {
        using var primeiro = banco.NovoContexto(new ContextoMutavel());
        using var segundo = banco.NovoContexto(new ContextoMutavel());

        await using (var trava = await TravaDaRodada.TentarAsync(primeiro, default))
        {
            Assert.NotNull(trava);
            Assert.Null(await TravaDaRodada.TentarAsync(segundo, default));
        }

        await using var depois = await TravaDaRodada.TentarAsync(segundo, default);
        Assert.NotNull(depois);
    }
}
