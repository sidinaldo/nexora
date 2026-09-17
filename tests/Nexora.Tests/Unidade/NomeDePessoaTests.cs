using Nexora.Core.Texto;
using Nexora.Core.Whatsapp;

namespace Nexora.Tests.Unidade;

/// <summary>COMO TRATAR ALGUÉM NUMA MENSAGEM QUE SAI DO PRODUTO.
///
/// ⚠️ ESTE ARQUIVO NASCE DE UMA MENSAGEM QUE CHEGOU NO WHATSAPP DE UM CLIENTE:
///
///     "Oi, (84)! Passando para saber se você ainda tem interesse."
///
/// O nome do contato ERA o telefone formatado — `CanonicalizadorTelefone.Formatar`, que é o que o
/// produto usa quando o WhatsApp não manda `pushName` —, e "primeiro nome" era `Split(' ')[0]`.
/// Nenhum dos dois estava errado sozinho.
///
/// Teste puro, sem banco, porque a regra é de texto e vale para os três lugares que a usam: a
/// régua, o e-mail e o semeador. Antes eram três cópias, e o defeito valia para as três.</summary>
public class NomeDePessoaTests
{
    [Theory]
    // ---------- gente
    [InlineData("Maria Silva", "Maria")]
    [InlineData("  Maria  Silva  ", "Maria")]
    [InlineData("Maria", "Maria")]
    [InlineData("Ysia Braglia", "Ysia")]
    // Uma letra é um nome: quem se apresenta como "J" é chamado de "J".
    [InlineData("J R Tolkien", "J")]
    // ---------- não é gente
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    // ⚠️ O CASO DO DEFEITO, nas formas em que o telefone aparece como nome.
    [InlineData("(84) 95278-7173", null)]
    [InlineData("(83) 9527-7173", null)]
    [InlineData("5584952787173", null)]
    [InlineData("+55 84 95278-7173", null)]
    // ---------- pedaço sem letra não invalida o que vem depois
    [InlineData("123 Maria", "Maria")]
    public void O_PRIMEIRO_NOME_E_O_PRIMEIRO_PEDACO_COM_LETRA(string? nome, string? esperado) =>
        Assert.Equal(esperado, NomeDePessoa.Primeiro(nome));

    /// <summary>⚠️ NENHUM TELEFONE QUE O PRÓPRIO PRODUTO GERA PASSA POR NOME.
    ///
    /// O teste acima fixa quatro grafias à mão, e à mão é justamente o problema: quem mexer em
    /// `Formatar` não vai lembrar de vir aqui. Este percorre a saída REAL do formatador para uma
    /// faixa de números — celular com 9 e fixo com 8 dígitos, DDDs diferentes — e exige que nenhum
    /// deles seja aceito como nome.</summary>
    [Fact]
    public void O_TELEFONE_FORMATADO_PELO_PRODUTO_NUNCA_VIRA_NOME()
    {
        var telefones = new[]
        {
            "5584988887777", "5511940653647", "5583952787138", "558432211234", "556199998888"
        };

        foreach (var t in telefones)
        {
            var comoNome = CanonicalizadorTelefone.Formatar(t);

            Assert.Null(NomeDePessoa.Primeiro(comoNome));
            Assert.Equal("Oi!", NomeDePessoa.Saudacao("Oi", comoNome));
        }
    }

    /// <summary>⚠️ A PONTUAÇÃO MUDA JUNTO, e é por isso que a saudação inteira sai daqui em vez de
    /// só o nome. Devolver "" e deixar o template com `$"Oi, {nome}!"` daria "Oi, !" — um defeito
    /// trocado por outro, e igualmente visível para quem recebe.</summary>
    [Theory]
    [InlineData("Oi", "Maria Silva", "Oi, Maria!")]
    [InlineData("Oi", "(84) 95278-7173", "Oi!")]
    [InlineData("Oi", null, "Oi!")]
    [InlineData("Olá", "Rafael Souza", "Olá, Rafael!")]
    [InlineData("Olá", "   ", "Olá!")]
    public void A_SAUDACAO_NUNCA_FICA_COM_VIRGULA_SOLTA(
        string abertura, string? nome, string esperado) =>
        Assert.Equal(esperado, NomeDePessoa.Saudacao(abertura, nome));
}
