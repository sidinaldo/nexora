using Nexora.Core.LeadAds;

namespace Nexora.Tests.Unidade;

/// <summary>AS REGRAS DO ARQUIVO DO GERENCIADOR DE LEADS (INT-XX).
///
/// ⚠️ ESCRITOS SEM UM EXPORT REAL NA MÃO. O cabeçalho vem do spec; o prefixo nos ids e o `p:` no
/// telefone vêm de memória do formato da Meta, e por isso cada teste de prefixo tem o par "sem
/// prefixo" — as duas formas precisam funcionar até alguém conferir com um arquivo de verdade.</summary>
public class MapeamentoMetaTests
{
    // ==================================================================== o mapeamento automático
    [Fact]
    public void O_CABECALHO_DO_GERENCIADOR_DE_LEADS_SE_MAPEIA_SOZINHO()
    {
        var cabecalho = new[]
        {
            "id", "created_time", "ad_id", "ad_name", "adset_id", "adset_name",
            "campaign_id", "campaign_name", "form_id", "form_name", "is_organic", "platform",
            "full_name", "phone_number", "email", "Qual seu orçamento?"
        };

        var m = MapeamentoMeta.Sugerir(cabecalho).ToDictionary(x => x.Coluna, x => x.Campo);

        Assert.Equal(CampoImportacao.MetaLeadId, m["id"]);
        Assert.Equal(CampoImportacao.CriadoEm, m["created_time"]);
        Assert.Equal(CampoImportacao.MetaAdId, m["ad_id"]);
        Assert.Equal(CampoImportacao.MetaCampaignId, m["campaign_id"]);
        Assert.Equal(CampoImportacao.MetaFormId, m["form_id"]);
        Assert.Equal(CampoImportacao.OrigemDetalhe, m["campaign_name"]);
        Assert.Equal(CampoImportacao.Nome, m["full_name"]);
        Assert.Equal(CampoImportacao.Telefone, m["phone_number"]);
        Assert.Equal(CampoImportacao.Email, m["email"]);

        // O que ninguém reconhece fica para o dono decidir — inclusive a pergunta do formulário.
        Assert.Equal(CampoImportacao.Ignorar, m["platform"]);
        Assert.Equal(CampoImportacao.Ignorar, m["is_organic"]);
        Assert.Equal(CampoImportacao.Ignorar, m["Qual seu orçamento?"]);
    }

    /// <summary>E o arquivo do cliente, com os nomes em português.</summary>
    [Theory]
    [InlineData("Nome", CampoImportacao.Nome)]
    [InlineData("Nome Completo", CampoImportacao.Nome)]
    [InlineData("TELEFONE", CampoImportacao.Telefone)]
    [InlineData("Celular", CampoImportacao.Telefone)]
    [InlineData("WhatsApp", CampoImportacao.Telefone)]
    [InlineData("E-mail", CampoImportacao.Email)]
    public void NOMES_EM_PORTUGUES_TAMBEM(string coluna, CampoImportacao esperado) =>
        Assert.Equal(esperado, MapeamentoMeta.Sugerir([coluna])[0].Campo);

    /// <summary>⚠️ CAMPO ÚNICO É SUGERIDO UMA VEZ SÓ. Com `phone_number` e `telefone` no mesmo
    /// arquivo, sugerir os dois como telefone faria a prévia escolher um em silêncio.</summary>
    [Fact]
    public void DUAS_COLUNAS_DE_TELEFONE_NAO_VIRAM_DOIS_TELEFONES()
    {
        var m = MapeamentoMeta.Sugerir(["phone_number", "telefone"]);

        Assert.Equal(CampoImportacao.Telefone, m[0].Campo);
        Assert.Equal(CampoImportacao.Ignorar, m[1].Campo);
    }

    // ==================================================================== o prefixo nos ids
    [Theory]
    [InlineData("l:1234567890123456", "1234567890123456")]   // lead
    [InlineData("ag:120201234567890", "120201234567890")]    // anúncio
    [InlineData("c:120209876543210", "120209876543210")]     // campanha
    [InlineData("f:987654321", "987654321")]                 // formulário
    [InlineData("1234567890123456", "1234567890123456")]     // ⚠️ SEM prefixo passa intacto
    [InlineData("  l:123  ", "123")]
    [InlineData("", null)]
    [InlineData("l:", null)]
    public void O_PREFIXO_DE_TIPO_SAI_DO_ID(string bruto, string? esperado) =>
        Assert.Equal(esperado, MapeamentoMeta.IdLimpo(bruto));

    // ==================================================================== a data
    [Theory]
    // o formato da Meta: fuso sem dois-pontos
    [InlineData("2026-09-10T14:32:11+0000", "2026-09-10T14:32:11Z")]
    [InlineData("2026-09-10T14:32:11-0300", "2026-09-10T17:32:11Z")]
    // ISO comum
    [InlineData("2026-09-10T14:32:11+00:00", "2026-09-10T14:32:11Z")]
    [InlineData("2026-09-10T14:32:11Z", "2026-09-10T14:32:11Z")]
    // ⚠️ o que sobra depois que o Excel em português reescreve o arquivo: horário de Brasília
    [InlineData("10/09/2026 14:32", "2026-09-10T17:32:00Z")]
    public void CREATED_TIME_VIRA_UTC(string bruto, string esperadoUtc) =>
        Assert.Equal(DateTime.Parse(esperadoUtc).ToUniversalTime(), MapeamentoMeta.Data(bruto));

    /// <summary>Data ilegível NÃO recusa a linha: o contato entra com a data de hoje, que é o que
    /// acontecia antes de existir esta coluna. Perder o lead por causa da data seria pior.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("ontem")]
    [InlineData("99/99/9999")]
    public void DATA_ILEGIVEL_VOLTA_NULA(string bruto) =>
        Assert.Null(MapeamentoMeta.Data(bruto));
}
