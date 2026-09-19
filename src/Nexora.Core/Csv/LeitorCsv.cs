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
    private readonly IReadOnlyList<int> _numeros;

    internal TabelaCsv(
        Dictionary<string, int> colunas, IReadOnlyList<string[]> linhas, IReadOnlyList<int> numeros)
    {
        _colunas = colunas;
        _linhas = linhas;
        _numeros = numeros;
    }

    public int Quantidade => _linhas.Count;

    /// <summary>O número da linha COMO O EXCEL MOSTRA — o que o dono procura para corrigir.
    ///
    /// ⚠️ NÃO É `índice + 2`, e foi isso que a revisão achou. As linhas em branco são descartadas
    /// antes de chegar aqui (uma planilha salva em CSV costuma ter algumas), e a conta `i + 2`
    /// passava a apontar a linha errada a partir da primeira em branco: "linha 46" era a 47 do
    /// Excel, e o dono editava a vizinha. O número agora vem do arquivo, contado ANTES de
    /// descartar qualquer coisa.
    ///
    /// Um campo entre aspas com quebra de linha dentro continua sendo UMA linha, como no Excel —
    /// a conta é por registro, não por quebra física.</summary>
    public int NumeroLinha(int linha) => _numeros[linha];

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

        var linhas = DividirComOSeparadorCerto(texto);

        // A primeira linha com conteúdo é o cabeçalho. Uma planilha exportada às vezes começa com
        // linhas em branco, e contá-las como cabeçalho faria o arquivo inteiro parecer inválido.
        var iCabecalho = linhas.FindIndex(TemConteudo);
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

        // ⚠️ O NÚMERO É GUARDADO ANTES DE DESCARTAR AS EM BRANCO. `i` é a posição do registro no
        // arquivo, então `i + 1` é a linha do Excel — o cabeçalho incluído, as em branco incluídas.
        var corpo = linhas
            .Select((linha, i) => (linha, numero: i + 1))
            .Skip(iCabecalho + 1)
            .Where(x => TemConteudo(x.linha))
            .ToList();

        return new TabelaCsv(
            colunas, corpo.Select(x => x.linha).ToList(), corpo.Select(x => x.numero).ToList());
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

        // ⚠️ COM BOM, É UTF-8 E PONTO — foi o próprio arquivo que disse. Nenhum plano B aqui.
        if (corpo.Length != bytes.Length) return new UTF8Encoding(false).GetString(corpo);

        // ⚠️ SEM BOM, UTF-8 ESTRITO, e só a falha de decodificação troca de tabela. A versão
        // anterior decodificava frouxo e trocava tudo para Latin-1 ao ver UM `U+FFFD` no texto —
        // mas `U+FFFD` também é um caractere legítimo: um pushName com emoji quebrado, exportado
        // e reimportado, fazia o arquivo inteiro ser relido como Latin-1, e todo acento virava lixo
        // ("JoÃ£o") sem erro nenhum. Estrito, o decodificador só falha com bytes que NÃO SÃO UTF-8.
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(corpo);
        }
        catch (DecoderFallbackException)
        {
            // ⚠️ WINDOWS-1252, E NÃO LATIN-1. É o que o Excel em português grava quando salva
            // "CSV (separado por vírgulas)" sem UTF-8. Os dois concordam nas letras acentuadas,
            // e divergem exatamente onde o cliente escreve: aspas curvas, travessão, reticências e
            // o euro caem em 0x80–0x9F, que no Latin-1 são caracteres de controle invisíveis.
            return Windows1252.Value.GetString(corpo);
        }
    }

    /// <summary>A tabela 1252 vem do provedor de páginas de código, que o .NET não registra
    /// sozinho. `Lazy` para registrar uma vez, na primeira planilha que precisar.</summary>
    private static readonly Lazy<Encoding> Windows1252 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    });

    /// <summary>Divide o arquivo com o separador que dá MAIS colunas no cabeçalho.
    ///
    /// ⚠️ PELO MESMO PARSER QUE VAI LER, e esta é a mudança. A versão anterior contava separadores
    /// na PRIMEIRA LINHA FÍSICA, com uma contagem própria de aspas — e divergia do parser de duas
    /// formas, as duas achadas em revisão:
    ///
    ///   · arquivo começando com linha em branco: a primeira linha era vazia, os dois separadores
    ///     empatavam em zero, `;` ganhava, e um CSV de vírgula virava UMA coluna chamada
    ///     "nome,telefone" — "falta a coluna nome" sobre um arquivo que a tinha;
    ///   · a contagem de aspas tinha regra própria, diferente da do parser.
    ///
    /// Agora o arquivo é dividido com cada separador e vence o que dá mais colunas no mesmo
    /// cabeçalho que `Ler` vai usar — a primeira linha COM CONTEÚDO. Duas passadas sobre no máximo
    /// 1 MB; é barato, e elimina a possibilidade de escolher com uma regra e ler com outra.
    ///
    /// Empate fica com `;`, que é o do Excel em pt-BR — ver `CsvBrasileiro`.</summary>
    private static List<string[]> DividirComOSeparadorCerto(string texto)
    {
        List<string[]>? melhor = null;
        var maisColunas = -1;

        foreach (var separador in Separadores)
        {
            var linhas = Dividir(texto, separador);
            var cabecalho = linhas.FirstOrDefault(TemConteudo);
            var colunas = cabecalho?.Length ?? 0;

            if (colunas > maisColunas) { maisColunas = colunas; melhor = linhas; }
        }

        return melhor!;
    }

    private static bool TemConteudo(string[] linha) =>
        linha.Any(c => !string.IsNullOrWhiteSpace(c));

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

            // ⚠️ ASPA SÓ ABRE CAMPO NO COMEÇO DELE — é a regra do RFC 4180, e é o que o Excel faz.
            // A versão anterior entrava em modo aspas em QUALQUER aspa, e uma célula com `TV 42"`
            // (polegadas) engolia todo o resto do arquivo num campo só: 800 linhas viravam 4, sem
            // erro nenhum apontando a aspa. No meio do campo, aspa é só um caractere.
            if (c == '"' && atual.Length == 0) { dentro = true; continue; }
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
