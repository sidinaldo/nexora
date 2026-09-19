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
            [new LinhaImportada(2, "Maria", "5584988887777", null, null, null, situacao, null)]);

        var json = JsonSerializer.Serialize(resumo, ComoAApi);

        // A palavra EXATA que `contatos.html` compara no `@switch`.
        Assert.Contains($"\"situacao\":\"{naTela}\"", json);
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
