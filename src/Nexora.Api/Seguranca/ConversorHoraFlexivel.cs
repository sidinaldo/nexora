using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexora.Api.Seguranca;

/// <summary>Lê `TimeOnly` aceitando `HH:mm` E `HH:mm:ss`.
///
/// ===================== O BUG QUE ISTO CONSERTA =====================
/// `&lt;input type="time"&gt;` manda `"14:30"` — sem segundos, é o que a especificação do HTML
/// define. O conversor padrão do System.Text.Json para `TimeOnly` exige `HH:mm:ss` e devolve
/// 400. Resultado: criar lembrete COM HORA pela tela nunca funcionava.
///
/// É o pior tipo de bug: o vendedor não abre chamado dizendo "o parser recusou o formato". Ele
/// conclui que o sistema não presta e para de usar lembrete com hora — que é metade do valor do
/// Meu Dia.
/// ===================================================================
///
/// ===================== POR QUE NA API, E NÃO NO CLIENTE =====================
/// Mandar `"14:30:00"` do Angular resolveria ESTA tela. A API continuaria recusando o formato
/// que todo navegador produz, e o próximo consumidor — outro painel, um app, uma integração —
/// tropeçaria no mesmo lugar, com o mesmo 400 sem explicação.
///
/// O servidor é quem define o contrato. Aceitar as duas formas aqui torna o contrato honesto
/// com o que o HTML de fato envia.
/// ============================================================================
///
/// A ESCRITA continua saindo em `HH:mm:ss`: formato único de saída mantém o cliente sem
/// adivinhação, e é o que o `input[type=time]` também aceita ao ler.</summary>
public class ConversorHoraFlexivel : JsonConverter<TimeOnly>
{
    /// <summary>Da mais específica para a menos: `TryParseExact` com lista de formatos escolhe a
    /// primeira que casar, e `HH:mm:ss` é o formato canônico de saída.</summary>
    private static readonly string[] Formatos = ["HH:mm:ss", "HH:mm"];

    public override TimeOnly Read(ref Utf8JsonReader leitor, Type tipo, JsonSerializerOptions op)
    {
        var texto = leitor.GetString();

        if (string.IsNullOrWhiteSpace(texto))
            throw new JsonException("Hora vazia. Use HH:mm ou HH:mm:ss.");

        if (TimeOnly.TryParseExact(texto, Formatos, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var hora))
            return hora;

        // Mensagem que diz o que fazer. O 400 do conversor padrão não diz nada, e o sintoma
        // ("não salva o lembrete") não aponta para o formato da hora.
        throw new JsonException($"Hora inválida: \"{texto}\". Use HH:mm ou HH:mm:ss.");
    }

    public override void Write(Utf8JsonWriter escritor, TimeOnly valor, JsonSerializerOptions op) =>
        escritor.WriteStringValue(valor.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
}

/// <summary>O mesmo para `TimeOnly?`.
///
/// O System.Text.Json NÃO deriva o conversor do nullable a partir do conversor do tipo base
/// quando ele é registrado na lista de `Converters` — e `HoraAlvo` é justamente nullable
/// (lembrete sem hora marcada é o caso comum). Sem este par, a correção não teria efeito
/// nenhum no campo que importa.</summary>
public class ConversorHoraFlexivelNulavel : JsonConverter<TimeOnly?>
{
    private static readonly ConversorHoraFlexivel Base = new();

    public override TimeOnly? Read(ref Utf8JsonReader leitor, Type tipo, JsonSerializerOptions op) =>
        leitor.TokenType == JsonTokenType.Null ? null : Base.Read(ref leitor, tipo, op);

    public override void Write(Utf8JsonWriter escritor, TimeOnly? valor, JsonSerializerOptions op)
    {
        if (valor is null) escritor.WriteNullValue();
        else Base.Write(escritor, valor.Value, op);
    }
}
