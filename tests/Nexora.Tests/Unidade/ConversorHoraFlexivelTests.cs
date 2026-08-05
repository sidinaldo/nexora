using System.Text.Json;
using System.Text.Json.Serialization;
using Nexora.Api.Seguranca;
using Nexora.Core.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>O conversor de hora que aceita `HH:mm` e `HH:mm:ss`.
///
/// ===================== O BUG =====================
/// `&lt;input type="time"&gt;` manda `"14:30"`, sem segundos — é o que a especificação do HTML
/// define. O conversor padrão do System.Text.Json exige `HH:mm:ss` e devolve 400, então criar
/// lembrete COM HORA pela tela nunca funcionou.
///
/// O teste desserializa o DTO REAL (`NovoLembrete`), com as MESMAS opções do Program.cs — não um
/// `TimeOnly` solto. É a diferença entre provar que o conversor funciona e provar que o endpoint
/// aceita o payload que o navegador manda.
/// =================================================</summary>
public class ConversorHoraFlexivelTests
{
    /// <summary>As mesmas opções registradas no `Program.cs`. Divergir aqui faria o teste passar
    /// contra uma configuração que não é a que roda.</summary>
    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(),
            new ConversorHoraFlexivel(),
            new ConversorHoraFlexivelNulavel()
        }
    };

    [Theory]
    [InlineData("14:30")]      // o que o navegador manda
    [InlineData("14:30:00")]   // o formato canônico
    public void OS_DOIS_FORMATOS_GRAVAM_O_MESMO_VALOR(string hora)
    {
        var json = $$"""
            { "contatoId": 7, "dataAlvo": "2026-08-06", "horaAlvo": "{{hora}}",
              "titulo": "Ligar de volta" }
            """;

        var novo = JsonSerializer.Deserialize<NovoLembrete>(json, Opcoes)!;

        Assert.Equal(new TimeOnly(14, 30), novo.HoraAlvo);
    }

    [Fact]
    public void SEM_HORA_CONTINUA_SENDO_NULO()
    {
        // Lembrete sem hora marcada é o caso COMUM — "amanhã" sem compromisso de horário. Se o
        // conversor nulável estivesse faltando, este caso quebraria e o outro passaria.
        var json = """
            { "contatoId": 7, "dataAlvo": "2026-08-06", "horaAlvo": null, "titulo": "Retomar" }
            """;

        Assert.Null(JsonSerializer.Deserialize<NovoLembrete>(json, Opcoes)!.HoraAlvo);
    }

    [Fact]
    public void A_SAIDA_E_SEMPRE_HH_MM_SS()
    {
        // Formato único de saída: o cliente não precisa adivinhar, e `input[type=time]` lê os
        // dois. Aceitar dois na entrada não vira devolver dois na saída.
        var dto = new LembreteDto(
            1, 7, "Cliente", null, "manual", "pendente",
            new DateOnly(2026, 8, 6), new TimeOnly(9, 5),
            "Ligar", null, false, null, null, null);

        Assert.Contains("\"09:05:00\"", JsonSerializer.Serialize(dto, Opcoes));
    }

    [Theory]
    [InlineData("\"25:00\"")]      // hora que não existe
    [InlineData("\"14h30\"")]      // formato brasileiro de texto
    [InlineData("\"\"")]           // vazio
    [InlineData("\"2:3\"")]        // sem zero à esquerda
    public void FORMATO_INVALIDO_FALHA_COM_MENSAGEM_QUE_ENSINA(string valorJson)
    {
        // Aceitar os dois formatos não pode virar aceitar qualquer coisa: hora inválida que
        // passasse viraria lembrete disparando no horário errado, em silêncio.
        var json = $$"""
            { "contatoId": 7, "dataAlvo": "2026-08-06", "horaAlvo": {{valorJson}}, "titulo": "X" }
            """;

        var erro = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<NovoLembrete>(json, Opcoes));

        Assert.Contains("HH:mm", erro.Message);
    }
}
