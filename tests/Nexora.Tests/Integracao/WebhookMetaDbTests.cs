using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Whatsapp;
using Nexora.Infra.CloudApi;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Whatsapp;

namespace Nexora.Tests.Integracao;

/// <summary>O WEBHOOK DA API OFICIAL (INT-XX), contra Postgres REAL: a porta confere e grava, a
/// rodada da fila processa, e a mensagem chega na mesma `RecepcaoMensagem` da Evolution.
///
/// Tudo roda SEM TENANT no contexto (EmpresaId = 0), como em producao. Se faltar um
/// IgnoreQueryFilters, a consulta volta vazia em silencio — e e aqui que aparece.
///
/// O que estes testes seguram, acima de tudo: o numero de uma empresa nao entra com a assinatura
/// de outra; entrega repetida nao vira mensagem repetida; e status nunca anda para tras.</summary>
[Collection("banco")]
public class WebhookMetaDbTests(BancoTeste banco)
{
    private const string PnidA = "1090000000501";
    private const string WabaA = "2090000000501";
    private const string SegredoA = "segredo-do-app-A";
    private const string TokenA = "EAAG-token-A";
    private const string VerifyA = "0123456789abcdef0123456789abcdef";

    private const string PnidB = "1090000000502";
    private const string WabaB = "2090000000502";
    private const string SegredoB = "segredo-do-app-B";

    private const string De = "5584988887777";

    // ==================================================================== handshake
    [Fact]
    public async Task O_HANDSHAKE_DEVOLVE_O_DESAFIO_E_MARCA_A_CONEXAO()
    {
        var (db, tx, amb) = await PrepararAsync("handshake");
        using var _ = db; using var __ = tx;

        var resposta = await amb.Recepcao.VerificarAsync("subscribe", VerifyA, "1158201444", default);

        Assert.Equal("1158201444", resposta);
        db.ChangeTracker.Clear();
        var conexao = await db.Conexoes.IgnoreQueryFilters().SingleAsync(c => c.Id == amb.Oficial.Id);
        Assert.NotNull(conexao.WebhookVerificadoEm);
    }

    [Theory]
    [InlineData("subscribe", "token-que-ninguem-tem")]
    [InlineData("unsubscribe", VerifyA)]
    [InlineData("subscribe", "")]
    public async Task HANDSHAKE_COM_TOKEN_DESCONHECIDO_E_RECUSADO(string modo, string token)
    {
        var (db, tx, amb) = await PrepararAsync("handshake-nao");
        using var _ = db; using var __ = tx;

        Assert.Null(await amb.Recepcao.VerificarAsync(modo, token, "1158201444", default));

        db.ChangeTracker.Clear();
        var conexao = await db.Conexoes.IgnoreQueryFilters().SingleAsync(c => c.Id == amb.Oficial.Id);
        Assert.Null(conexao.WebhookVerificadoEm);
    }

    // ==================================================================== assinatura
    [Fact]
    public async Task ASSINATURA_QUE_NAO_BATE_E_RECUSADA_E_NADA_E_GRAVADO()
    {
        var (db, tx, amb) = await PrepararAsync("assinatura");
        using var _ = db; using var __ = tx;
        var corpo = TextoDe(PnidA, WabaA, "wamid.ASS1", "oi");

        Assert.Equal(ResultadoWebhookMeta.AssinaturaInvalida,
            await amb.Recepcao.AceitarAsync(corpo, AssinaturaMeta.Assinar(corpo, "segredo-errado"), default));
        Assert.Equal(ResultadoWebhookMeta.AssinaturaInvalida,
            await amb.Recepcao.AceitarAsync(corpo, null, default));

        // Assinado certo, mas o corpo mudou depois.
        var assinatura = AssinaturaMeta.Assinar(corpo, SegredoA);
        var mexido = TextoDe(PnidA, WabaA, "wamid.ASS1", "oi, mexido");
        Assert.Equal(ResultadoWebhookMeta.AssinaturaInvalida,
            await amb.Recepcao.AceitarAsync(mexido, assinatura, default));

        Assert.Empty(await FilaAsync(db));
    }

