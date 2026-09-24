using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Conversoes;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Evolution;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O EVENTO ENTRANDO NA FILA (INT-4). Nada vai para a rede aqui.
///
/// ===================== O QUE ESTE ARQUIVO PROTEGE =====================
/// Três coisas, e cada uma tem custo do lado de fora:
///
///   • **sem consentimento, nada entra na fila** — porque fila com PII que não pode drenar é dívida
///     silenciosa, e porque religar faria sair o que ninguém decidiu mandar agora;
///   • **o `Purchase` da mesma venda entra UMA vez** — reabrir e refechar é gesto normal na tela, e
///     o segundo evento ensinaria o algoritmo a gastar o dobro;
///   • **o `Lead` sai para todo contato novo, com rastro ou sem** — inclusive o do WhatsApp, que é o
///     caminho de maior volume deste público.
///  ======================================================================</summary>
[Collection("banco")]
public class PublicadorConversoesDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset Marco = new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);

    private static SalvarCredencial Conectado => new(
        "1234567890123456", "EAAGtokenbemlongoparaMascarar", null, true, true, true, true);

    // ==================================================================== o portão
    [Fact]
    public async Task SEM_CREDENCIAL_NENHUM_EVENTO_ENTRA_NA_FILA()
    {
        // É o estado de toda empresa que não usa a integração — ou seja, a maioria. Enfileirar aqui
        // acumularia PII hasheada para ninguém.
        var (db, tx, amb) = await PrepararAsync("sem-cred");
        using var _ = db; using var __ = tx;

        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        Assert.Empty(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SEM_CONSENTIMENTO_DECLARADO_NENHUM_EVENTO_ENTRA_NA_FILA()
    {
        // ⚠️ A REGRA DE LGPD. Guardar o rastro é tratamento no banco do próprio cliente; mandá-lo
        // para a Meta é COMPARTILHAMENTO com terceiro. Sem a declaração, o rastro fica e o evento
        // não nasce.
        var (db, tx, amb) = await PrepararAsync("sem-consent");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado with { ConsentimentoDeclarado = false }, default);
        db.ChangeTracker.Clear();

        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        Assert.Empty(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CADA_EVENTO_RESPEITA_O_PROPRIO_INTERRUPTOR()
    {
        // O cliente que quer otimizar só por venda desliga o `Lead`. Ignorar isso mandaria a Meta
        // procurar quem preenche formulário — o oposto do que ele pediu.
        var (db, tx, amb) = await PrepararAsync("interruptor");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado with { EmLead = false }, default);
        db.ChangeTracker.Clear();

        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync());

        await amb.Publicador.PublicarCompraAsync(amb.Cenario.Negociacao.Id, default);
        db.ChangeTracker.Clear();
        Assert.Single(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    }

    // ==================================================================== o lead
    [Fact]
    public async Task O_LEAD_COM_RASTRO_LEVA_O_FBC_DO_CLIQUE_E_A_HORA_DO_FATO()
    {
        var (db, tx, amb) = await PrepararAsync("lead-rastro");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);

        var doNavegador = Guid.NewGuid();
        var quandoClicou = Marco.UtcDateTime.AddDays(-2);

        db.RastreiosLead.Add(new RastreioLead
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Cenario.Contato.Id,
            Fonte = FonteRastreio.FormularioSite,
            UtmCampaign = "promo-de-marco",
            Pagina = "https://cliente.com.br/promo",
            Identificadores = RegrasRastreio.Montar(
                (RegrasRastreio.ChaveFbc, "fb.1.1700000000.IwAR-original"),
                (RegrasRastreio.ChaveFbp, "fb.1.1700000000.111")),
            Ip = "203.0.113.7",
            UserAgent = "Mozilla/5.0 (iPhone)",
            EventoId = doNavegador,
            OcorridoEm = quandoClicou
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Publicador.PublicarLeadAsync(
            await db.Contatos.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(c => c.Id == amb.Cenario.Contato.Id), default);
        db.ChangeTracker.Clear();

        var evento = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();

        // ⚠️ O ID DO NAVEGADOR, e não um novo: é ele que a Meta casa com o evento do pixel. Sem
        // isto o lead conta duas vezes para quem tem pixel instalado.
        Assert.Equal(doNavegador, evento.EventoId);

        // ⚠️ A HORA DO CLIQUE, e não a de agora: a Meta atribui pelo `event_time`, e é isso que faz
        // o lead de anteontem ser creditado ao dia em que ele aconteceu.
        Assert.Equal(quandoClicou, evento.OcorridoEm);
        Assert.Equal(quandoClicou.AddDays(7), evento.ExpiraEm);
        Assert.Equal(StatusConversao.Pendente, evento.Status);

        var corpo = JsonNode.Parse(evento.Payload)!["data"]![0]!;
        Assert.Equal("Lead", (string)corpo["event_name"]!);
        Assert.Equal("website", (string)corpo["action_source"]!);
        Assert.Equal("https://cliente.com.br/promo", (string)corpo["event_source_url"]!);
        Assert.Equal("fb.1.1700000000.IwAR-original", (string)corpo["user_data"]!["fbc"]!);
        Assert.Equal("203.0.113.7", (string)corpo["user_data"]!["client_ip_address"]!);

        // O telefone vai hasheado, e o telefone em claro NÃO aparece no payload.
        Assert.Equal(HashPessoal.Telefone(amb.Cenario.Contato.Telefone),
            (string)corpo["user_data"]!["ph"]![0]!);
        Assert.DoesNotContain(amb.Cenario.Contato.Telefone, evento.Payload);
    }

    [Fact]
    public async Task O_LEAD_SEM_RASTRO_TAMBEM_SAI__E_E_O_CASO_DA_MAIORIA()
    {
        // ⚠️ O RASTRO MELHORA A ATRIBUIÇÃO, NÃO A HABILITA. O casamento por telefone funciona
        // sozinho, e boa parte deste público não tem site — publicar só com rastro faria o bloco
        // servir à minoria.
        var (db, tx, amb) = await PrepararAsync("lead-sem-rastro");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        var evento = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        var corpo = JsonNode.Parse(evento.Payload)!["data"]![0]!;

        // `chat`, que é o canal real de quem chegou pelo WhatsApp.
        Assert.Equal("chat", (string)corpo["action_source"]!);
        Assert.Null(corpo["event_source_url"]);
        Assert.NotNull(corpo["user_data"]!["ph"]);
    }

    [Fact]
    public async Task O_MESMO_CONTATO_NAO_GERA_DOIS_LEADS__E_NAO_DA_ERRO()
    {
        // A colisão é o caso NORMAL acontecendo duas vezes, não um erro: o `ON CONFLICT DO NOTHING`
        // devolve em silêncio, como `ServicoCaptura` já faz com telefone repetido.
        var (db, tx, amb) = await PrepararAsync("lead-repetido");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        Assert.Single(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync());

        // ⚠️ E SEM ERRO NO LOG — é isto que faz o `ON CONFLICT DO NOTHING` ser load-bearing. Sem
        // ele o INSERT estoura, o `catch` engole, e o resultado acima fica idêntico: uma linha.
        Assert.Empty(amb.Log.Erros);
    }

    // ==================================================================== a compra
    [Fact]
    public async Task A_COMPRA_LEVA_O_VALOR_E_A_HORA_DO_FECHAMENTO()
    {
        var (db, tx, amb) = await PrepararAsync("compra");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 1450.50m, null, null, default);
        db.ChangeTracker.Clear();

        // ⚠️ SAI PELO PRÓPRIO `MarcarGanhoAsync`, e não por uma chamada de teste: é o ponto real, e
        // é ele que precisa estar ligado.
        var eventos = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var compra = Assert.Single(eventos.Where(e => e.Tipo == TipoConversao.Compra));

        Assert.Equal(amb.Cenario.Negociacao.Id, compra.NegociacaoId);
        Assert.Equal(Marco.UtcDateTime, compra.OcorridoEm);

        var corpo = JsonNode.Parse(compra.Payload)!["data"]![0]!;
        Assert.Equal("Purchase", (string)corpo["event_name"]!);
        Assert.Equal("system_generated", (string)corpo["action_source"]!);
        Assert.Equal(1450.50m, (decimal)corpo["custom_data"]!["value"]!);
        Assert.Equal("BRL", (string)corpo["custom_data"]!["currency"]!);
    }

    [Fact]
    public async Task REABRIR_E_REFECHAR_A_MESMA_VENDA_MANDA_UM_PURCHASE_SO()
    {
        // ⚠️ O TESTE QUE PROTEGE O DINHEIRO DO CLIENTE. Reabrir e refechar é gesto normal na tela;
        // o segundo `Purchase` não é registro repetido — é o algoritmo aprendendo que aquele público
        // converte o dobro do que converte, e gastando a verba dele em cima disso.
        var (db, tx, amb) = await PrepararAsync("refechar");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        await amb.Publicador.PublicarCompraAsync(amb.Cenario.Negociacao.Id, default);
        await amb.Publicador.PublicarCompraAsync(amb.Cenario.Negociacao.Id, default);
        db.ChangeTracker.Clear();

        Assert.Single(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Tipo == TipoConversao.Compra).ToListAsync());

        // Repetir é o caso NORMAL acontecendo duas vezes, não erro.
        Assert.Empty(amb.Log.Erros);
    }

    [Fact]
    public async Task O_MESMO_CONTATO_TEM_LEAD_E_COMPRA__E_COM_IDS_DE_EVENTO_DIFERENTES()
    {
        // ⚠️ O ID DA COMPRA É NOVO, e nunca o do navegador. Reusar o `event_id` do lead faria a Meta
        // tratar a compra como repetição dele e descartá-la — justamente o evento que o bloco existe
        // para entregar.
        var (db, tx, amb) = await PrepararAsync("lead-e-compra");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);

        var doNavegador = Guid.NewGuid();
        db.RastreiosLead.Add(new RastreioLead
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Cenario.Contato.Id,
            Fonte = FonteRastreio.FormularioSite,
            Identificadores = "{}",
            EventoId = doNavegador,
            OcorridoEm = Marco.UtcDateTime
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        await amb.Publicador.PublicarCompraAsync(amb.Cenario.Negociacao.Id, default);
        db.ChangeTracker.Clear();

        var eventos = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(e => e.Id).ToListAsync();

        Assert.Equal(2, eventos.Count);
        Assert.Equal(doNavegador, eventos[0].EventoId);
        Assert.NotEqual(doNavegador, eventos[1].EventoId);
    }

    [Fact]
    public async Task O_LEAD_QUE_CHEGA_PELO_WHATSAPP_TAMBEM_VIRA_CONVERSAO()
    {
        // ⚠️ O CAMINHO DE MAIOR VOLUME DESTE PÚBLICO, e o que quase ficou sem teste. Boa parte destes
        // clientes não tem site: o anúncio deles é "Clique para WhatsApp" e vai direto para a
        // conversa. Publicar só no formulário faria o bloco inteiro servir à minoria.
        //
        // Sem rastro nenhum — o casamento da Meta por telefone funciona sozinho.
        var (db, tx, amb) = await PrepararAsync("lead-whatsapp");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        const string Jid = "5584970005555@s.whatsapp.net";

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Cenario.Conexao.InstanceName, Jid, "WA-CONV-1",
                "vi o anúncio", pushName: "Maria do Anúncio"), default);

        db.ChangeTracker.Clear();

        var contato = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Telefone == "5584970005555");

        var evento = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.ContatoId == contato.Id);

        Assert.Equal(TipoConversao.Lead, evento.Tipo);

        var corpo = JsonNode.Parse(evento.Payload)!["data"]![0]!;
        Assert.Equal("chat", (string)corpo["action_source"]!);
        Assert.Equal(HashPessoal.Telefone("5584970005555"), (string)corpo["user_data"]!["ph"]![0]!);

        // Sem IP, sem User-Agent, sem `fbc`: não houve navegador nenhum. E o evento vale assim.
        Assert.Null(corpo["user_data"]!["client_ip_address"]);
        Assert.Null(corpo["user_data"]!["fbc"]);
    }

    [Fact]
    public async Task MENSAGEM_DE_QUEM_JA_E_CONTATO_NAO_GERA_LEAD_DE_NOVO()
    {
        // O contato do cenário já existe. Cada mensagem dele não é um lead novo — publicar aqui
        // mandaria um `Lead` por mensagem recebida, e a Meta aprenderia que o cliente mais fiel é o
        // que mais "converte".
        var (db, tx, amb) = await PrepararAsync("wa-conhecido");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        var jid = amb.Cenario.Contato.Telefone + "@s.whatsapp.net";

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Cenario.Conexao.InstanceName, jid, "WA-CONV-2", "oi"),
            default);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    }

    // ============================================ o anúncio Clique-para-WhatsApp (INT-4, commit 8)
    /// <summary>Um `messages.upsert` com a referência do anúncio grudada, na posição que o payload
    /// real deste banco usa (`data.contextInfo`).</summary>
    private static string PayloadComAnuncio(string instancia, string jid, string waId) => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{instancia}}",
          "data": {
            "key": { "id": "{{waId}}", "remoteJid": "{{jid}}", "fromMe": false },
            "pushName": "Maria do Anúncio",
            "messageType": "conversation",
            "message": { "conversation": "vi o anúncio" },
            "contextInfo": {
              "entryPointConversionSource": "ctwa_ad",
              "externalAdReply": {
                "title": "Promoção de março",
                "sourceType": "ad",
                "sourceId": "120210000000000123",
                "sourceUrl": "https://fb.me/abc?x=1",
                "ctwaClid": "ARAaBBccDD-clique"
              }
            },
            "messageTimestamp": 1786230002
          }
        }
        """;

    [Fact]
    public async Task O_LEAD_DO_ANUNCIO_NO_WHATSAPP_VIRA_RASTRO_E_CONVERSAO_business_messaging()
    {
        // ⚠️ O CAMINHO DO PÚBLICO QUE NÃO TEM SITE, e o que o bloco INT-4 existe para alcançar: o
        // anúncio é "Clique para WhatsApp" e vai direto para a conversa. Sem isto, o bloco serviria só
        // a quem tem site — a minoria.
        var (db, tx, amb) = await PrepararAsync("ctwa");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        const string Jid = "5584970007777@s.whatsapp.net";

        await amb.Processador.ProcessarAsync(
            PayloadComAnuncio(amb.Cenario.Conexao.InstanceName, Jid, "WA-CTWA-1"), default);
        db.ChangeTracker.Clear();

        var contato = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Telefone == "5584970007777");

        // ---- o rastro
        var rastro = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.ContatoId == contato.Id);

        Assert.Equal(FonteRastreio.AnuncioWhatsapp, rastro.Fonte);
        Assert.Equal("meta", rastro.UtmSource);
        Assert.Equal("ctwa", rastro.UtmMedium);
        // O TÍTULO como campanha: é o que uma pessoa reconhece na tela do contato. O id do anúncio
        // fica em `utm_content`.
        Assert.Equal("Promoção de março", rastro.UtmCampaign);
        Assert.Equal("120210000000000123", rastro.UtmContent);
        Assert.Equal("https://fb.me/abc", rastro.Pagina);   // sem a query string
        Assert.Equal("ARAaBBccDD-clique",
            RegrasRastreio.Ler(rastro.Identificadores)[RegrasRastreio.ChaveCtwaClid]);

        // ⚠️ SEM IP E SEM USER-AGENT: não houve navegador nenhum neste caminho.
        Assert.Null(rastro.Ip);
        Assert.Null(rastro.UserAgent);

        // ---- a conversão
        var evento = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.ContatoId == contato.Id);

        var corpo = JsonNode.Parse(evento.Payload)!["data"]![0]!;

        // ⚠️ `business_messaging`, e é o `ctwa_clid` que autoriza esse valor: é ele que a Meta usa para
        // casar a conversa com o clique no anúncio. Sem o identificador o evento sairia como `chat`.
        Assert.Equal("business_messaging", (string)corpo["action_source"]!);
        Assert.Equal("ARAaBBccDD-clique", (string)corpo["user_data"]!["ctwa_clid"]!);
        Assert.Equal("whatsapp", (string)corpo["user_data"]!["messaging_channel"]!);

        // E o telefone, hasheado, continua lá — é o segundo elo do casamento.
        Assert.Equal(HashPessoal.Telefone("5584970007777"),
            (string)corpo["user_data"]!["ph"]![0]!);
    }

    [Fact]
    public async Task UMA_CONVERSA_NORMAL_NAO_VIRA_RASTRO_DE_ANUNCIO()
    {
        // ⚠️ O PAYLOAD REAL DESTE BANCO tem `contextInfo` e `entryPointConversionSource` numa conversa
        // que começou por um link `wa.me` comum. Confundir os dois poria metade dos leads do QR Code
        // como "veio de anúncio pago" — e o relatório de origem pararia de significar alguma coisa.
        var (db, tx, amb) = await PrepararAsync("ctwa-nao");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Cenario.Conexao.InstanceName,
                "5584970008888@s.whatsapp.net", "WA-CTWA-2", "oi", pushName: "Comum"), default);
        db.ChangeTracker.Clear();

        var contato = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Telefone == "5584970008888");

        // Nenhum rastro — o comportamento é idêntico ao de antes deste commit.
        Assert.Empty(await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.ContatoId == contato.Id).ToListAsync());

        // E a conversão sai como `chat`, que é o canal real.
        var evento = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.ContatoId == contato.Id);

        var corpo = JsonNode.Parse(evento.Payload)!["data"]![0]!;
        Assert.Equal("chat", (string)corpo["action_source"]!);
        Assert.Null(corpo["user_data"]!["ctwa_clid"]);
    }

    [Fact]
    public async Task A_SEGUNDA_MENSAGEM_DO_MESMO_ANUNCIO_NAO_SOBRESCREVE_O_RASTRO()
    {
        // Primeiro rastro ganha, como no site: o `ctwa_clid` do clique ORIGINAL é o elo de atribuição.
        // E a segunda mensagem não é lead novo, então nem chega a tentar.
        var (db, tx, amb) = await PrepararAsync("ctwa-repete");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        const string Jid = "5584970009999@s.whatsapp.net";
        var instancia = amb.Cenario.Conexao.InstanceName;

        await amb.Processador.ProcessarAsync(PayloadComAnuncio(instancia, Jid, "WA-CTWA-3"), default);
        db.ChangeTracker.Clear();

        // A mesma pessoa manda outra mensagem, agora vinda de OUTRO anúncio.
        await amb.Processador.ProcessarAsync(
            PayloadComAnuncio(instancia, Jid, "WA-CTWA-4")
                .Replace("ARAaBBccDD-clique", "clique-novo")
                .Replace("Promoção de março", "Promoção de abril"), default);
        db.ChangeTracker.Clear();

        var rastro = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.EmpresaId == amb.Cenario.Id);

        Assert.Equal("Promoção de março", rastro.UtmCampaign);
        Assert.Equal("ARAaBBccDD-clique",
            RegrasRastreio.Ler(rastro.Identificadores)[RegrasRastreio.ChaveCtwaClid]);
    }

    [Fact]
    public async Task QUEM_JA_ERA_CONTATO_E_CLICA_NUM_ANUNCIO_HOJE_GANHA_O_RASTRO()
    {
        // ⚠️ O CASO QUE UMA SABOTAGEM DESCOBRIU. Enquanto a gravação vivia dentro do `if
        // (contatoNovo)`, o cliente que chegou pelo WhatsApp ano passado e clica num anúncio HOJE não
        // deixava rastro nenhum — e é justamente dele que o dono quer saber que o anúncio funcionou.
        //
        // O contato do cenário já existe. Não há lead novo, então não há conversão de `Lead`; o
        // rastro, sim.
        var (db, tx, amb) = await PrepararAsync("ctwa-conhecido");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        var jid = amb.Cenario.Contato.Telefone + "@s.whatsapp.net";

        await amb.Processador.ProcessarAsync(
            PayloadComAnuncio(amb.Cenario.Conexao.InstanceName, jid, "WA-CTWA-5"), default);
        db.ChangeTracker.Clear();

        var rastro = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.ContatoId == amb.Cenario.Contato.Id);

        Assert.Equal(FonteRastreio.AnuncioWhatsapp, rastro.Fonte);
        Assert.Equal("ARAaBBccDD-clique",
            RegrasRastreio.Ler(rastro.Identificadores)[RegrasRastreio.ChaveCtwaClid]);

        // E nenhuma conversão de lead: cada mensagem de quem já é contato não é um lead novo.
        Assert.Empty(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task O_RASTRO_DO_SITE_GANHA_DO_ANUNCIO_QUE_CHEGA_DEPOIS()
    {
        // ⚠️ "PRIMEIRO RASTRO GANHA" ATRAVESSANDO OS DOIS CAMINHOS, e é aqui que o `ON CONFLICT DO
        // NOTHING` da gravação do anúncio fica load-bearing: a pessoa veio do site, e depois mandou
        // mensagem de um anúncio. O `fbc` do clique original é o elo de atribuição, e trocá-lo pelo
        // `ctwa_clid` de agora quebraria exatamente o que o bloco existe para fazer.
        var (db, tx, amb) = await PrepararAsync("ctwa-perde");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);

        db.RastreiosLead.Add(new RastreioLead
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Cenario.Contato.Id,
            Fonte = FonteRastreio.FormularioSite,
            UtmCampaign = "campanha-do-site",
            Identificadores = RegrasRastreio.Montar(
                (RegrasRastreio.ChaveFbc, "fb.1.1700000000.IwAR-original")),
            OcorridoEm = Marco.UtcDateTime.AddDays(-5)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var jid = amb.Cenario.Contato.Telefone + "@s.whatsapp.net";
        await amb.Processador.ProcessarAsync(
            PayloadComAnuncio(amb.Cenario.Conexao.InstanceName, jid, "WA-CTWA-6"), default);
        db.ChangeTracker.Clear();

        var rastro = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.ContatoId == amb.Cenario.Contato.Id);

        Assert.Equal(FonteRastreio.FormularioSite, rastro.Fonte);
        Assert.Equal("campanha-do-site", rastro.UtmCampaign);
        Assert.Equal("fb.1.1700000000.IwAR-original",
            RegrasRastreio.Ler(rastro.Identificadores)[RegrasRastreio.ChaveFbc]);

        // E sem erro no log DO PROCESSADOR — é ele que grava o rastro do anúncio. Sem esta
        // afirmação, tirar o `ON CONFLICT DO NOTHING` não derrubaria teste nenhum: o `catch`
        // engole a violação e o primeiro rastro sobrevive de qualquer jeito.
        Assert.Empty(amb.LogProcessador.Erros);
    }

    // ==================================================================== tenant zero
    [Fact]
    public async Task A_CAPTACAO_PUBLICA_ENFILEIRA_MESMO_SEM_TENANT_NO_CONTEXTO()
    {
        // ⚠️ A ARMADILHA Nº 1 DESTE CAMINHO. A captação roda sem sessão: `EmpresaId` é 0, e uma busca
        // de credencial com query filter voltaria VAZIA — o lead do site nunca viraria conversão,
        // em silêncio, enquanto o criado à mão na tela viraria.
        var (db, tx, amb) = await PrepararAsync("tenant-zero");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        var formulario = new FormularioCaptura
        {
            EmpresaId = amb.Cenario.Id,
            Nome = "Landing",
            Chave = ServicoCaptura.GerarChave(),
            Ativo = true
        };
        db.FormulariosCaptura.Add(formulario);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // O contexto vai a ZERO, como numa requisição pública de verdade.
        amb.Contexto.EmpresaId = 0;
        amb.Contexto.UsuarioId = 0;

        await amb.Captura.ReceberAsync(formulario.Chave,
            new LeadDoFormulario("Bruna Lima", "84988889999", "bruna@exemplo.com", null, null,
                new RastreioDoSite(UtmCampaign: "promo", Fbclid: "IwAR-do-clique")),
            new DadosDaConexao(null, "203.0.113.9", "Mozilla/5.0"), default);

        db.ChangeTracker.Clear();

        var evento = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(amb.Cenario.Id, evento.EmpresaId);
        Assert.Equal(TipoConversao.Lead, evento.Tipo);

        var corpo = JsonNode.Parse(evento.Payload)!["data"]![0]!;
        Assert.Equal("website", (string)corpo["action_source"]!);
        Assert.Equal("203.0.113.9", (string)corpo["user_data"]!["client_ip_address"]!);
        Assert.Equal(HashPessoal.Email("bruna@exemplo.com"), (string)corpo["user_data"]!["em"]![0]!);
    }

    // ==================================================================== isolamento
    [Fact]
    public async Task A_CREDENCIAL_DE_UMA_EMPRESA_NAO_PUBLICA_EVENTO_DE_OUTRA()
    {
        // O pior defeito imaginável deste bloco: o telefone do cliente de uma empresa saindo pelo
        // pixel de outra.
        var (db, tx, amb) = await PrepararAsync("iso-pub");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        // A empresa B NÃO tem credencial. O contato dela não pode gerar evento.
        var outra = await Semeador.TenantAsync(db, "pubconv-iso-b");
        db.ChangeTracker.Clear();

        await amb.Publicador.PublicarLeadAsync(
            await db.Contatos.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(c => c.Id == outra.Contato.Id), default);
        db.ChangeTracker.Clear();

        Assert.Empty(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto, RelogioFalso Relogio,
        IPublicadorConversoes Publicador, IServicoConversoes Conversoes,
        IServicoContatos Contatos, IServicoCaptura Captura,
        ProcessadorEventoEvolution Processador,
        LoggerQueGuarda<PublicadorConversoes> Log,
        LoggerQueGuarda<ProcessadorEventoEvolution> LogProcessador);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(Marco);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"pubconv-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        // ⚠️ LOG GUARDADO, e não `NullLogger` — ver `LoggerQueGuarda`. O publicador engole o próprio
        // erro, então sem olhar o log um INSERT que estoura fica indistinguível de um que não
        // inseriu porque não devia: tirar o `ON CONFLICT DO NOTHING` não derrubaria teste nenhum.
        var log = new LoggerQueGuarda<PublicadorConversoes>();
        var publicador = new PublicadorConversoes(db, relogio, log);

        // O log do PROCESSADOR também é guardado: é ele que grava o rastro do anúncio, e é no log
        // dele que a colisão do `ON CONFLICT` apareceria se a cláusula saísse.
        var logProcessador = new LoggerQueGuarda<ProcessadorEventoEvolution>();

        return (db, tx, new Ambiente(
            cenario, ctx, relogio, publicador,
            new ServicoConversoes(db, ctx, new ClienteMetaFalso(), relogio),
            new ServicoContatos(db, ctx, PublicadorDeTeste.Novo(db, relogio), publicador,
                new ColetorAuditoria(), relogio),
            new ServicoCaptura(db, new NotificadorFalso(), PublicadorDeTeste.Novo(db, relogio),
                publicador, relogio, NullLogger<ServicoCaptura>.Instance),
            new ProcessadorEventoEvolution(
                db, new ClienteWhatsAppFalso(), new ArmazenamentoFalso(), new NotificadorFalso(),
                PublicadorDeTeste.Novo(db, relogio), publicador, relogio, logProcessador),
            log, logProcessador));
    }
}
