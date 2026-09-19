using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexora.Core.LeadAds;

/// <summary>Para onde uma coluna do arquivo vai dentro do Nexora (INT-XX).</summary>
public enum CampoImportacao
{
    /// <summary>Não entra. É o destino de toda coluna que ninguém mapeou — `platform`, `is_organic`,
    /// o nome do conjunto de anúncios.</summary>
    Ignorar,

    Nome,
    Telefone,
    Email,

    /// <summary>⚠️ O ÚNICO QUE ACEITA VÁRIAS COLUNAS. É para onde vão as perguntas que o cliente
    /// criou no formulário ("Qual seu orçamento?", "Tem urgência?"): o contato não tem campo próprio
    /// para elas, e juntá-las em "Pergunta: resposta" na observação é o que deixa a resposta visível
    /// para o vendedor na tela do contato — que é para quem ela foi coletada.</summary>
    Observacoes,

    /// <summary>A campanha ou o anúncio, em texto — `OrigemDetalhe`, que a tela do contato já
    /// mostra como "de onde veio".</summary>
    OrigemDetalhe,

    MetaLeadId,
    MetaAdId,
    MetaCampaignId,
    MetaFormId,

    /// <summary>`created_time`: quando a pessoa preencheu. Vira a data de entrada do contato.</summary>
    CriadoEm
}

/// <summary>===================== O FORMATO DO GERENCIADOR DE LEADS DA META =====================
///
/// Duas famílias de coluna, e elas se tratam diferente:
///
///   METADADOS — fixos, com nome em inglês: `id`, `created_time`, `ad_id`, `adset_id`,
///               `campaign_id`, `form_id`, `platform`, `is_organic`, e os `*_name`. O mapeamento
///               automático os reconhece.
///   FORMULÁRIO — `full_name`, `phone_number`, `email`, e as perguntas que o CLIENTE criou, com o
///               nome que ele deu. Os três primeiros são reconhecidos; o resto o dono liga à mão.
///
/// ⚠️ ESTE ARQUIVO FOI ESCRITO SEM UM EXPORT REAL NA MÃO, e isso é uma pendência declarada — o
/// primeiro critério de aceite do INT-XX é "CSV real importa sem erro". Os nomes vêm do spec. Dois
/// detalhes vêm de memória do formato da Meta e estão tratados de forma DEFENSIVA, para funcionar
/// com ou sem eles: o prefixo de tipo nos ids (`l:`, `ag:`, `as:`, `c:`, `f:`) e o `p:` no telefone.
/// Quando chegar um arquivo de verdade, é aqui que se confere.
/// =========================================================================================</summary>
public static class MapeamentoMeta
{
    /// <summary>Nome da coluna (normalizado) → campo. O que não está aqui fica `Ignorar` e aparece
    /// na tela para o dono decidir.
    ///
    /// ⚠️ `id` SOZINHO É O LEAD, e é por isso que ele vai para `MetaLeadId` e não para nada mais
    /// genérico: no export do Gerenciador de Leads, a primeira coluna `id` é o id do lead — a chave
    /// da deduplicação.</summary>
    private static readonly Dictionary<string, CampoImportacao> Conhecidas = new()
    {
        // ---------- formulário
        ["full_name"] = CampoImportacao.Nome,
        ["nome"] = CampoImportacao.Nome,
        ["nome_completo"] = CampoImportacao.Nome,
        ["nome completo"] = CampoImportacao.Nome,
        ["name"] = CampoImportacao.Nome,

        ["phone_number"] = CampoImportacao.Telefone,
        ["phone"] = CampoImportacao.Telefone,
        ["telefone"] = CampoImportacao.Telefone,
        ["celular"] = CampoImportacao.Telefone,
        ["whatsapp"] = CampoImportacao.Telefone,

        ["email"] = CampoImportacao.Email,
        ["e-mail"] = CampoImportacao.Email,

        // ---------- metadados
        ["id"] = CampoImportacao.MetaLeadId,
        ["lead_id"] = CampoImportacao.MetaLeadId,
        ["ad_id"] = CampoImportacao.MetaAdId,
        ["campaign_id"] = CampoImportacao.MetaCampaignId,
        ["form_id"] = CampoImportacao.MetaFormId,
        ["created_time"] = CampoImportacao.CriadoEm,

        // O nome da CAMPANHA é o "de onde veio" que o vendedor entende; o id é para máquina.
        ["campaign_name"] = CampoImportacao.OrigemDetalhe,
    };

