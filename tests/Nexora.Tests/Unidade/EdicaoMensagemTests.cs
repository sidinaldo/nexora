using Nexora.Infra.Evolution;
using Nexora.Tests.Integracao;

namespace Nexora.Tests.Unidade;

/// <summary>A DECIFRAGEM DA EDIÇÃO, contra os bytes de verdade do "Falr" → "Fale".
///
/// Testes puros, sem banco. Cifrar e decifrar com o mesmo código provaria só que ele concorda
/// consigo mesmo; o que importa é bater com o que o WhatsApp manda — e isso só um vetor real prova.</summary>
public class EdicaoMensagemTests
{
    private static EdicaoMensagem.Edicao EdicaoReal() =>
        EdicaoMensagem.Ler(PayloadEvolution.EdicaoReal("inst", "558494259023@s.whatsapp.net", "WA-EDIT"))!;

    private static byte[] SegredoReal() =>
        EdicaoMensagem.SegredoDa(PayloadEvolution.OriginalComSegredo("inst", "558494259023@s.whatsapp.net", "Falr"))!;

    [Fact]
    public void O_LID_ABRE_A_EDICAO_REAL()
    {
        var texto = EdicaoMensagem.Decifrar(EdicaoReal(), SegredoReal(), [PayloadEvolution.LidQueAbre]);

        Assert.Equal("Fale", texto);
    }

    /// <summary>⚠️ O TELEFONE NÃO ABRE. É o JID que a Evolution entrega no aviso de mensagem nova,
    /// e foi o primeiro a ser tentado — este teste é o registro de que ele não serve.</summary>
    [Fact]
    public void O_TELEFONE_NAO_ABRE()
    {
        var texto = EdicaoMensagem.Decifrar(EdicaoReal(), SegredoReal(), ["558494259023@s.whatsapp.net"]);

        Assert.Null(texto);
    }

    /// <summary>Os candidatos são tentados em ordem e o errado não estraga nada: o GCM recusa a
    /// chave, e o próximo é tentado.</summary>
    [Fact]
    public void CANDIDATO_ERRADO_ANTES_DO_CERTO_NAO_ATRAPALHA()
    {
        var texto = EdicaoMensagem.Decifrar(EdicaoReal(), SegredoReal(),
            [null, "558494259023@s.whatsapp.net", PayloadEvolution.LidQueAbre]);

        Assert.Equal("Fale", texto);
    }

    [Fact]
    public void A_EDICAO_LIDA_APONTA_PARA_A_ORIGINAL()
    {
        var edicao = EdicaoReal();

        Assert.Equal(PayloadEvolution.IdQueFoiEditada, edicao.Alvo);
        Assert.Equal(12, edicao.Iv!.Length);
        Assert.Equal(32, SegredoReal().Length);
    }

    /// <summary>A confirmação traz o LID do APARELHO às vezes (`:9`). A chave usa o da pessoa.</summary>
    [Theory]
    [InlineData("181286291378345:9@lid", "181286291378345@lid")]
    [InlineData("181286291378345@lid", "181286291378345@lid")]
    [InlineData("558494281968@s.whatsapp.net", null)]
    [InlineData("120363000000000000@g.us", null)]
    [InlineData(null, null)]
    public void O_LID_SAI_SEM_O_APARELHO(string? remoteJid, string? esperado)
    {
        Assert.Equal(esperado, EdicaoMensagem.LidDe(remoteJid));
    }
}
