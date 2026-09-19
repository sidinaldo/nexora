using System.Globalization;
using System.Text;

namespace Nexora.Core.Entidades;

/// <summary>Texto → `OrigemLead`, casando SÓ pelo nome.
///
/// ===================== O DEFEITO QUE ISTO CONSERTA =====================
/// Eram duas cópias de `Enum.TryParse<OrigemLead>(texto, ignoreCase: true, ...)` — no cadastro e na
/// importação —, e `TryParse` aceita muito mais que nomes:
///
///   "2"     vira `Whatsapp`, pela posição no enum — um código de campanha na planilha virava a
///           origem errada, calado;
///   "15"    vira `(OrigemLead)15`, que não existe. O Npgsql não tem rótulo para gravá-lo no enum
///           nativo, e a importação inteira caía com 500 na hora do `SaveChanges` — DEPOIS de a
///           prévia ter dito que a linha entrava;
///   "4,8"   vira 12, por ser lista de flags.
///
/// A planilha do cliente não conhece o nosso enum, e o campo "origem" nela é texto livre. Casar
/// só por nome — e cair em `Manual` no resto — é a única leitura que não inventa informação.
/// =======================================================================</summary>
public static class OrigemLeadTexto
{
    public static OrigemLead Ler(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return OrigemLead.Manual;

        var limpo = SemAcento(texto.Trim()).Replace("_", "").Replace("-", "").Replace(" ", "");

        foreach (var origem in Enum.GetValues<OrigemLead>())
            if (string.Equals(origem.ToString(), limpo, StringComparison.OrdinalIgnoreCase))
                return origem;

        return OrigemLead.Manual;
    }

    /// <summary>"Indicação" é como o cliente escreve na planilha; `Indicacao` é o nome no enum.</summary>
    private static string SemAcento(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
