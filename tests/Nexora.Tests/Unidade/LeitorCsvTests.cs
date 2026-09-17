using System.Text;
using Nexora.Core.Csv;

namespace Nexora.Tests.Unidade;

/// <summary>O LADO DA LEITURA DO CSV.
///
/// ⚠️ O ARQUIVO NÃO VEM DAQUI. Vem do Excel do cliente, do sistema antigo dele, do contador — e
/// cada um desses escreve um dialeto. Estes testes são a lista do que precisa entrar sem que
/// ninguém tenha de "arrumar a planilha primeiro", que é onde uma importação morre.
///
/// O teste que fecha o ciclo está no fim: o que o `CsvBrasileiro` ESCREVE, o `LeitorCsv` LÊ.</summary>
public class LeitorCsvTests
{
    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    private static byte[] ComBom(string s)
    {
        var corpo = Utf8(s);
        var arquivo = new byte[CsvBrasileiro.Bom.Length + corpo.Length];
        CsvBrasileiro.Bom.CopyTo(arquivo, 0);
        corpo.CopyTo(arquivo, CsvBrasileiro.Bom.Length);
        return arquivo;
    }

    // ==================================================================== o basico
    [Fact]
    public void LE_CABECALHO_E_LINHAS_CASANDO_POR_NOME()
    {
        var t = LeitorCsv.Ler(Utf8("nome;telefone\nMaria;84988887777\nJoão;84999996666"))!;

        Assert.Equal(2, t.Quantidade);
        Assert.Equal("Maria", t.Valor(0, "nome"));
        Assert.Equal("84988887777", t.Valor(0, "telefone"));
        Assert.Equal("João", t.Valor(1, "nome"));
    }

    /// <summary>A ordem das colunas é do arquivo, não nossa: quem exporta de outro sistema não vai
    /// reordenar planilha para caber no nosso gosto.</summary>
    [Fact]
    public void A_ORDEM_DAS_COLUNAS_NAO_IMPORTA()
    {
        var t = LeitorCsv.Ler(Utf8("telefone;email;nome\n84988887777;m@x.com;Maria"))!;

        Assert.Equal("Maria", t.Valor(0, "nome"));
        Assert.Equal("m@x.com", t.Valor(0, "email"));
    }

    /// <summary>⚠️ SEM ISTO, "falta a coluna nome" SOBRE UM ARQUIVO QUE A TEM. `GetString` não
    /// remove o BOM: ele vira U+FEFF colado na primeira célula, e "﻿nome" não casa com "nome".
    /// E o BOM é justamente o que o NOSSO exportador escreve.</summary>
    [Fact]
    public void O_BOM_NAO_GRUDA_NA_PRIMEIRA_COLUNA()
    {
        var t = LeitorCsv.Ler(ComBom("nome;telefone\nMaria;84988887777"))!;

        Assert.True(t.Tem("nome"));
        Assert.Equal("Maria", t.Valor(0, "nome"));
    }

    // ==================================================================== os dialetos
    [Theory]
    [InlineData("nome;telefone\nMaria;84988887777")]          // Excel pt-BR
    [InlineData("nome,telefone\nMaria,84988887777")]          // o resto do mundo
    [InlineData("nome;telefone\r\nMaria;84988887777\r\n")]    // CRLF, com quebra no fim
    [InlineData("nome;telefone\rMaria;84988887777")]          // CR sozinho, Mac velho
    [InlineData("nome;telefone\n\n\nMaria;84988887777\n\n")]  // linhas em branco no meio e no fim
    public void ENGOLE_OS_DIALETOS_QUE_APARECEM_NO_MUNDO_REAL(string conteudo)
    {
        var t = LeitorCsv.Ler(Utf8(conteudo))!;

        Assert.Equal(1, t.Quantidade);
        Assert.Equal("Maria", t.Valor(0, "nome"));
        Assert.Equal("84988887777", t.Valor(0, "telefone"));
    }

    /// <summary>⚠️ PELO CABEÇALHO, E NÃO PELO ARQUIVO INTEIRO: uma observação com ponto e vírgula
    /// no meio não pode decidir o formato das outras mil linhas.</summary>
    [Fact]
    public void O_SEPARADOR_SAI_DO_CABECALHO()
    {
        var t = LeitorCsv.Ler(Utf8("nome,telefone,observacoes\nMaria,84988887777,\"comprou; voltou\""))!;

        Assert.Equal("Maria", t.Valor(0, "nome"));
        Assert.Equal("comprou; voltou", t.Valor(0, "observacoes"));
    }

