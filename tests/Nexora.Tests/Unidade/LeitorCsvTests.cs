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

    // ==================================================================== achados da revisão
    /// <summary>⚠️ ASPA NO MEIO DO CAMPO É SÓ UM CARACTERE. Uma célula `TV 42"` (polegadas) punha o
    /// parser em modo aspas e engolia o resto do arquivo num campo só: 800 linhas viravam 4, sem
    /// erro apontando a aspa. Pela regra do RFC 4180 — e do Excel — aspa só abre campo no começo.</summary>
    [Fact]
    public void ASPA_SOLTA_NO_MEIO_DO_CAMPO_NAO_ENGOLE_O_ARQUIVO()
    {
        var t = LeitorCsv.Ler(Utf8("nome;observacoes\nMaria;TV 42\"\nJoão;ok\nAna;tudo certo"))!;

        Assert.Equal(3, t.Quantidade);
        Assert.Equal("TV 42\"", t.Valor(0, "observacoes"));
        Assert.Equal("João", t.Valor(1, "nome"));
        Assert.Equal("Ana", t.Valor(2, "nome"));
    }

    /// <summary>⚠️ O NÚMERO DA LINHA É O DO EXCEL MESMO COM LINHA EM BRANCO. A conta `i + 2`
    /// errava a partir da primeira linha vazia, e o dono editava a vizinha da linha com problema.</summary>
    [Fact]
    public void O_NUMERO_DA_LINHA_SOBREVIVE_A_LINHAS_EM_BRANCO()
    {
        // 1 cabeçalho · 2 Maria · 3 em branco · 4 João · 5 só separador · 6 Ana
        var t = LeitorCsv.Ler(Utf8("nome;telefone\nMaria;1\n\nJoão;2\n;\nAna;3"))!;

        Assert.Equal(3, t.Quantidade);
        Assert.Equal(2, t.NumeroLinha(0));
        Assert.Equal(4, t.NumeroLinha(1));
        Assert.Equal(6, t.NumeroLinha(2));
    }

    /// <summary>E com linhas em branco ANTES do cabeçalho — a planilha que tem um título em cima.</summary>
    [Fact]
    public void O_NUMERO_DA_LINHA_CONTA_O_QUE_VEM_ANTES_DO_CABECALHO()
    {
        // 1 em branco · 2 só separadores · 3 cabeçalho · 4 Maria
        var t = LeitorCsv.Ler(Utf8("\n;;\nnome;telefone\nMaria;1"))!;

        Assert.Equal(4, t.NumeroLinha(0));
    }

    /// <summary>E o campo com quebra de linha DENTRO das aspas continua sendo uma linha só, como no
    /// Excel: a conta é por registro, não por quebra física.</summary>
    [Fact]
    public void QUEBRA_DENTRO_DE_ASPAS_NAO_EMPURRA_A_NUMERACAO()
    {
        var t = LeitorCsv.Ler(Utf8("nome;obs\nMaria;\"mora na\nsegunda rua\"\nJoão;ok"))!;

        Assert.Equal(2, t.NumeroLinha(0));
        Assert.Equal(3, t.NumeroLinha(1));
    }

    /// <summary>⚠️ LINHA EM BRANCO ANTES DO CABEÇALHO NÃO MUDA O SEPARADOR. A escolha era pela
    /// primeira linha FÍSICA: vazia, os dois empatavam em zero, `;` ganhava, e um CSV de vírgula
    /// virava UMA coluna "nome,telefone" — "falta a coluna nome" sobre um arquivo que a tinha.</summary>
    [Theory]
    [InlineData("\nnome,telefone\nMaria,84988887777")]
    [InlineData("\n\n\nnome,telefone\nMaria,84988887777")]
    [InlineData(",,\nnome,telefone\nMaria,84988887777")]
    public void O_SEPARADOR_VEM_DO_CABECALHO_DE_VERDADE(string conteudo)
    {
        var t = LeitorCsv.Ler(Utf8(conteudo))!;

        Assert.True(t.Tem("nome"));
        Assert.True(t.Tem("telefone"));
        Assert.Equal("84988887777", t.Valor(0, "telefone"));
    }

    // ==================================================================== a codificação
    /// <summary>⚠️ WINDOWS-1252, E NÃO LATIN-1. É o que o Excel em português grava sem UTF-8. Os
    /// dois concordam nas letras acentuadas e divergem nas aspas curvas, travessão e reticências —
    /// que no Latin-1 são caracteres de controle invisíveis.</summary>
    [Fact]
    public void PLANILHA_DO_EXCEL_EM_1252_LE_ACENTO_E_ASPAS_CURVAS()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var arquivo = Encoding.GetEncoding(1252).GetBytes(
            "nome;observacoes\nJoão Conceição;disse “volto amanhã” — talvez…");

        var t = LeitorCsv.Ler(arquivo)!;

        Assert.Equal("João Conceição", t.Valor(0, "nome"));
        Assert.Equal("disse “volto amanhã” — talvez…", t.Valor(0, "observacoes"));
    }

    /// <summary>⚠️ UM `U+FFFD` LEGÍTIMO NÃO DERRUBA O ARQUIVO PARA OUTRA TABELA. O emoji quebrado de
    /// um pushName, exportado e reimportado, fazia o arquivo INTEIRO ser relido como Latin-1, e
    /// todo acento virava "JoÃ£o" sem erro. `U+FFFD` escrito em UTF-8 é UTF-8 válido.</summary>
    [Fact]
    public void CARACTERE_DE_SUBSTITUICAO_NO_TEXTO_NAO_TROCA_A_CODIFICACAO()
    {
        var t = LeitorCsv.Ler(Utf8("nome;obs\nJoão �;ação"))!;

        Assert.Equal("João �", t.Valor(0, "nome"));
        Assert.Equal("ação", t.Valor(0, "obs"));
    }

    // ==================================================================== o cabeçalho original
    /// <summary>A tela de mapeamento mostra as perguntas que o cliente criou no formulário do Meta,
    /// e ele precisa reconhecê-las: acento, maiúscula e interrogação continuam lá.</summary>
    [Fact]
    public void O_CABECALHO_ORIGINAL_CHEGA_COMO_O_CLIENTE_ESCREVEU()
    {
        var t = LeitorCsv.Ler(Utf8("full_name;Qual seu orçamento?;EMAIL\nMaria;até 5 mil;m@x.com"))!;

        Assert.Equal(["full_name", "Qual seu orçamento?", "EMAIL"], t.Cabecalho);

        var r = t.Registro(0);
        Assert.Equal("até 5 mil", r["Qual seu orçamento?"]);
        Assert.Equal("m@x.com", r["EMAIL"]);
    }

    /// <summary>Coluna repetida aparece uma vez só — a primeira, a mesma que `Valor` usa. E célula
    /// que falta na linha vem vazia, não quebra.</summary>
    [Fact]
    public void O_REGISTRO_SEGUE_AS_MESMAS_REGRAS_DE_VALOR()
    {
        var t = LeitorCsv.Ler(Utf8("nome;email;Email\nMaria;primeiro@x.com;segundo@x.com\nJoão"))!;

        Assert.Equal(["nome", "email"], t.Cabecalho);
        Assert.Equal("primeiro@x.com", t.Registro(0)["email"]);
        Assert.Equal("", t.Registro(1)["email"]);
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
