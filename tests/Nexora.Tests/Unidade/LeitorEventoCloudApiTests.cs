using System.Text;
using Nexora.Infra.CloudApi;
using Nexora.Tests.Integracao;

namespace Nexora.Tests.Unidade;

/// <summary>O JSON DA CLOUD API, TRADUZIDO (INT-XX).
///
/// O leitor e o unico lugar que conhece o formato da Meta. O que ele entrega precisa ser o que a
/// thread da Evolution ja mostra: texto legivel, anexo pelo id, e nada que suma calado.</summary>
public class LeitorEventoCloudApiTests
{
    private const string Pnid = "1090000000001";
    private const string Waba = "2090000000001";
    private const string De = "5584988887777";

    private static EventoCloud LerUma(params string[] mensagens) =>
        LeitorEventoCloudApi.Ler(PayloadCloudApi.Recebidas(Pnid, De, "Maria", mensagens));

    // ==================================================================== a entrega
    [Fact]
    public void A_ENTREGA_SE_ABRE_EM_MUDANCAS_COM_O_NUMERO_E_A_CONTA()
    {
        var corpo = PayloadCloudApi.Entrega(
            PayloadCloudApi.Conta(Waba,
                PayloadCloudApi.Recebidas(Pnid, De, "Maria", PayloadCloudApi.Texto(De, "wamid.A", "oi")),
                PayloadCloudApi.Status("1090000000002", PayloadCloudApi.UmStatus("wamid.B", "read", De))),
            // O evento de template nao cita numero: so a conta.
            PayloadCloudApi.Conta("2090000000009",
                """{"field":"message_template_status_update","value":{"event":"APPROVED"}}"""));

        var mudancas = LeitorEventoCloudApi.Mudancas(corpo);

        Assert.Equal(3, mudancas.Count);
        Assert.Equal("messages", mudancas[0].Campo);
        Assert.Equal(Pnid, mudancas[0].PhoneNumberId);
        Assert.Equal(Waba, mudancas[0].WabaId);
        Assert.Equal("1090000000002", mudancas[1].PhoneNumberId);
        Assert.Equal("message_template_status_update", mudancas[2].Campo);
        Assert.Null(mudancas[2].PhoneNumberId);
        Assert.Equal("2090000000009", mudancas[2].WabaId);
    }

    [Theory]
    [InlineData("nao e json")]
    [InlineData("""{"object":"page","entry":[{"id":"1","changes":[{"field":"feed","value":{}}]}]}""")]
    [InlineData("""{"object":"whatsapp_business_account"}""")]
    [InlineData("""{"object":"whatsapp_business_account","entry":"torta"}""")]
    public void ENTREGA_QUE_NAO_E_DO_WHATSAPP_OU_TORTA_NAO_TEM_MUDANCA(string corpo)
    {
        Assert.Empty(LeitorEventoCloudApi.Mudancas(Encoding.UTF8.GetBytes(corpo)));
    }

