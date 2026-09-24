using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>AS REGRAS PURAS DO RASTRO (INT-4).
///
/// Tudo aqui entra por um endpoint PÚBLICO, sem sessão, com texto que vem da internet. Cada regra
/// testada abaixo é uma que, se faltar, vira erro 500 no formulário do site do cliente — ou dado
/// pessoal guardado sem que ninguém tenha decidido guardar.</summary>
public class RegrasRastreioTests
{
    // ==================================================================== a query string
    [Fact]
    public void A_URL_PERDE_A_QUERY_STRING_E_O_FRAGMENTO()
    {
        // ⚠️ POR QUE ISTO É REGRA E NÃO ZELO. A URL é do site DO CLIENTE, montada por ele, e não
        // temos como auditar o que ele põe ali — e-mail, CPF e token de sessão em query string são
        // comuns. Guardar tudo importaria dado pessoal de terceiro sem saber, e a anonimização não
        // limparia o que não sabe que existe.
        Assert.Equal("https://cliente.com.br/promo",
            RegrasRastreio.SemQuery("https://cliente.com.br/promo?email=joao@exemplo.com&utm_source=ig"));

        Assert.Equal("https://cliente.com.br/promo",
            RegrasRastreio.SemQuery("https://cliente.com.br/promo#depoimentos"));

        // O que interessa continua inteiro: qual página trouxe.
        Assert.Equal("https://cliente.com.br/promo",
            RegrasRastreio.SemQuery("https://cliente.com.br/promo"));

        Assert.Null(RegrasRastreio.SemQuery(null));
        Assert.Null(RegrasRastreio.SemQuery("   "));

        // URL que é SÓ query não sobra nada — e nada é nulo, não string vazia.
        Assert.Null(RegrasRastreio.SemQuery("?fbclid=abc"));
    }

    // ==================================================================== tetos
    [Fact]
    public void CADA_CAMPO_E_CORTADO_NO_TETO_DA_PROPRIA_COLUNA()
    {
        // Sem isto, o INSERT estoura com "value too long for type character varying(200)" — e
        // estoura no formulário do site, onde o visitante vê 500 e o cliente perde o lead.
        var gigante = new RastreioDoSite(
            UtmSource: new string('a', 500),
            Pagina: "https://cliente.com.br/" + new string('b', 2000),
            Fbclid: new string('c', 900));

        var r = gigante.Normalizar();

        Assert.Equal(RastreioLead.TetoUtm, r.UtmSource!.Length);
        Assert.Equal(RastreioLead.TetoUrl, r.Pagina!.Length);
        Assert.Equal(RastreioLead.TetoIdentificador, r.Fbclid!.Length);
    }

    [Fact]
    public void CAMPO_VAZIO_VIRA_NULO__E_NAO_STRING_VAZIA()
    {
        // O formulário manda o campo oculto vazio quando não achou o parâmetro na URL — que é o
        // caso da maioria dos visitantes. Guardar "" faria o relatório de campanha ter uma linha
        // em branco com metade dos leads dentro.
        var r = new RastreioDoSite(UtmSource: "", UtmCampaign: "   ", Fbclid: "").Normalizar();

        Assert.Null(r.UtmSource);
        Assert.Null(r.UtmCampaign);
        Assert.Null(r.Fbclid);
        Assert.False(r.TemAlgo());
    }

    [Fact]
    public void RASTRO_SEM_NADA_DENTRO_NAO_TEM_NADA__E_COM_UM_CAMPO_TEM()
    {
        Assert.False(new RastreioDoSite().TemAlgo());
        Assert.True(new RastreioDoSite(UtmCampaign: "promo").TemAlgo());
        Assert.True(new RastreioDoSite(Fbclid: "IwAR-x").TemAlgo());

        // ⚠️ `EventoId` SOZINHO NÃO CONTA. Ele é o id de deduplicação que o navegador gera em toda
        // visita, com ou sem anúncio: se contasse, todo lead de um site com o código novo criaria
        // uma linha de rastro que não diz de onde ninguém veio.
        Assert.False(new RastreioDoSite(EventoId: Guid.NewGuid()).TemAlgo());
    }

    // ==================================================================== os identificadores
    [Fact]
    public void SO_OS_IDENTIFICADORES_QUE_TEM_VALOR_ENTRAM_NO_JSON()
    {
        var json = new RastreioDoSite(Fbclid: "IwAR-abc", Gclid: "").Normalizar().Identificadores();

        var lido = RegrasRastreio.Ler(json);

        Assert.Equal("IwAR-abc", lido[RegrasRastreio.ChaveFbclid]);
        Assert.Single(lido);

        // Chave presente com nulo dentro obrigaria todo leitor a distinguir "não veio" de "veio
        // vazio" — e não existe diferença entre as duas.
        Assert.DoesNotContain(RegrasRastreio.ChaveGclid, lido.Keys);
    }

    [Fact]
    public void OS_CINCO_IDENTIFICADORES_DO_SITE_VAO_E_VOLTAM_INTEIROS()
    {
        // As chaves são contrato entre quem escreve (a captação) e quem lê (o montador do evento).
        // Duas grafias de "fbclid" não dariam erro nenhum — só um `user_data` silenciosamente
        // vazio, e o casamento com o clique deixando de acontecer sem aviso.
        var json = new RastreioDoSite(
            Fbclid: "IwAR-1", Fbp: "fb.1.123.456", Fbc: "fb.1.789.IwAR-1",
            Gclid: "GCL-1", Ttclid: "TT-1").Identificadores();

        var lido = RegrasRastreio.Ler(json);

        Assert.Equal("IwAR-1", lido[RegrasRastreio.ChaveFbclid]);
        Assert.Equal("fb.1.123.456", lido[RegrasRastreio.ChaveFbp]);
        Assert.Equal("fb.1.789.IwAR-1", lido[RegrasRastreio.ChaveFbc]);
        Assert.Equal("GCL-1", lido[RegrasRastreio.ChaveGclid]);
        Assert.Equal("TT-1", lido[RegrasRastreio.ChaveTtclid]);
        Assert.Equal(5, lido.Count);
    }

    [Fact]
    public void LER_NUNCA_LANCA__PORQUE_A_ANONIMIZACAO_ESVAZIA_O_JSON()
    {
        // Depois de anonimizar, `identificadores` é `{}`. E numa correção manual alguém pode ter
        // escrito qualquer coisa ali. Quem lê isto está montando um evento — lançar ali derrubaria
        // o fechamento de uma venda por causa de um campo de rastreio.
        Assert.Empty(RegrasRastreio.Ler("{}"));
        Assert.Empty(RegrasRastreio.Ler(null));
        Assert.Empty(RegrasRastreio.Ler(""));
        Assert.Empty(RegrasRastreio.Ler("nao e json"));
        Assert.Empty(RegrasRastreio.Ler("[1,2,3]"));
    }
}
