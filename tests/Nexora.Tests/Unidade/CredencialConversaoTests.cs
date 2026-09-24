using Nexora.Core.Entidades;

namespace Nexora.Tests.Unidade;

/// <summary>O PORTÃO DE ENVIO DA CREDENCIAL DE ANÚNCIO (INT-4).
///
/// `PodeEnviar` é a única pergunta que separa "o rastro está guardado" de "o dado pessoal desta
/// pessoa saiu daqui para a Meta". Espelha `WebhookSaida.Assina` de propósito: uma cópia da
/// resposta, num lugar, chamada por todo mundo.
///
/// Está em teste de unidade porque é regra pura — e porque quando ela erra, erra para fora: não
/// existe tela onde alguém veja que mandou o que não devia.</summary>
public class CredencialConversaoTests
{
    [Fact]
    public void A_CREDENCIAL_COMPLETA_ENVIA_OS_DOIS_EVENTOS()
    {
        var c = Completa();

        Assert.True(c.PodeEnviar(TipoConversao.Lead));
        Assert.True(c.PodeEnviar(TipoConversao.Compra));
    }

    [Fact]
    public void SEM_CONSENTIMENTO_DECLARADO_NADA_SAI()
    {
        // ⚠️ A REGRA QUE MAIS IMPORTA AQUI. Guardar o rastro é tratamento de dado no banco do
        // próprio cliente; mandá-lo para a Meta é COMPARTILHAMENTO com terceiro, e isso precisa
        // de base legal no site dele — que só ele sabe se tem.
        //
        // E há a razão prática: sem esta trava, a fila acumularia PII hasheada que não pode
        // drenar. Fila que nunca drena é dívida silenciosa.
        var c = Completa();
        c.ConsentimentoEm = null;
        c.ConsentimentoPor = null;

        Assert.False(c.PodeEnviar(TipoConversao.Lead));
        Assert.False(c.PodeEnviar(TipoConversao.Compra));
    }

    [Fact]
    public void SEM_TOKEN_NADA_SAI__NEM_COM_O_PIXEL_PREENCHIDO()
    {
        // O caso real: a pessoa colou o Pixel ID, salvou, e foi buscar o token depois. A tela
        // aceita esse meio-caminho de propósito; o que não pode é o publicador enfileirar evento
        // que nunca vai ter com o que ser enviado.
        var c = Completa();
        c.Token = null;
        Assert.False(c.PodeEnviar(TipoConversao.Lead));

        // Espaço em branco é a mesma coisa que vazio: é o que sobra de um campo "limpo" na mão.
        c.Token = "   ";
        Assert.False(c.PodeEnviar(TipoConversao.Lead));
    }

    [Fact]
    public void O_INTERRUPTOR_DA_PESSOA_E_O_DO_SISTEMA_SAO_SEPARADOS()
    {
        // `Ativo` é o interruptor de quem configurou. `DesativadaEm` é o motor desligando sozinho
        // porque a Meta recusou o token.
        //
        // Se fossem a mesma coluna, religar depois de trocar o token exigiria adivinhar quem
        // desligou — e o passo de "Primeiros passos" não saberia se deve acender de novo.
        var desligadaPelaPessoa = Completa();
        desligadaPelaPessoa.Ativo = false;
        Assert.False(desligadaPelaPessoa.PodeEnviar(TipoConversao.Lead));

        var desligadaPeloMotor = Completa();
        desligadaPeloMotor.DesativadaEm = new DateTime(2026, 3, 20, 10, 0, 0, DateTimeKind.Utc);
        desligadaPeloMotor.DesativadaMotivo = "A Meta recusou o token.";
        Assert.False(desligadaPeloMotor.PodeEnviar(TipoConversao.Lead));

        // E a credencial que a pessoa deixou ligada continua ligada mesmo depois de um erro
        // transitório — só a desativação explícita fecha o portão.
        Assert.True(Completa().PodeEnviar(TipoConversao.Lead));
    }

    [Fact]
    public void CADA_EVENTO_TEM_O_PROPRIO_INTERRUPTOR()
    {
        // O caso do cliente que quer otimizar por venda e não por lead: mandar `Lead` de tudo
        // ensinaria o algoritmo a procurar quem preenche formulário, que é justamente o que este
        // bloco existe para corrigir.
        var soCompra = Completa();
        soCompra.EmLead = false;

        Assert.False(soCompra.PodeEnviar(TipoConversao.Lead));
        Assert.True(soCompra.PodeEnviar(TipoConversao.Compra));

        var soLead = Completa();
        soLead.EmCompra = false;

        Assert.True(soLead.PodeEnviar(TipoConversao.Lead));
        Assert.False(soLead.PodeEnviar(TipoConversao.Compra));
    }

    private static CredencialConversao Completa() => new()
    {
        EmpresaId = 1,
        Plataforma = PlataformaConversao.Meta,
        Identificador = "1234567890",
        Token = "EAAG-token-de-teste",
        Ativo = true,
        EmLead = true,
        EmCompra = true,
        ConsentimentoEm = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        ConsentimentoPor = 7
    };
}