    // ==================================================================== mensagens
    [Fact]
    public void TEXTO_COM_O_NOME_DO_PERFIL_E_A_HORA_DA_META()
    {
        var evento = LerUma(PayloadCloudApi.Texto(De, "wamid.T1", "Oi, vi o anúncio"));

        var m = Assert.Single(evento.Mensagens);
        Assert.Equal("wamid.T1", m.Id);
        Assert.Equal(De, m.De);
        Assert.Equal("text", m.Tipo);
        Assert.Equal("Oi, vi o anúncio", m.Texto);
        Assert.Null(m.MidiaId);
        Assert.InRange(m.Quando, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("Maria", evento.Nomes[De]);
        // A mensagem crua vai junto: e nela que o `referral` do anuncio e lido.
        Assert.Contains("\"wamid.T1\"", m.Json);
    }

    [Fact]
    public void RESPOSTA_CITANDO_OUTRA_TRAZ_O_ID_CITADO()
    {
        var evento = LerUma(PayloadCloudApi.Texto(De, "wamid.T2", "essa aqui",
            ""","context":{"from":"15550001111","id":"wamid.NOSSA"}"""));

        Assert.Equal("wamid.NOSSA", Assert.Single(evento.Mensagens).CitadaId);
    }

    [Fact]
    public void ANEXO_VEM_PELO_ID_COM_A_LEGENDA_E_O_NOME_DO_ARQUIVO()
    {
        var evento = LerUma(
            PayloadCloudApi.Midia(De, "wamid.F1", "image", "MEDIA-1", "image/jpeg", legenda: "olha"),
            PayloadCloudApi.Midia(De, "wamid.F2", "document", "MEDIA-2", "application/pdf",
                                  arquivo: "proposta.pdf"),
            PayloadCloudApi.Midia(De, "wamid.F3", "audio", "MEDIA-3", "audio/ogg; codecs=opus"));

        Assert.Equal(3, evento.Mensagens.Count);
        var foto = evento.Mensagens[0];
        var doc = evento.Mensagens[1];
        var audio = evento.Mensagens[2];
        Assert.Equal("MEDIA-1", foto.MidiaId);
        Assert.Equal("olha", foto.Texto);
        Assert.Equal("MEDIA-2", doc.MidiaId);
        Assert.Equal("proposta.pdf", doc.NomeArquivo);
        Assert.Equal("MEDIA-3", audio.MidiaId);
        Assert.Null(audio.Texto);
    }

    [Fact]
    public void LOCALIZACAO_VIRA_O_NOME_DO_LUGAR_E_O_LINK_DO_MAPA()
    {
        var evento = LerUma(PayloadCloudApi.DoTipo(De, "wamid.L1", "location",
            """{"latitude":-5.79,"longitude":-35.2,"name":"Loja Centro","address":"Rua A, 10"}"""));

        Assert.Equal("📍 Localização: Loja Centro\n\nhttps://www.google.com/maps?q=-5.79,-35.2",
            Assert.Single(evento.Mensagens).Texto);
    }

    [Fact]
    public void CONTATO_COMPARTILHADO_VIRA_O_NOME_DELE()
    {
        var evento = LerUma(PayloadCloudApi.DoTipo(De, "wamid.C1", "contacts",
            """[{"name":{"formatted_name":"João Pedreiro"},"phones":[{"phone":"+5584911112222"}]}]"""));

        Assert.Equal("👤 Contato: João Pedreiro", Assert.Single(evento.Mensagens).Texto);
    }

    /// <summary>O cliente tocou num botao ou num item de lista: o que ele "disse" e o titulo.</summary>
    [Fact]
    public void RESPOSTA_DE_BOTAO_E_DE_LISTA_VIRA_O_TITULO()
    {
        var evento = LerUma(
            PayloadCloudApi.DoTipo(De, "wamid.I1", "interactive",
                """{"type":"button_reply","button_reply":{"id":"sim","title":"Quero sim"}}"""),
            PayloadCloudApi.DoTipo(De, "wamid.I2", "interactive",
                """{"type":"list_reply","list_reply":{"id":"p2","title":"Plano 2","description":"R$ 99"}}"""),
            PayloadCloudApi.DoTipo(De, "wamid.B1", "button", """{"payload":"PARAR","text":"Parar"}"""));

        Assert.Equal(new string?[] { "Quero sim", "Plano 2\n\nR$ 99", "Parar" },
            evento.Mensagens.Select(m => m.Texto));
    }

    /// <summary>Tipo que o leitor nao conhece nao some: vem sem texto, com o tipo, e vira o rotulo
    /// de "nao suportada" na thread.</summary>
    [Fact]
    public void TIPO_DESCONHECIDO_CHEGA_SEM_TEXTO_E_COM_O_TIPO()
    {
        var evento = LerUma(PayloadCloudApi.DoTipo(De, "wamid.O1", "order", """{"catalog_id":"1"}"""));

        var m = Assert.Single(evento.Mensagens);
        Assert.Equal("order", m.Tipo);
        Assert.Null(m.Texto);
        Assert.Null(m.MidiaId);
    }

    [Fact]
    public void MENSAGEM_SEM_ID_OU_SEM_REMETENTE_E_DESCARTADA()
    {
        var evento = LerUma(
            """{"from":"5584988887777","timestamp":"1","type":"text","text":{"body":"sem id"}}""",
            """{"id":"wamid.X","timestamp":"1","type":"text","text":{"body":"sem from"}}""");

        Assert.Empty(evento.Mensagens);
    }

    // ==================================================================== status
    [Fact]
    public void STATUS_FAILED_TRAZ_O_CODIGO_E_O_MOTIVO()
    {
        var evento = LeitorEventoCloudApi.Ler(PayloadCloudApi.Status(Pnid,
            PayloadCloudApi.UmStatus("wamid.S1", "delivered", De),
            PayloadCloudApi.UmStatus("wamid.S2", "failed", De, 131047, "Re-engagement message")));

        Assert.Equal(2, evento.Status.Count);
        Assert.Equal(new StatusCloud("wamid.S1", "delivered", null), evento.Status[0]);
        Assert.Equal(new StatusCloud("wamid.S2", "failed", "131047 Re-engagement message"), evento.Status[1]);
        Assert.Empty(evento.Mensagens);
    }

    [Theory]
    [InlineData("nao e json")]
    [InlineData("""{"field":"messages"}""")]
    [InlineData("""{"field":"messages","value":{"messages":"torta","statuses":7}}""")]
    public void MUDANCA_TORTA_NAO_LANCA(string json)
    {
        var evento = LeitorEventoCloudApi.Ler(json);

        Assert.Empty(evento.Mensagens);
        Assert.Empty(evento.Status);
    }
}
