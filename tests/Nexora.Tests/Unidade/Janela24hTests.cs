using Nexora.Core.Entidades;
using Nexora.Core.Whatsapp;

namespace Nexora.Tests.Unidade;

/// <summary>A JANELA DE 24H DA META (INT-XX).
///
/// A regra que decide se texto livre pode sair pela Cloud API. Errar para mais faz a Meta recusar
/// o envio (131047) depois de o vendedor escrever; errar para menos trava o vendedor a toa.</summary>
public class Janela24hTests
{
    private static readonly DateTime Entrada = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FECHA_24H_DEPOIS_DA_ULTIMA_ENTRADA_E_AVISA_2H_ANTES()
    {
        Assert.Equal(Entrada.AddHours(24), Janela24h.FechaEm(Entrada));
        Assert.Equal(Entrada.AddHours(22), Janela24h.AvisoEm(Entrada));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(23 * 60 + 59, true)]
    [InlineData(24 * 60, false)]
    [InlineData(30 * 60, false)]
    public void ABERTA_ATE_O_INSTANTE_EM_QUE_FECHA(int minutosDepois, bool aberta)
    {
        Assert.Equal(aberta, Janela24h.Aberta(Entrada, Entrada.AddMinutes(minutosDepois)));
    }

    /// <summary>Cliente que nunca escreveu para o numero: a janela nunca abriu.</summary>
    [Fact]
    public void SEM_ENTRADA_A_JANELA_ESTA_FECHADA()
    {
        Assert.Null(Janela24h.FechaEm(null));
        Assert.Null(Janela24h.AvisoEm(null));
        Assert.False(Janela24h.Aberta(null, Entrada));
        Assert.False(Janela24h.PermiteTextoLivre(CanalWhatsapp.CloudApi, null, Entrada));
    }

    /// <summary>⚠️ A EVOLUTION NUNCA BLOQUEIA. Na Evolution a janela e so informacao de tempo de
    /// resposta — bloquear ali seria inventar uma restricao que o canal nao tem.</summary>
    [Fact]
    public void SO_A_CLOUD_API_PRENDE_O_TEXTO_LIVRE_A_JANELA()
    {
        var doisDiasDepois = Entrada.AddDays(2);

        Assert.True(Janela24h.PermiteTextoLivre(CanalWhatsapp.Evolution, Entrada, doisDiasDepois));
        Assert.True(Janela24h.PermiteTextoLivre(CanalWhatsapp.Evolution, null, doisDiasDepois));
        Assert.False(Janela24h.PermiteTextoLivre(CanalWhatsapp.CloudApi, Entrada, doisDiasDepois));
        Assert.True(Janela24h.PermiteTextoLivre(CanalWhatsapp.CloudApi, Entrada, Entrada.AddHours(1)));
    }
}
