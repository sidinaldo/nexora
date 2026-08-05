using Nexora.Core.Whatsapp;

namespace Nexora.Tests;

/// <summary>A funcao mais load-bearing do produto. Se o cadastro e o WhatsApp nao
/// canonicalizarem igual, a mensagem recebida nao casa com contato nenhum e some SEM ERRO NO
/// LOG. Estes testes existem para que essa falha silenciosa vire uma falha barulhenta.</summary>
public class CanonicalizadorTelefoneTests
{
    [Theory]
    // Como a pessoa digita no cadastro (sem DDI).
    [InlineData("(84) 98888-7777", "5584988887777")]
    [InlineData("84 98888-7777", "5584988887777")]
    [InlineData("84988887777", "5584988887777")]
    [InlineData("(84) 8888-7777", "558488887777")]      // 10 digitos: fixo/celular antigo
    // Como o WhatsApp entrega (ja com DDI).
    [InlineData("5584988887777", "5584988887777")]
    [InlineData("+55 (84) 98888-7777", "5584988887777")]
    [InlineData("55 84 9 8888 7777", "5584988887777")]
    // Zeros a esquerda (discagem interurbana anotada no cadastro).
    [InlineData("084988887777", "5584988887777")]
    public void Canonicaliza_para_DDI_mais_DDD_mais_numero(string entrada, string esperado)
    {
        Assert.Equal(esperado, CanonicalizadorTelefone.Canonicalizar(entrada));
    }

    [Fact]
    public void As_duas_pontas_do_sistema_chegam_no_mesmo_valor()
    {
        // O TESTE QUE IMPORTA: o cadastro digita de um jeito, o WhatsApp entrega de outro.
        var doCadastro = CanonicalizadorTelefone.Canonicalizar("(84) 98888-7777");
        var doWhatsApp = CanonicalizadorTelefone.Canonicalizar("5584988887777@s.whatsapp.net".Split('@')[0]);

        Assert.Equal(doCadastro, doWhatsApp);
    }

    [Fact]
    public void Variantes_cobrem_com_e_sem_o_nono_digito()
    {
        // Numero COM o 9: a variante tira, porque o WhatsApp as vezes entrega o JID sem ele
        // (contas habilitadas antes de 2012).
        var comNove = CanonicalizadorTelefone.Variantes("5584988887777");
        Assert.Contains("5584988887777", comNove);
        Assert.Contains("558488887777", comNove);
        Assert.Equal(2, comNove.Count);

        // Numero SEM o 9: a variante poe.
        var semNove = CanonicalizadorTelefone.Variantes("558488887777");
        Assert.Contains("558488887777", semNove);
        Assert.Contains("5584988887777", semNove);
        Assert.Equal(2, semNove.Count);
    }

    [Fact]
    public void Variantes_das_duas_formas_se_cruzam()
    {
        // Consequencia pratica: nao importa em qual forma o contato foi cadastrado, a busca
        // por variantes encontra a mensagem recebida na outra forma.
        var a = CanonicalizadorTelefone.Variantes("5584988887777");
        var b = CanonicalizadorTelefone.Variantes("558488887777");

        Assert.NotEmpty(a.Intersect(b));
        Assert.Equal(a.OrderBy(x => x), b.OrderBy(x => x));
    }

    [Fact]
    public void Fixo_de_oito_digitos_com_nono_digito_ganha_a_variante_movel()
    {
        // Numero de 8 digitos que NAO comeca com 9 (fixo antigo). A variante ainda e gerada:
        // e barato, e o custo de perder a mensagem e alto.
        var variantes = CanonicalizadorTelefone.Variantes("558433334444");
        Assert.Contains("558433334444", variantes);
        Assert.Contains("5584933334444", variantes);
    }

    [Theory]
    [InlineData("5584988887777", true)]     // 13 = 55 + DDD + 9 digitos
    [InlineData("558488887777", true)]      // 12 = 55 + DDD + 8 digitos
    [InlineData("(84) 98888-7777", true)]   // valida depois de canonicalizar
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("123", false)]              // curto demais
    [InlineData("98888777", false)]         // sem DDD
    [InlineData("5584988887777999", false)] // longo demais
    [InlineData("abc", false)]              // sem digito nenhum
    [InlineData("1234567890123", false)]    // 13 digitos mas nao comeca com 55
    public void Numero_invalido_falha_alto_em_vez_de_virar_lixo(string? entrada, bool valido)
    {
        // Aceitar lixo aqui e o pior caminho: o contato seria criado com um telefone que nunca
        // casa com mensagem nenhuma, e ninguem descobriria por que ele "nao responde".
        Assert.Equal(valido, CanonicalizadorTelefone.EhValido(entrada));
    }

    [Theory]
    [InlineData("5584988887777", "(84) 98888-7777")]
    [InlineData("558488887777", "(84) 8888-7777")]
    public void Formata_para_exibicao(string canonico, string esperado)
    {
        // Usado como NOME do contato quando o WhatsApp nao manda pushName — a coluna e NOT NULL
        // e "" seria pior que o numero.
        Assert.Equal(esperado, CanonicalizadorTelefone.Formatar(canonico));
    }

    [Fact]
    public void Canonicalizar_e_idempotente()
    {
        // O valor gravado no banco passa por aqui de novo a cada mensagem recebida; se a
        // segunda passada mudasse o resultado, o contato deixaria de casar consigo mesmo.
        var uma = CanonicalizadorTelefone.Canonicalizar("(84) 98888-7777");
        var duas = CanonicalizadorTelefone.Canonicalizar(uma);
        Assert.Equal(uma, duas);
    }
}
