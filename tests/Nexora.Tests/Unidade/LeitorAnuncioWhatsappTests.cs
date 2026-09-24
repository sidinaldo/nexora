using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;

namespace Nexora.Tests.Unidade;

/// <summary>O ANÚNCIO CLIQUE-PARA-WHATSAPP NO PAYLOAD CRU (INT-4, commit 8).
///
/// ===================== POR QUE ESTE LEITOR É TOLERANTE =====================
/// O diagnóstico do commit 0 estabeleceu que **o canal chega** — a Evolution iça o `contextInfo` para
/// a raiz do `data` e o entrega sem podar. O que ele **não** estabeleceu são os nomes e o aninhamento
/// exatos no caso "anúncio": nunca houve lead de anúncio no banco de desenvolvimento.
///
/// Então os testes abaixo exercitam as TRÊS posições documentadas e as DUAS grafias de cada campo, e
/// o mais importante deles é o que prova que o leitor **falha fechado**: payload sem anúncio não gera
/// rastro, e o comportamento fica idêntico ao de antes deste commit.
/// ========================================================================</summary>
public class LeitorAnuncioWhatsappTests
{
    /// <summary>O `contextInfo` içado para a raiz do `data` — a posição que o payload real deste
    /// banco usa (é onde o `entryPointConversionSource` aparece).</summary>
    private const string NaRaizDoData = """
        {
          "event": "messages.upsert",
          "instance": "Juba",
          "data": {
            "key": { "id": "WA-1", "remoteJid": "5584970001111@s.whatsapp.net", "fromMe": false },
            "pushName": "Maria do Anúncio",
            "message": { "conversation": "vi o anúncio" },
            "contextInfo": {
              "entryPointConversionSource": "ctwa_ad",
              "externalAdReply": {
                "title": "Promoção de março — 30% off",
                "body": "Fale com a gente agora",
                "sourceType": "ad",
                "sourceId": "120210000000000123",
                "sourceUrl": "https://fb.me/abc?utm_x=1",
                "ctwaClid": "ARAaBBccDD-clique"
              }
            },
            "messageTimestamp": 1786230002
          }
        }
        """;

    // ==================================================================== as três posições
    [Fact]
    public void ACHA_O_ANUNCIO_NA_RAIZ_DO_DATA()
    {
        var a = LeitorAnuncioWhatsapp.Ler(NaRaizDoData)!;

        Assert.Equal("ARAaBBccDD-clique", a.CtwaClid);
        Assert.Equal("120210000000000123", a.AnuncioId);
        Assert.Equal("Promoção de março — 30% off", a.Titulo);

        // ⚠️ A QUERY STRING SAI. Mesma razão do rastro do site: é URL de terceiro, e não temos como
        // auditar o que vai nela.
        Assert.Equal("https://fb.me/abc", a.Url);
    }

    [Fact]
    public void ACHA_O_ANUNCIO_DENTRO_DO_extendedTextMessage()
    {
        // A segunda posição documentada: o `contextInfo` do próprio tipo de mensagem.
        var payload = """
            {
              "data": {
                "key": { "id": "WA-2", "remoteJid": "5584970002222@s.whatsapp.net" },
                "message": {
                  "extendedTextMessage": {
                    "text": "vi o anúncio",
                    "contextInfo": {
                      "externalAdReply": { "ctwaClid": "aninhado-1", "sourceId": "999" }
                    }
                  }
                }
              }
            }
            """;

        var a = LeitorAnuncioWhatsapp.Ler(payload)!;
        Assert.Equal("aninhado-1", a.CtwaClid);
        Assert.Equal("999", a.AnuncioId);
    }

    [Fact]
    public void ACHA_O_ANUNCIO_DENTRO_DO_contextInfo_DE_UMA_MIDIA()
    {
        // A terceira: quem clica no anúncio e manda uma FOTO primeiro. Fixar um caminho só perderia
        // este caso em silêncio — é por isso que a busca é recursiva.
        var payload = """
            {
              "data": {
                "message": {
                  "imageMessage": {
                    "caption": "é esse produto?",
                    "contextInfo": {
                      "externalAdReply": { "ctwaClid": "na-midia", "title": "Anúncio da foto" }
                    }
                  }
                }
              }
            }
            """;

        var a = LeitorAnuncioWhatsapp.Ler(payload)!;
        Assert.Equal("na-midia", a.CtwaClid);
        Assert.Equal("Anúncio da foto", a.Titulo);
    }

