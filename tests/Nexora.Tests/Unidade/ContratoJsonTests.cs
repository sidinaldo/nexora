using System.Text.Json;
using Nexora.Api.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>O QUE SAI PELO FIO, com a configuração de JSON que a API usa de verdade.
///
/// ⚠️ ESTE ARQUIVO NASCE DE UM DEFEITO QUE OS TESTES DO PAINEL ESCONDERAM. A prévia de
/// importação marcava TODA linha como "fora": a API mandava `"situacao":"Nova"` e a tela comparava
/// com `'nova'`. Os specs do Angular passavam porque o mock trazia `'nova'` — eles testavam o
/// contrato que a tela ESPERAVA, não o que a API MANDAVA.
///
/// O teste que falta nesse tipo de defeito é deste lado: serializar com `OpcoesJson.Configurar`,
/// a MESMA função que o `Program.cs` chama, e conferir a palavra que chega à tela.</summary>
public class ContratoJsonTests
{
    /// <summary>`JsonSerializerDefaults.Web` é o ponto de partida do ASP.NET Core (camelCase nas
    /// propriedades); `OpcoesJson.Configurar` é o que o `Program.cs` acrescenta por cima.</summary>
    private static readonly JsonSerializerOptions ComoAApi = Criar();

    private static JsonSerializerOptions Criar()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        OpcoesJson.Configurar(o);
        return o;
    }

    [Theory]
    [InlineData(SituacaoLinha.Nova, "nova")]
    [InlineData(SituacaoLinha.Repetida, "repetida")]
    [InlineData(SituacaoLinha.Invalida, "invalida")]
    public void A_SITUACAO_DA_LINHA_CHEGA_EM_MINUSCULA(SituacaoLinha situacao, string naTela)
    {
        var resumo = new ResumoImportacao(1, 1, 0, 0,
            [new LinhaImportada(2, "Maria", "5584988887777", null, null, null, situacao, null)],
            new AvisoIntegracoes(false, false));

        var json = JsonSerializer.Serialize(resumo, ComoAApi);

        // A palavra EXATA que `contatos.html` compara no `@switch`.
        Assert.Contains($"\"situacao\":\"{naTela}\"", json);
    }

    /// <summary>O selo do contato é decidido no servidor, e a tela compara a PALAVRA no `@switch`.
    /// `sem_negocio` com sublinhado — o que `EnumMinusculo` produz —, e não o `sem-negocio` com
    /// hífen que a regra antiga do painel usava.</summary>
    [Theory]
    [InlineData(Nexora.Core.Entidades.SituacaoContato.SemNegocio, "sem_negocio")]
    [InlineData(Nexora.Core.Entidades.SituacaoContato.Aberto, "aberto")]
    [InlineData(Nexora.Core.Entidades.SituacaoContato.Ganho, "ganho")]
    [InlineData(Nexora.Core.Entidades.SituacaoContato.Perdido, "perdido")]
    public void A_SITUACAO_DO_CONTATO_CHEGA_COM_A_PALAVRA_QUE_A_TELA_COMPARA(
        Nexora.Core.Entidades.SituacaoContato situacao, string naTela)
    {
        var json = JsonSerializer.Serialize(
            new ContatoResumo(1, "Maria", "5584988887777", null, "manual", null, null, [], 0m,
                null, null, null, null, null, situacao, DateTime.UtcNow, null, null, 0),
            ComoAApi);

        Assert.Contains($"\"situacao\":\"{naTela}\"", json);
    }

    /// <summary>A caixinha "Avisar minhas integrações" é DECIDIDA no servidor e só desenhada na tela
    /// — então os dois nomes que a tela lê (`p.aviso.disponivel`, `p.aviso.marcadoPorPadrao`) são
    /// contrato, e mudá-los esconderia a caixinha sem erro nenhum.</summary>
    [Fact]
    public void O_AVISO_DA_IMPORTACAO_CHEGA_COM_OS_NOMES_QUE_A_TELA_LE()
    {
        var json = JsonSerializer.Serialize(
            new ResumoImportacao(0, 0, 0, 0, [], new AvisoIntegracoes(true, false)), ComoAApi);

        Assert.Contains("\"aviso\":{\"disponivel\":true,\"marcadoPorPadrao\":false}", json);
    }

    /// <summary>O mapeamento da importação da Meta (INT-XX) vai e VOLTA pela tela: o servidor
    /// sugere, o dono ajusta, e o que ele ajustou volta ao servidor. O rótulo tem de ser o mesmo nos
    /// dois sentidos, senão a tela devolve um campo que o servidor não reconhece.</summary>
    [Fact]
    public void O_CAMPO_DO_MAPEAMENTO_VAI_E_VOLTA_EM_SNAKE_CASE()
    {
        var ida = JsonSerializer.Serialize(
            new ColunaMapeada("id", Nexora.Core.LeadAds.CampoImportacao.MetaLeadId), ComoAApi);
        Assert.Contains("\"campo\":\"meta_lead_id\"", ida);

        var volta = JsonSerializer.Deserialize<ColunaMapeada>(
            "{\"coluna\":\"Qual seu orçamento?\",\"campo\":\"observacoes\"}", ComoAApi)!;
        Assert.Equal(Nexora.Core.LeadAds.CampoImportacao.Observacoes, volta.Campo);
        Assert.Equal("Qual seu orçamento?", volta.Coluna);
    }

    /// <summary>E o resto da API continua como estava: o conversor global segue mandando o enum
    /// com o nome do C#. `EnumMinusculo` é por propriedade, de propósito — trocar a política
    /// global mudaria o contrato de TODOS os endpoints de uma vez, inclusive para quem integra.</summary>
    [Fact]
    public void O_CONVERSOR_GLOBAL_NAO_MUDOU_PARA_OS_OUTROS_ENUMS()
    {
        var json = JsonSerializer.Serialize(new { origem = Nexora.Core.Entidades.OrigemLead.Indicacao }, ComoAApi);

        Assert.Contains("\"origem\":\"Indicacao\"", json);
    }
}
