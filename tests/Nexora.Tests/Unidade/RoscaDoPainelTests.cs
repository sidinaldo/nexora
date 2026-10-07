using Nexora.Infra.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>A rosca das origens do painel inicial, montada no servidor (AUD-XX). Era a tela que
/// somava por origem, cortava as maiores, juntava o resto em "Outros" e ajustava os percentuais.</summary>
public class RoscaDoPainelTests
{
    [Fact]
    public void SOMA_POR_ORIGEM_E_AS_CAMPANHAS_DESCEM_COMO_SUB_LINHA()
    {
        var (total, fatias) = ServicoDashboard.Rosca([
            ("instagram", "Promoção de Julho", 3),
            ("instagram", null, 2),
            ("instagram", "Black Friday", 5),
            ("whatsapp", null, 4)
        ]);

        Assert.Equal(14, total);

        var instagram = fatias[0];
        Assert.Equal("instagram", instagram.Origem);
        Assert.Equal(10, instagram.Leads);
        // Só campanha NOMEADA vira sub-linha, da maior para a menor.
        Assert.Equal(["Black Friday", "Promoção de Julho"], instagram.Campanhas.Select(c => c.Nome));

        Assert.Equal("whatsapp", fatias[1].Origem);
        Assert.Empty(fatias[1].Campanhas);
    }

    /// <summary>Seis tons de verde é o que o olho distingue: passando disso, as cinco maiores ficam
    /// e o resto vira UMA fatia, sempre a última.</summary>
    [Fact]
    public void PASSANDO_DE_SEIS_O_RESTO_VIRA_UMA_FATIA_AGRUPADA_NO_FIM()
    {
        var (total, fatias) = ServicoDashboard.Rosca([
            ("whatsapp", null, 30), ("instagram", null, 20), ("site", null, 15),
            ("google", null, 10), ("facebook", null, 8), ("indicacao", null, 5), ("manual", null, 2)
        ]);

        Assert.Equal(90, total);
        Assert.Equal(ServicoDashboard.MaximoDeFatias, fatias.Count);
        Assert.All(fatias.Take(5), f => Assert.False(f.Agrupada));

        var outros = fatias[^1];
        Assert.True(outros.Agrupada);
        Assert.Equal(ServicoDashboard.OrigemAgrupada, outros.Origem);
        Assert.Equal(7, outros.Leads);   // indicação 5 + manual 2
        Assert.Empty(outros.Campanhas);
    }

    [Fact]
    public void ATE_SEIS_ORIGENS_NAO_HA_AGRUPADA()
    {
        var (_, fatias) = ServicoDashboard.Rosca([
            ("whatsapp", null, 30), ("instagram", null, 20), ("site", null, 15),
            ("google", null, 10), ("facebook", null, 8), ("indicacao", null, 5)
        ]);

        Assert.Equal(6, fatias.Count);
        Assert.DoesNotContain(fatias, f => f.Agrupada);
    }

    /// <summary>⚠️ OS PERCENTUAIS SOMAM 100. Três terços arredondados um a um dariam 99,99, e a
    /// legenda "não fecha" — o ajuste era a tela que fazia, empurrando a diferença para a última
    /// fatia.</summary>
    [Fact]
    public void OS_PERCENTUAIS_SOMAM_100()
    {
        var (_, fatias) = ServicoDashboard.Rosca([
            ("whatsapp", null, 1), ("instagram", null, 1), ("site", null, 1)
        ]);

        Assert.Equal(100m, fatias.Sum(f => f.Percentual));
    }

    [Fact]
    public void SEM_LEADS_NAO_HA_FATIA()
    {
        var (total, fatias) = ServicoDashboard.Rosca([]);

        Assert.Equal(0, total);
        Assert.Empty(fatias);
    }
}
