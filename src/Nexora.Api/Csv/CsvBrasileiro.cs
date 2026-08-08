using System.Globalization;
using System.Text;

namespace Nexora.Api.Csv;

/// <summary>===================== O CSV QUE O EXCEL BRASILEIRO ABRE =====================
///
/// Três decisões, e cada uma tem um sintoma quando falta:
///
///   BOM UTF-8      sem ele o Excel assume a codificação do sistema e "Preço" abre como "PreÃ§o".
///                  O BOM são TRÊS BYTES (EF BB BF) no começo do arquivo, não um caractere.
///
///   `;` separador  o Excel em pt-BR usa `;` porque a VÍRGULA é o separador DECIMAL. Com vírgula
///                  o arquivo abre com tudo na primeira coluna.
///
///   `,` decimal    "1234.56" é TEXTO para o Excel pt-BR, e a coluna não soma. E sem separador de
///                  milhar: "1.234,56" com o ponto viraria outro número em alguns locales.
///
/// CRLF nas quebras, que é o que o Excel gera — LF sozinho funciona na leitura, mas o arquivo
/// abre diferente do que o usuário exportaria de volta, e a diferença aparece em `diff`.
///
/// Vive aqui e não no `download.ts` porque os dois PRECISAM produzir o mesmo arquivo: um relatório
/// não pode sair diferente conforme tenha vindo do botão da tela ou da rota. Este é o lado que
/// vale para volume; o cliente só dispara o download.
/// ==============================================================================</summary>
public static class CsvBrasileiro
{
    /// <summary>Vírgula decimal, sem separador de milhar. Ver o comentário acima.</summary>
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Os bytes do arquivo, prontos para `File(...)`. Devolve BYTES e não string porque o
    /// BOM é byte: `Encoding.UTF8.GetBytes("﻿" + ...)` funciona, mas esconde a decisão em
    /// cima de um caractere invisível que qualquer edição pode apagar sem ninguém ver.</summary>
    public static byte[] Gerar(IEnumerable<string[]> linhas)
    {
        var texto = string.Join("\r\n", linhas.Select(Linha));

        // `new UTF8Encoding(false)` para NÃO duplicar o preâmbulo: o `Encoding.UTF8` estático já
        // carrega um, e concatená-lo ao explícito daria seis bytes de BOM e um arquivo corrompido.
        var corpo = new UTF8Encoding(false).GetBytes(texto);
        var arquivo = new byte[Bom.Length + corpo.Length];

        Bom.CopyTo(arquivo, 0);
        corpo.CopyTo(arquivo, Bom.Length);
        return arquivo;
    }

    /// <summary>EF BB BF. Público para o teste conferir sem repetir a constante.</summary>
    public static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Escapa só quando precisa — campo com `;`, aspas ou quebra de linha. Mesma regra do
    /// `download.ts` do cliente, para o arquivo sair igual venha de onde vier.</summary>
    private static string Linha(string[] campos) =>
        string.Join(';', campos.Select(c =>
            c.AsSpan().IndexOfAny(";\"\r\n".AsSpan()) >= 0
                ? $"\"{c.Replace("\"", "\"\"")}\""
                : c));

    public static string Num(int v) => v.ToString(Br);

    /// <summary>Duas casas SEMPRE, mesmo em valor redondo: uma coluna com "50" e "1234,56"
    /// misturados o Excel às vezes lê como texto.</summary>
    public static string Moeda(decimal v) => v.ToString("0.00", Br);

    public static string Dec(double v) => v.ToString("0.0", Br);

    /// <summary>Sem o símbolo `%`: com ele a célula vira texto e não entra em conta nenhuma.
    /// O cabeçalho da coluna já diz que é percentual.</summary>
    public static string Pct(double v) => (v * 100).ToString("0.0", Br);
}
