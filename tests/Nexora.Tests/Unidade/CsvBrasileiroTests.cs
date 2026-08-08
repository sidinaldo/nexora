using System.Text;
using Nexora.Api.Csv;

namespace Nexora.Tests.Unidade;

/// <summary>===================== "ABRE NO EXCEL COM ACENTO CORRETO" =====================
///
/// O critério é sobre o que o Excel brasileiro faz com o arquivo, e isso é decidido por três
/// coisas que se conferem em BYTES — não em "parece certo no bloco de notas":
///
///   BOM UTF-8    sem ele o Excel assume a codificação do sistema e "Preço" abre "PreÃ§o";
///   `;`          com vírgula, o arquivo abre com tudo na primeira coluna;
///   `,` decimal  com ponto, a coluna de dinheiro é TEXTO e não soma.
///
/// Nenhuma das três aparece se alguém só olhar o arquivo aberto na máquina certa — é por isso
/// que o teste lê o byte.
/// ==============================================================================</summary>
public class CsvBrasileiroTests
{
    [Fact]
    public void O_ARQUIVO_COMECA_COM_BOM_UTF8()
    {
        var bytes = CsvBrasileiro.Gerar([["Preço", "Valor"]]);

        // EF BB BF, e exatamente uma vez: dois preâmbulos concatenados dão um arquivo que o Excel
        // abre com um caractere invisível colado no primeiro cabeçalho.
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
        Assert.NotEqual(0xEF, bytes[3]);
    }

    [Fact]
    public void ACENTO_SOBREVIVE_a_ida_e_volta()
    {
        var bytes = CsvBrasileiro.Gerar([["Preço", "Conversão", "Última compra", "João"]]);

        // Lê como UTF-8 pulando o BOM — é o que o Excel faz quando encontra o preâmbulo.
        var texto = Encoding.UTF8.GetString(bytes, CsvBrasileiro.Bom.Length,
                                            bytes.Length - CsvBrasileiro.Bom.Length);

        Assert.Equal("Preço;Conversão;Última compra;João", texto);
    }

    [Fact]
    public void SEPARADOR_E_PONTO_E_VIRGULA_e_a_quebra_e_CRLF()
    {
        var bytes = CsvBrasileiro.Gerar([["a", "b"], ["c", "d"]]);
        var texto = Texto(bytes);

        Assert.Equal("a;b\r\nc;d", texto);
    }

    /// <summary>Vírgula decimal e SEM separador de milhar. Com ponto, o Excel pt-BR trata a
    /// célula como texto e a coluna não soma — que é justamente para o que serve exportar.</summary>
    [Fact]
    public void DINHEIRO_SAI_COM_VIRGULA_DECIMAL_e_sem_milhar()
    {
        var texto = Texto(CsvBrasileiro.Gerar([
            [CsvBrasileiro.Moeda(1234.56m), CsvBrasileiro.Moeda(50m), CsvBrasileiro.Num(1000)]
        ]));

        Assert.Equal("1234,56;50,00;1000", texto);
    }

    /// <summary>Duas casas SEMPRE. Uma coluna com "50" e "1234,56" misturados é a receita para o
    /// Excel decidir que a coluna inteira é texto.</summary>
    [Fact]
    public void VALOR_REDONDO_mantem_as_duas_casas()
    {
        Assert.Equal("0,00", CsvBrasileiro.Moeda(0m));
        Assert.Equal("7,00", CsvBrasileiro.Moeda(7m));
    }

    /// <summary>Percentual SEM o símbolo: com ele a célula vira texto. O cabeçalho da coluna já
    /// diz que é percentual.</summary>
    [Fact]
    public void PERCENTUAL_sai_como_numero()
    {
        Assert.Equal("66,7", CsvBrasileiro.Pct(2d / 3d));
        Assert.Equal("100,0", CsvBrasileiro.Pct(1d));
    }

    /// <summary>===================== O CAMPO QUE QUEBRA O ARQUIVO =====================
    /// Nome de cliente com `;` é comum ("Silva; Filho" sai de importação malfeita), e sem escape
    /// ele empurra todas as colunas seguintes uma casa para a direita — a partir daquela linha o
    /// arquivo inteiro fica desalinhado, e ninguém percebe olhando as primeiras.
    /// ======================================================================</summary>
    [Fact]
    public void CAMPO_COM_SEPARADOR_ASPAS_OU_QUEBRA_e_escapado()
    {
        var texto = Texto(CsvBrasileiro.Gerar([
            ["Silva; Filho", "diz \"oi\"", "linha\ncom quebra", "comum"]
        ]));

        Assert.Equal("\"Silva; Filho\";\"diz \"\"oi\"\"\";\"linha\ncom quebra\";comum", texto);
    }

    [Fact]
    public void CAMPO_COMUM_nao_ganha_aspas_a_toa()
    {
        // Aspas em tudo é válido no formato e horrível de ler quando alguém abre no editor para
        // conferir uma linha — e conferir uma linha é o que se faz quando o número não bate.
        Assert.Equal("Ana;100;instagram", Texto(CsvBrasileiro.Gerar([["Ana", "100", "instagram"]])));
    }

    private static string Texto(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes, CsvBrasileiro.Bom.Length,
                                bytes.Length - CsvBrasileiro.Bom.Length);
}