    /// <summary>O mapeamento sugerido para um cabeçalho, na ordem do arquivo.
    ///
    /// ⚠️ CADA CAMPO ÚNICO É SUGERIDO UMA VEZ SÓ. Um arquivo com `phone_number` e `telefone` (o
    /// cliente acrescentou uma coluna à mão) não pode sugerir dois telefones: a primeira ganha, e a
    /// outra fica `Ignorar` para o dono ver e decidir. `Observacoes` não entra nessa regra — ela
    /// aceita várias por definição.</summary>
    public static IReadOnlyList<(string Coluna, CampoImportacao Campo)> Sugerir(
        IReadOnlyList<string> cabecalho)
    {
        var usados = new HashSet<CampoImportacao>();
        var resultado = new List<(string, CampoImportacao)>(cabecalho.Count);

        foreach (var coluna in cabecalho)
        {
            var campo = Conhecidas.GetValueOrDefault(Normalizar(coluna), CampoImportacao.Ignorar);

            if (campo != CampoImportacao.Ignorar && campo != CampoImportacao.Observacoes
                && !usados.Add(campo))
                campo = CampoImportacao.Ignorar;

            resultado.Add((coluna, campo));
        }

        return resultado;
    }

    /// <summary>Tira o prefixo de tipo que o export da Meta põe nos ids: `l:1234` → `1234`.
    ///
    /// ⚠️ SÓ LETRAS ANTES DOS DOIS-PONTOS, e isso é o que torna o corte seguro com ou sem prefixo:
    /// id de verdade é só dígito, então `l:`, `ag:`, `as:`, `c:`, `f:` saem, e um valor sem prefixo
    /// passa intacto. Guardar com o prefixo faria o mesmo lead ter dois ids diferentes no dia em que
    /// ele chegar pela Graph API, que não usa prefixo — e a deduplicação deixaria de casar.</summary>
    public static string? IdLimpo(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var v = PrefixoDeTipo.Replace(valor.Trim(), "");
        return v.Length == 0 ? null : v;
    }

    private static readonly Regex PrefixoDeTipo = new(@"^[A-Za-z]{1,3}:", RegexOptions.Compiled);

    /// <summary>`created_time` → instante UTC. Nulo quando não dá para ler — e aí o contato entra com
    /// a data de hoje, que é o comportamento de antes, em vez de a linha ser recusada por um campo
    /// que não é essencial.
    ///
    /// Aceita o que aparece no mundo real:
    ///   · ISO com fuso sem dois-pontos, `2026-09-10T14:32:11+0000` — o formato da Meta;
    ///   · ISO com fuso normal, `2026-09-10T14:32:11+00:00` ou `Z`;
    ///   · ⚠️ `10/09/2026 14:32` — o que sobra quando o dono abre o CSV no Excel em português e salva
    ///     de novo. Lido como horário de BRASÍLIA, porque foi o Excel dele que reescreveu.</summary>
    public static DateTime? Data(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var v = valor.Trim();

        // `+0000` → `+00:00`: o parser do .NET quer os dois-pontos no fuso.
        v = FusoSemDoisPontos.Replace(v, "$1$2:$3");

        if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var iso)
            && (v.Contains('T') || v.Contains('-')))
            return iso.UtcDateTime;

        if (DateTime.TryParseExact(v,
                ["dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy"],
                CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var br))
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(br, DateTimeKind.Unspecified),
                Brasilia.Value);

        return null;
    }

    private static readonly Regex FusoSemDoisPontos =
        new(@"([+-])(\d{2})(\d{2})$", RegexOptions.Compiled);

    /// <summary>`America/Sao_Paulo` no Linux, `E. South America Standard Time` no Windows — o .NET 8
    /// converte entre os dois, mas só se pedirmos pelo nome IANA.</summary>
    private static readonly Lazy<TimeZoneInfo> Brasilia =
        new(() => TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));

    /// <summary>Minúscula, sem acento, sem espaço nas pontas — a mesma regra do `LeitorCsv`.</summary>
    internal static string Normalizar(string bruto)
    {
        var sb = new StringBuilder(bruto.Length);
        foreach (var c in bruto.Trim().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
