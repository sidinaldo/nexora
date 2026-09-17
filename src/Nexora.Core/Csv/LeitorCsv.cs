using System.Text;

namespace Nexora.Core.Csv;

/// <summary>Uma tabela lida de um CSV: o cabeçalho e as linhas, já casadas por NOME de coluna.
///
/// A ordem das colunas no arquivo não importa — quem exporta de outro sistema não vai reordenar
/// planilha para caber no nosso gosto.</summary>
public sealed class TabelaCsv
{
    private readonly Dictionary<string, int> _colunas;
    private readonly IReadOnlyList<string[]> _linhas;

    internal TabelaCsv(Dictionary<string, int> colunas, IReadOnlyList<string[]> linhas)
    {
        _colunas = colunas;
        _linhas = linhas;
    }

    public int Quantidade => _linhas.Count;

    /// <summary>Os nomes de coluna do arquivo, já normalizados (minúsculas, sem acento).</summary>
    public IReadOnlyCollection<string> Colunas => _colunas.Keys;

    public bool Tem(string coluna) => _colunas.ContainsKey(LeitorCsv.Normalizar(coluna));

    /// <summary>O valor da coluna na linha, ou "" — coluna ausente e célula vazia dizem a mesma
    /// coisa para quem importa, e distinguir só produziria dois caminhos com o mesmo destino.
    ///
    /// ⚠️ ACEITA VÁRIOS NOMES, o primeiro que existir. "telefone", "celular" e "whatsapp" são a
    /// mesma coluna no arquivo que o cliente tem na mão — e obrigar um nome único faria a
    /// importação falhar por causa do cabeçalho, que é a pior primeira impressão possível.</summary>
    public string Valor(int linha, params string[] nomes)
    {
        foreach (var nome in nomes)
            if (_colunas.TryGetValue(LeitorCsv.Normalizar(nome), out var i) && i < _linhas[linha].Length)
                return _linhas[linha][i].Trim();

        return "";
    }
}

/// <summary>===================== O LADO DA LEITURA DO CSV =====================
///
/// É o espelho de <see cref="CsvBrasileiro"/>, e mora ao lado dele de propósito: o arquivo que o
/// Nexora EXPORTA tem de voltar para dentro do Nexora. Os dois lados separados divergiriam no
/// primeiro ajuste, e o sintoma seria o cliente exportando os contatos, reimportando e vendo tudo
/// duplicado — ou nada entrar.
///
/// ===================== O QUE ELE PRECISA ENGOLIR =====================
/// O arquivo não vem daqui. Vem do Excel do cliente, do sistema antigo dele, do contador:
///
///   BOM UTF-8      o próprio `CsvBrasileiro` escreve; se não for descartado, a PRIMEIRA coluna
///                  do cabeçalho vira "﻿nome" e nunca casa com "nome".
///   `;` ou `,`     o Excel pt-BR usa `;`; quase todo o resto do mundo usa `,`. Decidido pela
///                  linha de cabeçalho, contando fora das aspas.
///   aspas          `"Silva, João"` é UM campo, e `""` dentro delas é uma aspa literal.
///   CRLF, LF, CR   Windows, Unix e Excel velho de Mac.
///   linhas vazias  planilha salva em CSV costuma terminar com uma; ela não é um contato.
/// ====================================================================
///
/// ⚠️ NÃO É UM PARSER DE CSV GENÉRICO, e não tenta ser. Sem `CsvHelper` porque o formato aqui é
/// pequeno e conhecido, e uma dependência a mais num projeto que já escreve o próprio CSV seria
/// carregar as duas coisas.</summary>
public static class LeitorCsv
{
    /// <summary>Os dois separadores que aparecem no mundo real. `;` primeiro porque é o do Excel
    /// em pt-BR — ver `CsvBrasileiro`.</summary>
    private static readonly char[] Separadores = [';', ','];