    // ==================================================================== as duas grafias
    [Fact]
    public void ACEITA_A_FORMA_DA_API_OFICIAL_TAMBEM()
    {
        // `referral` com `snake_case` é o formato da API oficial do WhatsApp Cloud; `externalAdReply`
        // com `camelCase` é o do Baileys, que é o que a Evolution roda. Aceitar as duas é o que faz
        // este leitor continuar valendo no dia em que o cliente migrar.
        var payload = """
            {
              "entry": [{ "changes": [{ "value": { "messages": [{
                "referral": {
                  "source_type": "ad",
                  "source_id": "120211111",
                  "source_url": "https://fb.me/xyz",
                  "ctwa_clid": "oficial-1",
                  "headline": "Anúncio oficial"
                }
              }] } }] }]
            }
            """;

        var a = LeitorAnuncioWhatsapp.Ler(payload)!;
        Assert.Equal("oficial-1", a.CtwaClid);
        Assert.Equal("120211111", a.AnuncioId);
        Assert.Equal("Anúncio oficial", a.Titulo);
    }

    // ==================================================================== falha fechado
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("não é json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"data":{"message":{"conversation":"oi"}}}""")]
    public void SEM_ANUNCIO_NAO_HA_RASTRO(string? payload)
    {
        // ⚠️ O TESTE MAIS IMPORTANTE DESTE ARQUIVO. Os nomes dos campos vêm da documentação, não dos
        // nossos dados — então o comportamento sem reconhecer nada tem de ser IDÊNTICO ao de antes
        // deste commit. Nulo aqui é "não mexi em nada".
        Assert.Null(LeitorAnuncioWhatsapp.Ler(payload));
    }

    [Fact]
    public void O_contextInfo_DE_UMA_CONVERSA_NORMAL_NAO_VIRA_ANUNCIO()
    {
        // É o payload REAL deste banco: alguém que clicou num link `wa.me` comum. Ele tem
        // `contextInfo` e `entryPointConversionSource`, e NÃO é anúncio — confundir os dois poria
        // metade dos leads do QR Code como "veio de anúncio pago".
        var payload = """
            {
              "data": {
                "key": { "id": "WA-3", "remoteJid": "558494259023@s.whatsapp.net" },
                "message": { "conversation": "Olá! Tenho interesse. #ntjb" },
                "contextInfo": {
                  "mentionedJid": [],
                  "entryPointConversionSource": "click_to_chat_link",
                  "entryPointConversionDelaySeconds": 5
                }
              }
            }
            """;

        Assert.Null(LeitorAnuncioWhatsapp.Ler(payload));
    }

    [Fact]
    public void UM_BLOCO_DE_ANUNCIO_VAZIO_TAMBEM_NAO_VIRA_RASTRO()
    {
        // `externalAdReply` presente e sem nenhum campo útil: uma linha de rastro dali não diria de
        // onde ninguém veio.
        Assert.Null(LeitorAnuncioWhatsapp.Ler(
            """{"data":{"contextInfo":{"externalAdReply":{"sourceType":"ad"}}}}"""));
    }

    // ==================================================================== os tetos
    [Fact]
    public void CADA_CAMPO_E_CORTADO_NO_TETO_DA_COLUNA_ONDE_ELE_VAI_MORAR()
    {
        // Texto que vem da internet, e a coluna tem largura. Sem o corte, o INSERT estoura "value too
        // long" — e o que se perde não é o rastro: é o LEAD, porque a mensagem para de ser processada.
        var gigante = new string('x', 5000);
        var payload = $$"""
            {
              "data": { "contextInfo": { "externalAdReply": {
                "ctwaClid": "{{gigante}}",
                "sourceId": "{{gigante}}",
                "sourceUrl": "https://fb.me/{{gigante}}",
                "title": "{{gigante}}"
              } } }
            }
            """;

        var a = LeitorAnuncioWhatsapp.Ler(payload)!;

        Assert.Equal(RastreioLead.TetoIdentificador, a.CtwaClid!.Length);
        Assert.Equal(RastreioLead.TetoUtm, a.AnuncioId!.Length);
        Assert.Equal(RastreioLead.TetoUrl, a.Url!.Length);
        Assert.Equal(RastreioLead.TetoUtm, a.Titulo!.Length);
    }

    [Fact]
    public void UM_JSON_FUNDO_DEMAIS_NAO_TRAVA_A_LEITURA()
    {
        // A busca é recursiva porque o aninhamento é o que se desconhece — e um teto de profundidade
        // é o que impede isso de virar uma porta para JSON malicioso de mil níveis.
        var payload = "{\"a\":" + string.Concat(Enumerable.Repeat("{\"b\":", 40))
                    + "{\"externalAdReply\":{\"ctwaClid\":\"fundo\"}}"
                    + string.Concat(Enumerable.Repeat("}", 40)) + "}";

        // Não acha (está muito além do teto) e, o que importa, não estoura.
        Assert.Null(LeitorAnuncioWhatsapp.Ler(payload));
    }
}
