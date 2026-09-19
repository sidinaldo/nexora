using Nexora.Core.Entidades;

namespace Nexora.Tests.Unidade;

/// <summary>Texto → origem, casando só pelo nome. Ver `OrigemLeadTexto` para o defeito: `Enum.TryParse`
/// aceitava "2" (virava WhatsApp), "15" (valor inexistente, 500 no `SaveChanges`) e "4,8" (flags).</summary>
public class OrigemLeadTextoTests
{
    [Theory]
    // ---------- os nomes, do jeito que a planilha e a tela escrevem
    [InlineData("indicacao", OrigemLead.Indicacao)]
    [InlineData("Indicação", OrigemLead.Indicacao)]
    [InlineData("  INSTAGRAM  ", OrigemLead.Instagram)]
    [InlineData("whatsapp", OrigemLead.Whatsapp)]
    [InlineData("meta_ads", OrigemLead.MetaAds)]
    [InlineData("Meta Ads", OrigemLead.MetaAds)]
    // ---------- ⚠️ O DEFEITO: número e lista de flags NÃO são origem
    [InlineData("2", OrigemLead.Manual)]
    [InlineData("15", OrigemLead.Manual)]
    [InlineData("4,8", OrigemLead.Manual)]
    [InlineData("-1", OrigemLead.Manual)]
    // ---------- texto livre que não é nome nosso
    [InlineData("panfleto da esquina", OrigemLead.Manual)]
    [InlineData("", OrigemLead.Manual)]
    [InlineData(null, OrigemLead.Manual)]
    public void CASA_SO_PELO_NOME(string? texto, OrigemLead esperado) =>
        Assert.Equal(esperado, OrigemLeadTexto.Ler(texto));

    /// <summary>E nunca devolve um valor que o enum não tem — é esse que o Npgsql não sabe gravar.</summary>
    [Theory]
    [InlineData("15")]
    [InlineData("4,8")]
    [InlineData("999999")]
    public void NUNCA_DEVOLVE_VALOR_FORA_DO_ENUM(string texto) =>
        Assert.True(Enum.IsDefined(OrigemLeadTexto.Ler(texto)));
}
