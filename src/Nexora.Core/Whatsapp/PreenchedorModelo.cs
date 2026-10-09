using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nexora.Core.Servicos;
using Nexora.Core.Texto;

namespace Nexora.Core.Whatsapp;

/// <summary>Os valores de um template no momento do envio. `Vendedor` nulo vale o nome da empresa:
/// a automatica sem dono ainda fala em nome de alguem.</summary>
public sealed record DadosDoModelo(string? NomeContato, string NomeEmpresa, string? NomeVendedor);

/// <summary>===================== AS VARIAVEIS DE UM TEMPLATE (INT-XX) =====================
///
/// Quem escreve o template usa NOMES (`{{nome}}`, `{{empresa}}`, `{{vendedor}}`); a Meta so entende
/// NUMEROS (`{{1}}`, `{{2}}`), na ordem em que aparecem. Esta classe faz a ponte nos dois sentidos —
/// e e o unico lugar que sabe disso.
///
/// ⚠️ LISTA FECHADA. Variavel livre deixaria o template pedir um dado que o envio nao tem, e a Meta
/// recusa parametro vazio (132000) — o erro apareceria so no envio, para o vendedor.
///
/// ⚠️ NAO PODE COMECAR NEM TERMINAR COM VARIAVEL: e regra da Meta, e ela rejeita o template na
/// revisao, dias depois. Recusar aqui poupa a espera.
/// ===================================================================================</summary>
public static class PreenchedorModelo
{
    public static readonly IReadOnlyList<string> Permitidas = ["nome", "empresa", "vendedor"];

    /// <summary>O teto da Meta para o corpo de um template.</summary>
    public const int TetoCorpo = 1024;

    private static readonly Regex Variavel = new(@"\{\{\s*([^{}]*?)\s*\}\}", RegexOptions.Compiled);

    /// <summary>As variaveis do corpo, na ordem da primeira aparicao. LANCA
    /// `RegraDeNegocioException` com o que corrigir.</summary>
    public static IReadOnlyList<string> VariaveisDe(string corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo))
            throw new RegraDeNegocioException("Escreva o texto do template.");
        if (corpo.Length > TetoCorpo)
            throw new RegraDeNegocioException($"O texto do template passa de {TetoCorpo} caracteres.");

        var ordem = new List<string>();
        foreach (Match m in Variavel.Matches(corpo))
        {
            var nome = m.Groups[1].Value.ToLowerInvariant();
            if (!Permitidas.Contains(nome))
                throw new RegraDeNegocioException(
                    $"A variável {{{{{m.Groups[1].Value}}}}} não existe. Use {{{{nome}}}}, {{{{empresa}}}} ou {{{{vendedor}}}}.");
            if (!ordem.Contains(nome)) ordem.Add(nome);
        }

        var aparado = corpo.Trim();
        if (aparado.StartsWith("{{", StringComparison.Ordinal) || aparado.EndsWith("}}", StringComparison.Ordinal))
            throw new RegraDeNegocioException(
                "A Meta não aceita template que começa ou termina com variável. Escreva um texto antes e depois.");

        return ordem;
    }

    /// <summary>O corpo como a Meta quer: cada nome trocado pelo numero da sua posicao.</summary>
    public static string ParaMeta(string corpo, IReadOnlyList<string> variaveis) =>
        Variavel.Replace(corpo, m =>
        {
            var indice = IndiceDe(variaveis, m.Groups[1].Value);
            return "{{" + (indice + 1).ToString(CultureInfo.InvariantCulture) + "}}";
        });

    /// <summary>Os parametros do envio, na ordem das variaveis.</summary>
    public static IReadOnlyList<string> Valores(IReadOnlyList<string> variaveis, DadosDoModelo dados) =>
        variaveis.Select(v => Valor(v, dados)).ToList();

    /// <summary>O texto que o cliente vai ler — o que fica na thread.</summary>
    public static string Preencher(string corpo, DadosDoModelo dados) =>
        Variavel.Replace(corpo, m => Valor(m.Groups[1].Value.ToLowerInvariant(), dados));

    /// <summary>Exemplos para a revisao da Meta: ela exige um valor por variavel ao criar.</summary>
    public static IReadOnlyList<string> Exemplos(IReadOnlyList<string> variaveis) =>
        Valores(variaveis, new DadosDoModelo("Maria", "Loja Exemplo", "João"));

    /// <summary>O nome do template na Meta: minusculas, numeros e `_`. "Boas-vindas à loja" vira
    /// `boas_vindas_a_loja`.</summary>
    public static string NomeParaMeta(string nome)
    {
        var semAcento = new StringBuilder();
        foreach (var c in (nome ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            semAcento.Append(c);
        }

        var saida = new StringBuilder();
        foreach (var c in semAcento.ToString().ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) saida.Append(c);
            else if (saida.Length > 0 && saida[^1] != '_') saida.Append('_');
        }

        var final = saida.ToString().Trim('_');
        if (final.Length == 0)
            throw new RegraDeNegocioException("Dê um nome ao template, com letras ou números.");
        if (final.Length > 512) final = final[..512].TrimEnd('_');
        return final;
    }

    private static string Valor(string variavel, DadosDoModelo dados)
    {
        if (variavel == "nome")
        {
            // Sem nome de verdade (so o telefone), "cliente" — a Meta recusa parametro vazio.
            var primeiro = NomeDePessoa.Primeiro(dados.NomeContato);
            if (primeiro == null) return "cliente";
            return primeiro;
        }
        if (variavel == "vendedor")
        {
            var primeiro = NomeDePessoa.Primeiro(dados.NomeVendedor);
            if (primeiro == null) return dados.NomeEmpresa;
            return primeiro;
        }
        return dados.NomeEmpresa;
    }

    private static int IndiceDe(IReadOnlyList<string> variaveis, string nome)
    {
        var chave = nome.Trim().ToLowerInvariant();
        for (var i = 0; i < variaveis.Count; i++)
            if (variaveis[i] == chave) return i;
        throw new RegraDeNegocioException($"A variável {{{{{nome}}}}} não está na lista do template.");
    }
}