    /// <summary>Lê o arquivo inteiro. Devolve `null` quando não há nem cabeçalho — arquivo vazio,
    /// ou só linhas em branco.</summary>
    public static TabelaCsv? Ler(byte[] bytes)
    {
        var texto = Decodificar(bytes);
        if (string.IsNullOrWhiteSpace(texto)) return null;

        var separador = EscolherSeparador(texto);
        var linhas = Dividir(texto, separador);

        // A primeira linha com conteúdo é o cabeçalho. Uma planilha exportada às vezes começa com
        // linhas em branco, e contá-las como cabeçalho faria o arquivo inteiro parecer inválido.
        var iCabecalho = linhas.FindIndex(l => l.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (iCabecalho < 0) return null;

        var colunas = new Dictionary<string, int>();
        var cabecalho = linhas[iCabecalho];
        for (var i = 0; i < cabecalho.Length; i++)
        {
            var nome = Normalizar(cabecalho[i]);
            // ⚠️ O PRIMEIRO GANHA. Planilha com duas colunas "email" existe, e sobrescrever faria
            // a segunda (normalmente a vazia, sobra de edição) apagar a primeira.
            if (nome.Length > 0) colunas.TryAdd(nome, i);
        }

        var corpo = linhas.Skip(iCabecalho + 1)
            .Where(l => l.Any(c => !string.IsNullOrWhiteSpace(c)))
            .ToList();

        return new TabelaCsv(colunas, corpo);
    }

    /// <summary>Minúsculas, sem acento e sem espaços nas pontas. "Telefone", "TELEFONE" e
    /// "telefone " são a mesma coluna; "Observações" e "observacoes" também.</summary>
    internal static string Normalizar(string bruto)
    {
        var semAcento = new StringBuilder(bruto.Length);

        foreach (var c in bruto.Trim().Normalize(NormalizationForm.FormD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                semAcento.Append(c);

        return semAcento.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>⚠️ O BOM SAI AQUI. `Encoding.UTF8.GetString` NÃO o remove — ele vira o caractere
    /// U+FEFF colado no começo da primeira célula, e "﻿nome" nunca casa com "nome". O sintoma é a
    /// importação dizer "falta a coluna nome" sobre um arquivo que a tem.
    ///
    /// Latin-1 como plano B: planilha salva como "CSV (separado por vírgulas)" em Windows antigo
    /// não é UTF-8, e ler assim transforma "João" em caracteres de substituição. Se a decodificação
    /// UTF-8 produzir U+FFFD, o arquivo não era UTF-8.</summary>
    private static string Decodificar(byte[] bytes)
    {
        var corpo = bytes.Length >= 3 && bytes[0] == CsvBrasileiro.Bom[0]
                 && bytes[1] == CsvBrasileiro.Bom[1] && bytes[2] == CsvBrasileiro.Bom[2]
            ? bytes[3..]
            : bytes;

        var texto = new UTF8Encoding(false).GetString(corpo);
        return texto.Contains('�') ? Encoding.Latin1.GetString(corpo) : texto;
    }

    /// <summary>Pelo cabeçalho, e não pelo arquivo inteiro: um campo de observação com ponto e
    /// vírgula no meio não pode decidir o formato das outras mil linhas.</summary>
    private static char EscolherSeparador(string texto)
    {
        var fim = texto.IndexOfAny(['\r', '\n']);
        var cabecalho = fim < 0 ? texto : texto[..fim];

        var melhor = Separadores[0];
        var maior = -1;

        foreach (var s in Separadores)
        {
            var quantos = ContarFora(cabecalho, s);
            if (quantos > maior) { maior = quantos; melhor = s; }
        }

        return melhor;
    }

    private static int ContarFora(string linha, char separador)
    {
        var dentro = false;
        var total = 0;

        foreach (var c in linha)
        {
            if (c == '"') dentro = !dentro;
            else if (c == separador && !dentro) total++;
        }

        return total;
    }

    /// <summary>A máquina de estados. Aspas, `""` escapado, separador e quebra de linha DENTRO de
    /// aspas — tudo num passo só, porque dividir por linha antes e por campo depois quebra o campo
    /// que contém uma quebra de linha.</summary>
    private static List<string[]> Dividir(string texto, char separador)
    {
        var linhas = new List<string[]>();
        var campos = new List<string>();
        var atual = new StringBuilder();
        var dentro = false;

        void FecharCampo()
        {
            campos.Add(atual.ToString());
            atual.Clear();
        }

        void FecharLinha()
        {
            FecharCampo();
            linhas.Add([.. campos]);
            campos.Clear();
        }

        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];

            if (dentro)
            {
                if (c != '"') { atual.Append(c); continue; }

                // `""` é uma aspa literal; uma aspa sozinha fecha o campo.
                if (i + 1 < texto.Length && texto[i + 1] == '"') { atual.Append('"'); i++; }
                else dentro = false;
                continue;
            }

            if (c == '"') { dentro = true; continue; }
            if (c == separador) { FecharCampo(); continue; }

            if (c is '\r' or '\n')
            {
                // CRLF conta como UMA quebra; CR e LF sozinhos também.
                if (c == '\r' && i + 1 < texto.Length && texto[i + 1] == '\n') i++;
                FecharLinha();
                continue;
            }

            atual.Append(c);
        }

        // O que sobrou depois da última quebra — arquivo sem quebra no fim é o caso comum.
        if (atual.Length > 0 || campos.Count > 0) FecharLinha();

        return linhas;
    }
}