    // ==================================================================== aspas
    [Fact]
    public void ASPAS_SEGURAM_SEPARADOR_QUEBRA_DE_LINHA_E_ASPAS()
    {
        var t = LeitorCsv.Ler(Utf8(
            "nome;observacoes\n"
            + "\"Silva; João\";\"disse \"\"volto amanhã\"\"\"\n"
            + "Ana;\"mora na\nsegunda rua\""))!;

        Assert.Equal(2, t.Quantidade);
        Assert.Equal("Silva; João", t.Valor(0, "nome"));
        Assert.Equal("disse \"volto amanhã\"", t.Valor(0, "observacoes"));
        Assert.Equal("mora na\nsegunda rua", t.Valor(1, "observacoes"));
    }

    // ==================================================================== o cabecalho
    [Theory]
    [InlineData("Nome")]
    [InlineData("NOME")]
    [InlineData("  nome  ")]
    public void O_NOME_DA_COLUNA_IGNORA_CAIXA_E_ESPACO(string cabecalho)
    {
        var t = LeitorCsv.Ler(Utf8($"{cabecalho};telefone\nMaria;84988887777"))!;
        Assert.Equal("Maria", t.Valor(0, "nome"));
    }

    /// <summary>"Observações" com til é o que o Excel escreve quando o cliente digita em
    /// português — e é o que o NOSSO exportador escreve também.</summary>
    [Fact]
    public void O_NOME_DA_COLUNA_IGNORA_ACENTO()
    {
        var t = LeitorCsv.Ler(Utf8("nome;Observações\nMaria;cliente antigo"))!;
        Assert.Equal("cliente antigo", t.Valor(0, "observacoes"));
    }

    /// <summary>⚠️ VÁRIOS NOMES PARA A MESMA COLUNA. "telefone", "celular" e "whatsapp" são a mesma
    /// coisa na planilha que o cliente tem na mão. Recusar o arquivo por causa do cabeçalho é a
    /// pior primeira impressão que uma importação pode dar.</summary>
    [Fact]
    public void ACEITA_SINONIMO_DE_COLUNA_O_PRIMEIRO_QUE_EXISTIR()
    {
        var t = LeitorCsv.Ler(Utf8("nome;celular\nMaria;84988887777"))!;

        Assert.Equal("84988887777", t.Valor(0, "telefone", "celular", "whatsapp"));
        Assert.Equal("", t.Valor(0, "telefone"));
    }

    [Fact]
    public void COLUNA_AUSENTE_OU_CELULA_VAZIA_DEVOLVEM_A_MESMA_COISA()
    {
        var t = LeitorCsv.Ler(Utf8("nome;telefone;email\nMaria;84988887777"))!;

        Assert.Equal("", t.Valor(0, "email"));        // a linha nem tem a terceira célula
        Assert.Equal("", t.Valor(0, "inexistente"));
    }

    // ==================================================================== nada para ler
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    public void ARQUIVO_SEM_CABECALHO_DEVOLVE_NULO(string conteudo) =>
        Assert.Null(LeitorCsv.Ler(Utf8(conteudo)));

    [Fact]
    public void CABECALHO_SEM_LINHA_NENHUMA_E_TABELA_VAZIA()
    {
        var t = LeitorCsv.Ler(Utf8("nome;telefone\n"))!;

        Assert.Equal(0, t.Quantidade);
        Assert.True(t.Tem("nome"));
    }

    // ==================================================================== o ciclo fechado
    /// <summary>⚠️ O TESTE QUE JUSTIFICA OS DOIS ARQUIVOS MORAREM JUNTOS.
    ///
    /// O que o Nexora EXPORTA tem de voltar para dentro do Nexora. Escrito com `CsvBrasileiro.Gerar`
    /// — BOM, `;`, CRLF e o escape dele — e lido de volta aqui. Se um dos lados mudar sozinho, é
    /// este teste que percebe, e não o cliente reimportando a própria lista de clientes.</summary>
    [Fact]
    public void O_QUE_O_NEXORA_EXPORTA_O_NEXORA_LE_DE_VOLTA()
    {
        var arquivo = CsvBrasileiro.Gerar([
            ["nome", "telefone", "observações"],
            ["Maria Silva", "5584988887777", "cliente antigo"],
            ["Silva; João", "5584999996666", "disse \"volto amanhã\""],
            ["Ana", "5584911112222", ""]
        ]);

        var t = LeitorCsv.Ler(arquivo)!;

        Assert.Equal(3, t.Quantidade);
        Assert.Equal("Maria Silva", t.Valor(0, "nome"));
        Assert.Equal("cliente antigo", t.Valor(0, "observacoes"));

        // As duas armadilhas do escape, de volta inteiras.
        Assert.Equal("Silva; João", t.Valor(1, "nome"));
        Assert.Equal("disse \"volto amanhã\"", t.Valor(1, "observacoes"));

        Assert.Equal("", t.Valor(2, "observacoes"));
    }
}