    /// <summary>===================== O CRITERIO DE ISOLAMENTO =====================
    ///
    /// A assinatura e conferida com o app secret DO NUMERO citado. Quem tem o segredo da empresa A
    /// nao injeta mensagem no numero da empresa B — nem sozinha, nem escondida junto com uma mudanca
    /// legitima de A.
    /// ======================================================================</summary>
    [Fact]
    public async Task O_NUMERO_DE_B_ASSINADO_COM_O_SEGREDO_DE_A_NAO_ENTRA()
    {
        var (db, tx, amb) = await PrepararAsync("isolamento");
        using var _ = db; using var __ = tx;
        var b = await OutraEmpresaAsync(db, "isolamento");
        var deBAntes = await db.Mensagens.IgnoreQueryFilters().CountAsync(m => m.EmpresaId == b);

        var soDeB = TextoDe(PnidB, WabaB, "wamid.FORJADA", "mensagem forjada");
        Assert.Equal(ResultadoWebhookMeta.AssinaturaInvalida,
            await amb.Recepcao.AceitarAsync(soDeB, AssinaturaMeta.Assinar(soDeB, SegredoA), default));
        Assert.Empty(await FilaAsync(db));

        var misturada = PayloadCloudApi.Entrega(
            PayloadCloudApi.Conta(WabaA, PayloadCloudApi.Recebidas(PnidA, De, "Maria",
                PayloadCloudApi.Texto(De, "wamid.LEGITIMA", "de verdade"))),
            PayloadCloudApi.Conta(WabaB, PayloadCloudApi.Recebidas(PnidB, De, "Maria",
                PayloadCloudApi.Texto(De, "wamid.CARONA", "de carona"))));
        Assert.Equal(ResultadoWebhookMeta.Aceito,
            await amb.Recepcao.AceitarAsync(misturada, AssinaturaMeta.Assinar(misturada, SegredoA), default));

        var fila = Assert.Single(await FilaAsync(db));
        Assert.Equal(amb.Oficial.Id, fila.ConexaoId);
        Assert.Equal(amb.Cenario.Id, fila.EmpresaId);

        await amb.Motor.DrenarAsync(default);

        db.ChangeTracker.Clear();
        Assert.NotNull(await MensagemAsync(db, "wamid.LEGITIMA"));
        Assert.Null(await MensagemAsync(db, "wamid.CARONA"));
        Assert.Equal(deBAntes, await db.Mensagens.IgnoreQueryFilters().CountAsync(m => m.EmpresaId == b));
    }

    /// <summary>Numero que nao e de ninguem aqui (removido do Nexora, por exemplo): nada e gravado e a
    /// resposta nao e erro — senao a Meta reenvia por dias.</summary>
    [Fact]
    public async Task NUMERO_DESCONHECIDO_NAO_GRAVA_E_NAO_E_ERRO()
    {
        var (db, tx, amb) = await PrepararAsync("desconhecido");
        using var _ = db; using var __ = tx;
        var corpo = TextoDe("1099999999999", "2099999999999", "wamid.NINGUEM", "oi");

        Assert.Equal(ResultadoWebhookMeta.NadaReconhecido,
            await amb.Recepcao.AceitarAsync(corpo, AssinaturaMeta.Assinar(corpo, SegredoA), default));
        Assert.Empty(await FilaAsync(db));
    }

