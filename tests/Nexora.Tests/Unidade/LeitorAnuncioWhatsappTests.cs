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

    // ==================================================================== o diagnóstico
    /* ===================== POR QUE ESTES TESTES EXISTEM =====================
       Os nomes dos campos vieram da documentação da Meta, nunca de uma mensagem real. Se estiverem
       errados, o leitor devolve nulo e NINGUÉM DESCOBRE — o lead entra, a venda fecha, e só o elo
       com o anúncio se perde em silêncio.

       O `LerComDiagnostico` existe para que o primeiro clique real de qualquer cliente entregue os
       nomes verdadeiros de graça, no lugar de um anúncio pago só para descobrir isso.
       ======================================================================== */

    [Fact]
    public void UM_BLOCO_SEM_CLIQUE_DEVOLVE_AS_CHAVES_QUE_CHEGARAM()
    {
        // O caso "era anúncio e eu não entendi nada": o bloco existe, o rastro não nasce, e o que
        // sobra de útil são os NOMES que vieram.
        var leitura = LeitorAnuncioWhatsapp.LerComDiagnostico(
            """{"data":{"contextInfo":{"externalAdReply":{"sourceType":"ad","ctwa_id":"x"}}}}""");

        Assert.Null(leitura.Anuncio);
        Assert.True(leitura.BlocoAchado);
        Assert.Equal(["ctwa_id", "sourceType"], leitura.Chaves);
    }

    [Fact]
    public void UM_BLOCO_COM_TITULO_E_SEM_CLIQUE_TAMBEM_DEVOLVE_AS_CHAVES()
    {
        // ⚠️ O CASO MAIS TRAIÇOEIRO, e o que uma condição presa a "não reconheci NADA" deixaria
        // passar: se a Meta renomear só o `ctwaClid`, o rastro É criado e a tela do contato mostra o
        // anúncio — mas a conversão degrada para `chat` sem ninguém perceber.
        var leitura = LeitorAnuncioWhatsapp.LerComDiagnostico("""
            {"data":{"contextInfo":{"externalAdReply":{
              "title":"Promoção de março","sourceId":"120210000000000123","clique":"ARAaBB"}}}}
            """);

        Assert.NotNull(leitura.Anuncio);
        Assert.Null(leitura.Anuncio!.CtwaClid);
        Assert.Equal(["clique", "sourceId", "title"], leitura.Chaves);
    }

    [Fact]
    public void UM_ANUNCIO_COMPLETO_TAMBEM_RELATA_AS_CHAVES()
    {
        // O diagnóstico relata o FATO — o que veio no bloco —, e não um veredito. Quem decide que
        // `ctwa_clid` ausente merece aviso é o processador, ao lado da linha de log que explica a
        // decisão. Misturar as duas coisas aqui prenderia a política dentro do Core.
        var leitura = LeitorAnuncioWhatsapp.LerComDiagnostico(NaRaizDoData);

        Assert.Equal("ARAaBBccDD-clique", leitura.Anuncio!.CtwaClid);
        Assert.True(leitura.BlocoAchado);
        Assert.Contains("ctwaClid", leitura.Chaves);
    }

    [Fact]
    public void A_CONVERSA_NORMAL_NAO_DEVOLVE_CHAVE_NENHUMA()
    {
        // ⚠️ O TESTE ANTI-RUÍDO. É o payload REAL deste banco — `contextInfo` de um link `wa.me`
        // comum, sem bloco de anúncio. Se o diagnóstico cair para as chaves do `contextInfo` quando
        // não há bloco, toda mensagem de QR Code vira um aviso e o log deixa de valer.
        var leitura = LeitorAnuncioWhatsapp.LerComDiagnostico("""
            {"data":{"contextInfo":{
              "mentionedJid":[],"entryPointConversionSource":"click_to_chat_link"}}}
            """);

        Assert.Null(leitura.Anuncio);
        Assert.False(leitura.BlocoAchado);
        Assert.Empty(leitura.Chaves);
    }

    [Fact]
    public void AS_CHAVES_NAO_TRAZEM_VALOR_NENHUM()
    {
        // ⚠️ A GUARDA DE LGPD, e a regressão que um futuro "logar o valor ajudaria a depurar"
        // introduziria. O achado #8 do `SEGURANCA.md` proíbe corpo de payload em log — e lá o
        // atenuante era "só no caminho de erro". Este relatório sai no caminho NORMAL.
        var leitura = LeitorAnuncioWhatsapp.LerComDiagnostico("""
            {"data":{"contextInfo":{"externalAdReply":{
              "sourceType":"ad","titulo":"Promoção secreta do cliente"}}}}
            """);

        Assert.Equal(["sourceType", "titulo"], leitura.Chaves);
        Assert.DoesNotContain(leitura.Chaves, chave => chave.Contains("Promoção"));
    }

    [Fact]
    public void UM_BLOCO_COM_CHAVES_DEMAIS_NAO_VIRA_UMA_LINHA_DE_LOG_GIGANTE()
    {
        // O payload vem da internet, e o maior visto neste banco tem 36 KB — sem teto, uma linha de
        // log viraria milhares de nomes.
        var campos = string.Join(",", Enumerable.Range(0, 200).Select(i => $"\"campo{i:D3}\":\"v\""));
        var leitura = LeitorAnuncioWhatsapp.LerComDiagnostico(
            """{"data":{"contextInfo":{"externalAdReply":{""" + campos + "}}}}");

        Assert.Equal(12, leitura.Chaves.Length);
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
