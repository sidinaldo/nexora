using Nexora.Core.Email;

namespace Nexora.Tests.Unidade;

/// <summary>Os textos dos e-mails (BUG-XX): o artigo que errava com nome masculino ("na Salão
/// Bela"), o rodapé que soava como uso suspeito, e o convite e a senha nova sem dizer quem pediu.</summary>
public class TextosDosEmailsTests
{
    [Fact]
    public void O_CONVITE_DIZ_QUEM_CONVIDOU_E_NAO_ERRA_O_ARTIGO()
    {
        var email = MontadorEmail.Convite("a@b.com", "Ana", "Salão Bela", "https://x/c/t", quemConvidou: "Carla");

        Assert.Contains("Carla convidou você para usar o Nexora com a equipe de Salão Bela.", email.Texto);
        Assert.DoesNotContain("da Salão", email.Html);
        Assert.Contains("ignore este e-mail", email.Html);
    }

    [Fact]
    public void A_SENHA_PEDIDA_PELO_DONO_NAO_DIZ_SE_VOCE_NAO_PEDIU()
    {
        var pelodono = MontadorEmail.ResetSenha("a@b.com", "Ana", "https://x/r/t", pedidoPor: "Carla");
        Assert.Contains("Carla pediu uma nova senha para você", pelodono.Texto);
        Assert.DoesNotContain("Se você não pediu", pelodono.Texto);

        var esqueci = MontadorEmail.ResetSenha("a@b.com", "Ana", "https://x/r/t");
        Assert.Contains("Se você não pediu", esqueci.Texto);
    }

    [Fact]
    public void O_RODAPE_NAO_SOA_COMO_USO_SUSPEITO()
    {
        var email = MontadorEmail.ResetSenha("a@b.com", "Ana", "https://x/r/t");

        Assert.Contains("seu endereço está cadastrado no Nexora", email.Html);
        Assert.DoesNotContain("alguém usou seu endereço", email.Html);
    }
}