    // ==================================================================== a mensagem recebida
    [Fact]
    public async Task A_MENSAGEM_ENTRA_PELA_FILA_COMO_A_DA_EVOLUTION()
    {
        var (db, tx, amb) = await PrepararAsync("ponta-a-ponta");
        using var _ = db; using var __ = tx;

        await AceitarAsync(amb, TextoDe(PnidA, WabaA, "wamid.OI1", "Oi, vi o anúncio", nome: "Maria Cliente"));

        var pendente = Assert.Single(await FilaAsync(db));
        Assert.Equal("messages", pendente.Campo);
        Assert.Null(pendente.ProcessadoEm);

        Assert.Equal(1, await amb.Motor.DrenarAsync(default));

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters()
            .SingleAsync(c => c.EmpresaId == amb.Cenario.Id && c.Telefone == De);
        Assert.Equal("Maria Cliente", contato.Nome);
        Assert.Equal(De, contato.WaId);

        var conversa = await db.Conversas.IgnoreQueryFilters().SingleAsync(c => c.ContatoId == contato.Id);
        Assert.Equal(amb.Oficial.Id, conversa.ConexaoId);
        Assert.NotNull(conversa.UltimaEntradaEm);
        Assert.Equal(1, conversa.NaoLidas);

        var mensagem = (await MensagemAsync(db, "wamid.OI1"))!;
        Assert.Equal(DirecaoMensagem.Entrada, mensagem.Direcao);
        Assert.Equal("Oi, vi o anúncio", mensagem.Texto);
        Assert.Equal(amb.Oficial.Id, mensagem.ConexaoId);
        Assert.Equal(amb.Oficial.InstanceName, mensagem.InstanceName);
        Assert.Single(amb.Painel.Mensagens);

        var processada = Assert.Single(await FilaAsync(db));
        Assert.NotNull(processada.ProcessadoEm);
        Assert.Null(processada.ProcessandoDesde);
        Assert.Null(processada.Erro);
    }

    /// <summary>⚠️ O NONO DIGITO: a Meta pode mandar o numero sem ele. A mensagem casa com o contato
    /// do cadastro (nao cria outro), e o `wa_id` fica guardado — e para ele que a resposta vai.</summary>
    [Fact]
    public async Task O_WA_ID_SEM_O_NONO_DIGITO_CASA_COM_O_CADASTRO_E_FICA_GUARDADO()
    {
        var (db, tx, amb) = await PrepararAsync("nono");
        using var _ = db; using var __ = tx;
        var contato = new Contato { EmpresaId = amb.Cenario.Id, Nome = "Maria do cadastro", Telefone = De };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await AceitarAsync(amb, TextoDe(PnidA, WabaA, "wamid.NONO", "oi", de: "558488887777"));
        await amb.Motor.DrenarAsync(default);

        db.ChangeTracker.Clear();
        var depois = await db.Contatos.IgnoreQueryFilters().SingleAsync(c => c.EmpresaId == amb.Cenario.Id);
        Assert.Equal(contato.Id, depois.Id);
        Assert.Equal("558488887777", depois.WaId);
        Assert.Equal("Maria do cadastro", depois.Nome);
    }

