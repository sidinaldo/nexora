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

    // ==================================================== INT-5 · por que esta parado

    /// <summary>===================== O ESPELHO, E E ISTO QUE SEGURA OS DOIS JUNTOS =====================
    ///
    /// Nao compara frase nenhuma. Compara a pergunta ("pode enviar?") com a resposta ("ha motivo?")
    /// em TODA combinacao das cinco condicoes — 2^5 por tipo, os dois tipos.
    ///
    /// ⚠️ E O QUE ACONTECE SE ALGUEM PUSER UMA CONDICAO NOVA NO `PodeEnviar` E ESQUECER A FRASE:
    /// a credencial passa a nao poder enviar, `MotivosParados` devolve lista vazia, e a tela fica
    /// MUDA exatamente no caso novo — que e quando ninguem sabe o que esta acontecendo. E o modo
    /// de falha que o INT-5 inteiro existe para matar, reaparecendo pela porta de dentro.
    ///
    /// Um teste por frase nao pegaria isso: cada um continuaria passando.
    /// =======================================================================================</summary>
    [Theory]
    [InlineData(TipoConversao.Lead)]
    [InlineData(TipoConversao.Compra)]
    public void O_MOTIVO_EXISTE_SEMPRE_QUE_O_ENVIO_ESTA_PARADO(TipoConversao tipo)
    {
        for (var combinacao = 0; combinacao < 32; combinacao++)
        {
            var c = Completa();
            c.Ativo = (combinacao & 1) == 0;
            c.DesativadaEm = (combinacao & 2) == 0 ? null : new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
            c.DesativadaMotivo = c.DesativadaEm is null ? null : "token recusado";
            c.ConsentimentoEm = (combinacao & 4) == 0 ? c.ConsentimentoEm : null;
            c.Token = (combinacao & 8) == 0 ? c.Token : "   ";
            if ((combinacao & 16) != 0) { c.EmLead = false; c.EmCompra = false; }

            var pode = c.PodeEnviar(tipo);
            var motivos = c.MotivosParados(tipo);

            Assert.True(pode == (motivos.Count == 0),
                $"combinacao {combinacao}: PodeEnviar={pode} mas {motivos.Count} motivo(s). "
                + "Condicao nova no PodeEnviar sem frase no MotivosParados — ou o contrario.");
        }
    }

    [Fact]
    public void COM_TUDO_CERTO_NAO_HA_MOTIVO_NENHUM()
    {
        var c = Completa();

        Assert.Empty(c.MotivosParados(TipoConversao.Lead));
        Assert.Empty(c.MotivosParados(TipoConversao.Compra));
    }

    [Fact]
    public void TODOS_OS_MOTIVOS_APARECEM_JUNTOS_E_NAO_SO_O_PRIMEIRO()
    {
        // "Conserte isto" seguido de "agora conserte aquilo" e o jeito mais rapido de alguem
        // desistir no meio. Quem acabou de conectar costuma ter DUAS pendencias, nao uma.
        var c = Completa();
        c.ConsentimentoEm = null;
        c.Token = null;

        var motivos = c.MotivosParados(TipoConversao.Compra);

        Assert.Equal(2, motivos.Count);
        Assert.Contains(motivos, m => m.Contains("consentimento"));
        Assert.Contains(motivos, m => m.Contains("token"));
    }

    [Fact]
    public void O_MOTIVO_DA_RECUSA_DA_META_CARREGA_A_RAZAO_DELA()
    {
        // ⚠️ A unica condicao que nao e escolha de quem configura. Sem o motivo da Meta a frase
        // vira "deu errado" — e o dono nao tem o que fazer com isso.
        var c = Completa();
        c.DesativadaEm = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        c.DesativadaMotivo = "token expirado";

        Assert.Contains("token expirado", Assert.Single(c.MotivosParados(TipoConversao.Compra)));
    }

    [Fact]
    public void O_EVENTO_DESMARCADO_APARECE_SO_NO_TIPO_DELE()
    {
        // ⚠️ O DEFEITO LATENTE QUE ISTO EXPOE: desmarcar "Purchase" nao mexe no lead, e a tela
        // antiga mostrava a credencial como `enviando` nos dois casos. As vendas sumiam em
        // silencio com o selo verde na tela.
        var c = Completa();
        c.EmCompra = false;

        Assert.Empty(c.MotivosParados(TipoConversao.Lead));
        Assert.Contains("Purchase", Assert.Single(c.MotivosParados(TipoConversao.Compra)));
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
