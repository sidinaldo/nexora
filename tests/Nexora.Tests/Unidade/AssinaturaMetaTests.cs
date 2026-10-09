using System.Text;
using Nexora.Core.Whatsapp;

namespace Nexora.Tests.Unidade;

/// <summary>A ASSINATURA DO WEBHOOK DA META (INT-XX).
///
/// A URL do webhook nao e segredo: sem esta conferencia, qualquer um que a conheca forja mensagem de
/// cliente. O vetor fixo abaixo foi calculado FORA do codigo (Python, `hmac` + `sha256`), para o teste
/// nao ser o `Assinar` conferindo a si mesmo.</summary>
public class AssinaturaMetaTests
{
    private const string Segredo = "segredo-do-app";
    private static readonly byte[] Corpo =
        Encoding.UTF8.GetBytes("""{"object":"whatsapp_business_account","entry":[]}""");

    private const string Esperada = "sha256=f695aba3a0c16a1b6e640394ee3edfebae02dd2d8df7d46fde532cc72d870e25";

    [Fact]
    public void O_FORMATO_E_O_DA_META_HMAC_SHA256_EM_HEX_MINUSCULO()
    {
        Assert.Equal(Esperada, AssinaturaMeta.Assinar(Corpo, Segredo));
        Assert.True(AssinaturaMeta.Valida(Corpo, Esperada, Segredo));
    }

    /// <summary>Um byte a mais no corpo e outra assinatura: e por isso que a porta le os bytes crus,
    /// e nao o JSON relido.</summary>
    [Fact]
    public void CORPO_MEXIDO_NAO_PASSA()
    {
        var mexido = Encoding.UTF8.GetBytes("""{"object":"whatsapp_business_account","entry":[] }""");

        Assert.False(AssinaturaMeta.Valida(mexido, Esperada, Segredo));
    }

    [Fact]
    public void SEGREDO_DE_OUTRO_APP_NAO_PASSA()
    {
        Assert.False(AssinaturaMeta.Valida(Corpo, Esperada, "segredo-de-outro-app"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("f695aba3a0c16a1b6e640394ee3edfebae02dd2d8df7d46fde532cc72d870e25")]   // sem o prefixo
    [InlineData("sha1=f695aba3a0c16a1b6e640394ee3edfebae02dd2d8df7d46fde532cc72d870e25")]
    [InlineData("sha256=nao-e-hex")]
    [InlineData("sha256=f695aba3")]                                                      // curta
    public void CABECALHO_AUSENTE_OU_TORTO_NAO_PASSA(string? cabecalho)
    {
        Assert.False(AssinaturaMeta.Valida(Corpo, cabecalho, Segredo));
    }

    /// <summary>Conexao sem app secret nunca valida — nem uma assinatura feita com segredo vazio.</summary>
    [Fact]
    public void SEGREDO_VAZIO_NUNCA_VALIDA()
    {
        Assert.False(AssinaturaMeta.Valida(Corpo, AssinaturaMeta.Assinar(Corpo, ""), ""));
    }
}