    /// <summary>A Meta reenvia o que nao foi confirmado a tempo. Duas entregas iguais: uma mensagem,
    /// uma nao lida, um aviso no painel.</summary>
    [Fact]
    public async Task A_MESMA_ENTREGA_DUAS_VEZES_E_UMA_MENSAGEM_SO()
    {
        var (db, tx, amb) = await PrepararAsync("repetida");
        using var _ = db; using var __ = tx;
        var corpo = TextoDe(PnidA, WabaA, "wamid.REPETIDA", "oi");

        await AceitarAsync(amb, corpo);
        await AceitarAsync(amb, corpo);
        Assert.Equal(2, await amb.Motor.DrenarAsync(default));

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Mensagens.IgnoreQueryFilters().CountAsync(m => m.WaMessageId == "wamid.REPETIDA"));
        Assert.Equal(1, (await db.Conversas.IgnoreQueryFilters().SingleAsync(c => c.EmpresaId == amb.Cenario.Id)).NaoLidas);
        Assert.Single(amb.Painel.Mensagens);
    }

    /// <summary>Tipo que o leitor nao conhece nao some: vira o rotulo, e o vendedor sabe que chegou algo.</summary>
    [Fact]
    public async Task TIPO_DESCONHECIDO_VIRA_ROTULO_E_NAO_SOME()
    {
        var (db, tx, amb) = await PrepararAsync("desconhecida");
        using var _ = db; using var __ = tx;

        await AceitarAsync(amb, Recebida(PnidA, WabaA,
            PayloadCloudApi.DoTipo(De, "wamid.PEDIDO", "order", """{"catalog_id":"1"}""")));
        await amb.Motor.DrenarAsync(default);

        Assert.Equal("[mensagem não suportada: order]", (await MensagemAsync(db, "wamid.PEDIDO"))!.Texto);
    }

    // ==================================================================== midia
    [Fact]
    public async Task MIDIA_E_BAIXADA_PELO_ID_COM_O_TOKEN_DA_CONEXAO_E_GUARDADA_NA_EMPRESA()
    {
        var (db, tx, amb) = await PrepararAsync("midia");
        using var _ = db; using var __ = tx;
        amb.Meta.MidiaParaDevolver = new MidiaRecebida(Convert.ToBase64String([1, 2, 3, 4, 5]), "image/jpeg", null);

        await AceitarAsync(amb, Recebida(PnidA, WabaA,
            PayloadCloudApi.Midia(De, "wamid.FOTO1", "image", "MEDIA-77", "image/jpeg", legenda: "olha a foto")));
        await amb.Motor.DrenarAsync(default);

        Assert.Equal(["MEDIA-77"], amb.Meta.MidiasBaixadas);
        // O token DECIFRADO, da conexao que recebeu.
        Assert.Equal([TokenA], amb.Meta.TokensUsados);

        var m = (await MensagemAsync(db, "wamid.FOTO1"))!;
        Assert.Equal(TipoMidia.Imagem, m.TipoMidia);
        Assert.Equal($"emp-{amb.Cenario.Id}/wamidFOTO1.jpg", m.MidiaChave);
        Assert.Equal(5, m.MidiaBytes);
        Assert.Equal("olha a foto", m.Texto);
        Assert.True(amb.Armazenamento.Objetos.ContainsKey(m.MidiaChave!));
    }

    /// <summary>O nome do arquivo vem na mensagem, e nao no download: precisa chegar ate a thread.</summary>
    [Fact]
    public async Task DOCUMENTO_CHEGA_COM_O_NOME_DO_ARQUIVO()
    {
        var (db, tx, amb) = await PrepararAsync("documento");
        using var _ = db; using var __ = tx;
        amb.Meta.MidiaParaDevolver = new MidiaRecebida(Convert.ToBase64String([1, 2, 3]), "application/pdf", null);

        await AceitarAsync(amb, Recebida(PnidA, WabaA,
            PayloadCloudApi.Midia(De, "wamid.DOC1", "document", "MEDIA-78", "application/pdf",
                                  arquivo: "proposta.pdf")));
        await amb.Motor.DrenarAsync(default);

        var m = (await MensagemAsync(db, "wamid.DOC1"))!;
        Assert.Equal("proposta.pdf", m.MidiaNome);
        Assert.Equal($"emp-{amb.Cenario.Id}/wamidDOC1.pdf", m.MidiaChave);
    }

    /// <summary>A Meta nao entregou o anexo: a mensagem entra assim mesmo, com o marcador na thread e
    /// a causa no `erro`.</summary>
    [Fact]
    public async Task MIDIA_QUE_A_META_NAO_ENTREGA_GRAVA_O_ERRO_E_A_MENSAGEM_ENTRA()
    {
        var (db, tx, amb) = await PrepararAsync("midia-falha");
        using var _ = db; using var __ = tx;

        await AceitarAsync(amb, Recebida(PnidA, WabaA,
            PayloadCloudApi.Midia(De, "wamid.FALHA1", "image", "MEDIA-79", "image/jpeg")));
        await amb.Motor.DrenarAsync(default);

        var m = (await MensagemAsync(db, "wamid.FALHA1"))!;
        Assert.Equal(TipoMidia.Nenhum, m.TipoMidia);
        Assert.False(string.IsNullOrWhiteSpace(m.Texto));
        Assert.Contains("Meta", m.Erro);
        // Nao e falha da RODADA: a linha da fila sai processada.
        Assert.Null(Assert.Single(await FilaAsync(db)).Erro);
    }

    // ==================================================================== anuncio
    /// <summary>O clique em anuncio da Cloud API chega no `referral` — o mesmo bloco que o leitor de
    /// anuncio ja conhece. Vira o rastro do lead, como na Evolution.</summary>
    [Fact]
    public async Task ANUNCIO_DA_API_OFICIAL_VIRA_RASTRO_DO_LEAD()
    {
        var (db, tx, amb) = await PrepararAsync("anuncio");
        using var _ = db; using var __ = tx;

        await AceitarAsync(amb, Recebida(PnidA, WabaA,
            PayloadCloudApi.Texto(De, "wamid.CTWA1", "Quero saber o preço", PayloadCloudApi.Anuncio("ARAaCloud-clique"))));
        await amb.Motor.DrenarAsync(default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters()
            .SingleAsync(c => c.EmpresaId == amb.Cenario.Id && c.Telefone == De);
        var rastro = await db.RastreiosLead.IgnoreQueryFilters().SingleAsync(r => r.ContatoId == contato.Id);

        Assert.Equal(FonteRastreio.AnuncioWhatsapp, rastro.Fonte);
        Assert.Equal("Promoção de outubro", rastro.UtmCampaign);
        Assert.Equal("120210000000000999", rastro.UtmContent);
        Assert.Equal("https://fb.me/abc", rastro.Pagina);
        Assert.Equal("ARAaCloud-clique", RegrasRastreio.Ler(rastro.Identificadores)[RegrasRastreio.ChaveCtwaClid]);
    }

    // ==================================================================== status
    /// <summary>O `ack` so avanca: `delivered` que chega depois de `read` nao desmarca o lido.</summary>
    [Fact]
    public async Task STATUS_SO_AVANCA()
    {
        var (db, tx, amb) = await PrepararAsync("status");
        using var _ = db; using var __ = tx;
        var id = await SaidaAsync(db, amb, "wamid.SAIDA1", ack: 2);

        await AceitarAsync(amb, StatusDe(PnidA, WabaA, PayloadCloudApi.UmStatus("wamid.SAIDA1", "read", De)));
        await AceitarAsync(amb, StatusDe(PnidA, WabaA, PayloadCloudApi.UmStatus("wamid.SAIDA1", "delivered", De)));
        await amb.Motor.DrenarAsync(default);

        Assert.Equal((short)4, (await MensagemAsync(db, "wamid.SAIDA1"))!.Ack);
        // Um aviso so: o `delivered` atrasado nao mexeu em nada, entao nao tem o que avisar.
        Assert.Equal([(id, (short?)4)], amb.Painel.Acks);
    }

    /// <summary>`failed` depois de `sent` e a Meta dizendo que nao entregou — o vendedor precisa ver,
    /// com o motivo. Depois de `delivered`, a mensagem chegou: o `failed` nao desfaz isso.</summary>
    [Fact]
    public async Task FAILED_MOSTRA_O_MOTIVO_SO_SE_A_MENSAGEM_NAO_CHEGOU()
    {
        var (db, tx, amb) = await PrepararAsync("failed");
        using var _ = db; using var __ = tx;
        await SaidaAsync(db, amb, "wamid.ENVIADA", ack: 2);
        await SaidaAsync(db, amb, "wamid.ENTREGUE", ack: 3);

        await AceitarAsync(amb, StatusDe(PnidA, WabaA,
            PayloadCloudApi.UmStatus("wamid.ENVIADA", "failed", De, 131047, "Re-engagement message"),
            PayloadCloudApi.UmStatus("wamid.ENTREGUE", "failed", De, 131047, "Re-engagement message")));
        await amb.Motor.DrenarAsync(default);

        var falhou = (await MensagemAsync(db, "wamid.ENVIADA"))!;
        Assert.Equal((short)0, falhou.Ack);
        Assert.Contains("A Meta não entregou", falhou.Erro);
        Assert.Contains("131047", falhou.Erro);

        var chegou = (await MensagemAsync(db, "wamid.ENTREGUE"))!;
        Assert.Equal((short)3, chegou.Ack);
        Assert.Null(chegou.Erro);
    }

    // ==================================================================== a fila
    /// <summary>A rodada que falha devolve a linha para a fila com o motivo; a seguinte a processa.</summary>
    [Fact]
    public async Task FALHA_VOLTA_PARA_A_FILA_E_A_RODADA_SEGUINTE_PROCESSA()
    {
        var falha = new FalhaNoComando("INSERT INTO mensagens") { Limite = 1 };
        var (db, tx, amb) = await PrepararAsync("nova-tentativa", falha);
        using var _ = db; using var __ = tx;

        await AceitarAsync(amb, TextoDe(PnidA, WabaA, "wamid.TENTA", "oi"));

        falha.Armada = true;
        await amb.Motor.DrenarAsync(default);

        var primeira = Assert.Single(await FilaAsync(db));
        Assert.Null(primeira.ProcessadoEm);
        Assert.Null(primeira.ProcessandoDesde);
        Assert.Equal((short)1, primeira.Tentativas);
        Assert.Contains("Falha simulada", primeira.Erro);
        Assert.Null(await MensagemAsync(db, "wamid.TENTA"));

        await amb.Motor.DrenarAsync(default);

        var segunda = Assert.Single(await FilaAsync(db));
        Assert.NotNull(segunda.ProcessadoEm);
        Assert.Equal((short)2, segunda.Tentativas);
        Assert.Null(segunda.Erro);
        Assert.NotNull(await MensagemAsync(db, "wamid.TENTA"));
    }

    /// <summary>Reserva de menos de 5 minutos e de uma rodada viva: ninguem mais pega. Reserva velha e
    /// de uma rodada que morreu: volta. E depois de 5 tentativas a linha para, para investigar.</summary>
    [Fact]
    public async Task SO_PEGA_O_QUE_NINGUEM_ESTA_PROCESSANDO_E_PARA_NA_QUINTA_TENTATIVA()
    {
        var (db, tx, amb) = await PrepararAsync("reserva");
        using var _ = db; using var __ = tx;
        var agora = DateTime.UtcNow;

        var viva = Linha(amb, processandoDesde: agora.AddMinutes(-1), tentativas: 1);
        var morta = Linha(amb, processandoDesde: agora.AddMinutes(-10), tentativas: 1);
        var esgotada = Linha(amb, processandoDesde: null, tentativas: MotorWebhooksMeta.MaximoTentativas);
        db.WebhooksMetaRecebidos.AddRange(viva, morta, esgotada);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(1, await amb.Motor.DrenarAsync(default));

        var fila = (await FilaAsync(db)).ToDictionary(x => x.Id);
        Assert.Null(fila[viva.Id].ProcessadoEm);
        Assert.NotNull(fila[morta.Id].ProcessadoEm);
        Assert.Null(fila[esgotada.Id].ProcessadoEm);
    }

    [Fact]
    public async Task PROCESSADAS_HA_MAIS_DE_7_DIAS_SAO_APAGADAS()
    {
        var (db, tx, amb) = await PrepararAsync("limpeza");
        using var _ = db; using var __ = tx;

        var velha = Linha(amb, processadoEm: DateTime.UtcNow.AddDays(-8));
        var recente = Linha(amb, processadoEm: DateTime.UtcNow.AddDays(-6));
        db.WebhooksMetaRecebidos.AddRange(velha, recente);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Motor.DrenarAsync(default);

        Assert.Equal([recente.Id], (await FilaAsync(db)).Select(x => x.Id));
    }

    /// <summary>O evento de template nao cita numero, so a conta: entra pela WABA, com a mesma
    /// assinatura. Quem trata e a etapa dos templates — aqui ele so nao pode se perder nem travar a fila.</summary>
    [Fact]
    public async Task EVENTO_DE_TEMPLATE_ENTRA_PELA_CONTA()
    {
        var (db, tx, amb) = await PrepararAsync("template");
        using var _ = db; using var __ = tx;

        await AceitarAsync(amb, PayloadCloudApi.Entrega(PayloadCloudApi.Conta(WabaA, """
            {"field":"message_template_status_update","value":{"event":"APPROVED",
             "message_template_id":123,"message_template_name":"boas_vindas",
             "message_template_language":"pt_BR","reason":"NONE"}}
            """)));
        await amb.Motor.DrenarAsync(default);

        var linha = Assert.Single(await FilaAsync(db));
        Assert.Equal("message_template_status_update", linha.Campo);
        Assert.Equal(amb.Oficial.Id, linha.ConexaoId);
        Assert.NotNull(linha.ProcessadoEm);
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, Conexao Oficial, RecepcaoWebhookMeta Recepcao, MotorWebhooksMeta Motor,
        ClienteCloudApiFalso Meta, ArmazenamentoFalso Armazenamento, NotificadorFalso Painel,
        CifraSegredos Cifra);

    /// <summary>Uma empresa com uma conexao da API oficial, sem contatos nem conversas: o webhook
    /// comeca do zero. Contexto em TENANT ZERO, como a porta e a rodada rodam.</summary>
    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo, FalhaNoComando? falha = null)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx, falha: falha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"meta-{sufixo}");
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == cenario.Id).ExecuteDeleteAsync();

        var cifra = CifraDeTeste.Nova();
        var oficial = new Conexao
        {
            EmpresaId = cenario.Id, Nome = "Oficial", InstanceName = $"cloud-meta-{sufixo}",
            Canal = CanalWhatsapp.CloudApi, PhoneNumberId = PnidA, WabaId = WabaA,
            AccessTokenCifrado = cifra.Cifrar(TokenA, FinalidadeSegredo.AccessToken),
            AppSecretCifrado = cifra.Cifrar(SegredoA, FinalidadeSegredo.AppSecret),
            VerifyToken = VerifyA, Status = StatusConexao.Conectado
        };
        db.Conexoes.Add(oficial);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var meta = new ClienteCloudApiFalso();
        var armazenamento = new ArmazenamentoFalso();
        var painel = new NotificadorFalso();

        var recepcao = new RecepcaoWebhookMeta(
            db, cifra, new SinalWebhooksMeta(), TimeProvider.System, NullLogger<RecepcaoWebhookMeta>.Instance);
        var processador = new ProcessadorWebhookCloudApi(
            db, armazenamento, painel, PublicadorDeTeste.Novo(db), PublicadorConversoesDeTeste.Novo(db),
            LeituraNpsDeTeste.Novo(db, TimeProvider.System), meta, cifra, TimeProvider.System,
            NullLogger<ProcessadorWebhookCloudApi>.Instance);
        var motor = new MotorWebhooksMeta(
            db, processador, TimeProvider.System, NullLogger<MotorWebhooksMeta>.Instance);

        return (db, tx, new Ambiente(cenario, oficial, recepcao, motor, meta, armazenamento, painel, cifra));
    }

    /// <summary>A empresa B, com o numero oficial dela e o app secret dela.</summary>
    private static async Task<long> OutraEmpresaAsync(NexoraDbContext db, string sufixo)
    {
        var b = await Semeador.TenantAsync(db, $"meta-{sufixo}-b");
        var cifra = CifraDeTeste.Nova();
        db.Conexoes.Add(new Conexao
        {
            EmpresaId = b.Id, Nome = "Oficial B", InstanceName = $"cloud-meta-{sufixo}-b",
            Canal = CanalWhatsapp.CloudApi, PhoneNumberId = PnidB, WabaId = WabaB,
            AccessTokenCifrado = cifra.Cifrar("EAAG-token-B", FinalidadeSegredo.AccessToken),
            AppSecretCifrado = cifra.Cifrar(SegredoB, FinalidadeSegredo.AppSecret),
            Status = StatusConexao.Conectado
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return b.Id;
    }

    /// <summary>Assina com o segredo de A, como a Meta faria, e confere que entrou.</summary>
    private static async Task AceitarAsync(Ambiente amb, byte[] corpo)
    {
        var resultado = await amb.Recepcao.AceitarAsync(corpo, AssinaturaMeta.Assinar(corpo, SegredoA), default);
        Assert.Equal(ResultadoWebhookMeta.Aceito, resultado);
    }

    private static byte[] TextoDe(
        string pnid, string waba, string id, string texto, string de = De, string nome = "Maria") =>
        PayloadCloudApi.Entrega(PayloadCloudApi.Conta(waba,
            PayloadCloudApi.Recebidas(pnid, de, nome, PayloadCloudApi.Texto(de, id, texto))));

    private static byte[] Recebida(string pnid, string waba, string mensagem) =>
        PayloadCloudApi.Entrega(PayloadCloudApi.Conta(waba, PayloadCloudApi.Recebidas(pnid, De, "Maria", mensagem)));

    private static byte[] StatusDe(string pnid, string waba, params string[] status) =>
        PayloadCloudApi.Entrega(PayloadCloudApi.Conta(waba, PayloadCloudApi.Status(pnid, status)));

    /// <summary>Uma mensagem NOSSA, ja enviada pela conexao oficial, com o `ack` dado.</summary>
    private static async Task<long> SaidaAsync(NexoraDbContext db, Ambiente amb, string waId, short ack)
    {
        var contato = await db.Contatos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.EmpresaId == amb.Cenario.Id && c.Telefone == De);
        if (contato == null)
        {
            contato = new Contato { EmpresaId = amb.Cenario.Id, Nome = "Maria", Telefone = De };
            db.Contatos.Add(contato);
            await db.SaveChangesAsync();
        }

        var conversa = await db.Conversas.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.ContatoId == contato.Id);
        if (conversa == null)
        {
            conversa = new Conversa
            {
                EmpresaId = amb.Cenario.Id, ContatoId = contato.Id, ConexaoId = amb.Oficial.Id,
                UltimaMensagemEm = DateTime.UtcNow
            };
            db.Conversas.Add(conversa);
            await db.SaveChangesAsync();
        }

        var mensagem = new Mensagem
        {
            EmpresaId = amb.Cenario.Id, ConversaId = conversa.Id, ContatoId = contato.Id,
            ConexaoId = amb.Oficial.Id, InstanceName = amb.Oficial.InstanceName,
            Direcao = DirecaoMensagem.Saida, WaMessageId = waId, Texto = "oi", Ack = ack,
            DataDisparo = DateOnly.FromDateTime(DateTime.UtcNow), EnviadaEm = DateTime.UtcNow
        };
        db.Mensagens.Add(mensagem);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return mensagem.Id;
    }

    private static WebhookMetaRecebido Linha(
        Ambiente amb, DateTime? processandoDesde = null, short tentativas = 0, DateTime? processadoEm = null) =>
        new()
        {
            EmpresaId = amb.Cenario.Id, ConexaoId = amb.Oficial.Id, Campo = "messages", Payload = "{}",
            RecebidoEm = DateTime.UtcNow.AddDays(-8), ProcessandoDesde = processandoDesde,
            Tentativas = tentativas, ProcessadoEm = processadoEm
        };

    private static async Task<List<WebhookMetaRecebido>> FilaAsync(NexoraDbContext db)
    {
        db.ChangeTracker.Clear();
        return await db.WebhooksMetaRecebidos.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    }

    private static async Task<Mensagem?> MensagemAsync(NexoraDbContext db, string waId)
    {
        db.ChangeTracker.Clear();
        return await db.Mensagens.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(m => m.WaMessageId == waId);
    }
}
