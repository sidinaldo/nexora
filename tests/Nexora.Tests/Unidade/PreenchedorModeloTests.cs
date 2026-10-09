using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;

namespace Nexora.Tests.Unidade;

/// <summary>AS VARIAVEIS DE UM TEMPLATE (INT-XX).
///
/// Quem escreve usa nomes (`{{nome}}`); a Meta so entende numeros (`{{1}}`). Errar a ponte manda
/// "Olá Loja Exemplo" para a Maria — e a Meta nao reclama, porque para ela e so texto.</summary>
public class PreenchedorModeloTests
{
    private static readonly DadosDoModelo Dados = new("Maria da Silva", "Loja Centro", "João Pedro");

    [Fact]
    public void AS_VARIAVEIS_SAEM_NA_ORDEM_DA_PRIMEIRA_APARICAO_E_SEM_REPETIR()
    {
        var variaveis = PreenchedorModelo.VariaveisDe(
            "Oi {{empresa}} aqui! {{ nome }}, o {{vendedor}} da {{empresa}} vai te atender.");

        Assert.Equal(["empresa", "nome", "vendedor"], variaveis);
    }

    [Fact]
    public void PARA_A_META_CADA_NOME_VIRA_O_NUMERO_DA_SUA_POSICAO()
    {
        const string corpo = "Olá {{nome}}, aqui é da {{empresa}}. Até mais, {{nome}}!";
        var variaveis = PreenchedorModelo.VariaveisDe(corpo);

        Assert.Equal("Olá {{1}}, aqui é da {{2}}. Até mais, {{1}}!", PreenchedorModelo.ParaMeta(corpo, variaveis));
        Assert.Equal(["Maria", "Loja Centro"], PreenchedorModelo.Valores(variaveis, Dados));
    }

    /// <summary>O texto que fica na thread e o que o cliente le: o PRIMEIRO nome, como se fala.</summary>
    [Fact]
    public void O_TEXTO_PREENCHIDO_USA_O_PRIMEIRO_NOME()
    {
        Assert.Equal("Olá Maria, o João da Loja Centro te espera.",
            PreenchedorModelo.Preencher("Olá {{nome}}, o {{vendedor}} da {{empresa}} te espera.", Dados));
    }

    /// <summary>A Meta recusa parametro vazio (132000). Contato sem nome de verdade vira "cliente";
    /// envio sem vendedor fala em nome da empresa.</summary>
    [Fact]
    public void SEM_NOME_OU_SEM_VENDEDOR_O_VALOR_NUNCA_FICA_VAZIO()
    {
        var semNome = new DadosDoModelo("(84) 98888-7777", "Loja Centro", null);

        Assert.Equal(["cliente", "Loja Centro"],
            PreenchedorModelo.Valores(["nome", "vendedor"], semNome));
    }

    [Theory]
    [InlineData("Olá {{cpf}}, tudo bem?", "{{cpf}} não existe")]
    [InlineData("{{nome}}, tudo bem?", "começa ou termina")]
    [InlineData("Tudo bem, {{nome}}", "começa ou termina")]
    [InlineData("   ", "Escreva o texto")]
    public void CORPO_FORA_DA_REGRA_E_RECUSADO_COM_O_QUE_CORRIGIR(string corpo, string trecho)
    {
        var erro = Assert.Throws<RegraDeNegocioException>(() => PreenchedorModelo.VariaveisDe(corpo));

        Assert.Contains(trecho, erro.Message);
    }

    [Fact]
    public void CORPO_ACIMA_DO_TETO_DA_META_E_RECUSADO()
    {
        var corpo = "Oi " + new string('a', PreenchedorModelo.TetoCorpo);

        Assert.Throws<RegraDeNegocioException>(() => PreenchedorModelo.VariaveisDe(corpo));
    }

    [Theory]
    [InlineData("Boas-vindas à loja", "boas_vindas_a_loja")]
    [InlineData("  Retorno  da   Proposta!! ", "retorno_da_proposta")]
    [InlineData("NPS_2026", "nps_2026")]
    public void O_NOME_VIRA_O_DA_META(string informado, string esperado)
    {
        Assert.Equal(esperado, PreenchedorModelo.NomeParaMeta(informado));
    }

    [Fact]
    public void NOME_SEM_LETRA_NEM_NUMERO_E_RECUSADO()
    {
        Assert.Throws<RegraDeNegocioException>(() => PreenchedorModelo.NomeParaMeta("!!! ---"));
    }
}
