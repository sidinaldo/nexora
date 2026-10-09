using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Evolution;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;
using Nexora.Infra.Whatsapp;

namespace Nexora.Tests.Integracao;

/// <summary>O webhook contra Postgres REAL, com payloads no formato que a Evolution manda.
///
/// O processador roda SEM tenant no contexto (EmpresaId = 0), exatamente como em producao — e
/// e por isso que estes testes valem: se algum IgnoreQueryFilters faltar, a consulta volta
/// vazia em silencio e o sintoma aparece aqui, nao em producao.</summary>
[Collection("banco")]
public class WebhookEvolutionDbTests(BancoTeste banco)
{
    private const string Telefone = "5584988887777";
    private const string Jid = "5584988887777@s.whatsapp.net";

    // ==================================================================== o nome do contato
    /// <summary>===================== O NOME DO LEAD NAO PODE SER O DO CHIP =====================
    ///
    /// Encontrado em producao: tres leads diferentes, telefones diferentes, todos chamados
    /// "Sidinaldo Barbosa 💡" — o nome da conta de WhatsApp CONECTADA.
    ///
    /// A causa: quando o vendedor inicia a conversa pelo celular, o webhook chega com
    /// `fromMe=true` e `pushName` do REMETENTE — que naquela direcao e ele mesmo. O contato
    /// criado e o DESTINATARIO, e nascia com o nome de quem escreveu.
    ///
    /// O caller ja protegia o texto (`entrada ? texto : null`) e passava o `pushName` sem olhar a
    /// direcao. Metade da regra estava escrita.
    /// ==================================================================================</summary>
    [Fact]
    public async Task CONTATO_CRIADO_POR_MENSAGEM_DE_SAIDA_NAO_HERDA_O_NOME_DO_CHIP()
    {
        var (db, tx, amb) = await PrepararAsync("nome-saida");
        using var _ = db; using var __ = tx;

        // O vendedor escreve primeiro, do celular. `pushName` = o nome DELE.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-EU-1", "oi, tudo bem?",
                                      fromMe: true, pushName: "Sidinaldo Barbosa"), default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters().SingleAsync(c => c.Telefone == Telefone);

        Assert.NotEqual("Sidinaldo Barbosa", contato.Nome);
        // O telefone formatado e a resposta honesta: ainda nao sabemos o nome dele.
        Assert.Equal(CanonicalizadorTelefone.Formatar(Telefone), contato.Nome);
    }

    /// <summary>A primeira resposta DELE traz o nome de verdade — e ai vale adotar.</summary>
    [Fact]
    public async Task A_PRIMEIRA_RESPOSTA_DO_CLIENTE_DA_NOME_AO_CONTATO()
    {
        var (db, tx, amb) = await PrepararAsync("nome-adota");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-EU-2", "oi",
                                      fromMe: true, pushName: "Sidinaldo Barbosa"), default);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-ELE-1", "opa, tudo",
                                      pushName: "Sidcley28"), default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters().SingleAsync(c => c.Telefone == Telefone);

        Assert.Equal("Sidcley28", contato.Nome);
    }

    /// <summary>===================== O QUE NAO PODE ACONTECER =====================
    /// "João - obra do centro" e trabalho do vendedor. O WhatsApp da pessoa dizer "Joao" nao
    /// autoriza apagar isso — e um apelido interno que some sozinho e pior que nome nenhum,
    /// porque ninguem entende POR QUE sumiu.
    /// ======================================================================</summary>
    [Fact]
    public async Task NOME_QUE_ALGUEM_DIGITOU_NUNCA_E_SOBRESCRITO()
    {
        var (db, tx, amb) = await PrepararAsync("nome-batizado");
        using var _ = db; using var __ = tx;

        var contato = await CriarContatoAsync(db, amb.Cenario, "João - obra do centro", Telefone);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-ELE-2", "oi",
                                      pushName: "Joãozinho ⚽"), default);

        db.ChangeTracker.Clear();
        var depois = await db.Contatos.IgnoreQueryFilters().SingleAsync(c => c.Id == contato.Id);

        Assert.Equal("João - obra do centro", depois.Nome);
    }

    /// <summary>Contato que ja tem nome vindo do proprio WhatsApp tambem nao e reescrito a cada
    /// mensagem: a pessoa troca o nome dela por emoji da semana, e a agenda do vendedor nao pode
    /// virar isso.</summary>
    [Fact]
    public async Task NOME_JA_ADOTADO_NAO_MUDA_A_CADA_MENSAGEM()
    {
        var (db, tx, amb) = await PrepararAsync("nome-estavel");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-E1", "oi", pushName: "Sidcley28"), default);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-E2", "opa", pushName: "🔥 Sidcley 🔥"), default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters().SingleAsync(c => c.Telefone == Telefone);

        Assert.Equal("Sidcley28", contato.Nome);
    }

    // ==================================================================== REC-2 · nada invisível
    /// <summary>===================== O CASO QUE ORIGINOU O BLOCO =====================
    ///
    /// O contato (83) 95278-7173 mandou um `templateMessage` — imagem + texto + botão, o formato
    /// que conta de negócio dispara. A linha foi gravada VAZIA: sem texto, sem mídia, sem erro.
    /// Na thread virou um balão branco, e o vendedor não tinha como saber o que tinha chegado.
    ///
    /// `ConteudoMensagem` conhecia seis formatos; qualquer outro caía num buraco silencioso.
    /// ======================================================================</summary>
    [Fact]
    public async Task TEMPLATE_CHEGA_COM_O_TEXTO_E_O_BOTAO()
    {
        var (db, tx, amb) = await PrepararAsync("template");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Bruta(amb.Instancia, Jid, "WA-TPL", "templateMessage",
                                   PayloadEvolution.TemplateDoSorteio), default);

        var m = await MensagemAsync(db, "WA-TPL");
        Assert.NotNull(m);

        // O CONTEÚDO, que estava lá o tempo todo.
        Assert.Contains("sorteio de R$ 2 milhões", m!.Texto);

        // E o BOTÃO: é para onde a mensagem queria levar o cliente. Sem ele, o texto termina em
        // "clique para ver mais detalhes 👇🏻" e não há nada abaixo.
        Assert.Contains("Mais informações", m.Texto);
        Assert.Contains("w.meta.me/s/21NW8gsIBSIylxC", m.Texto);
    }

    /// <summary>O template tem MAIS DE UMA FORMA, e a segunda apareceu no mesmo dia.
    ///
    /// `hydratedTemplate` (a do sorteio) e `interactiveMessageTemplate` (a do Mercado Pago) são o
    /// mesmo `messageType` com estruturas diferentes. Cobrir só a primeira deixava a segunda no
    /// rótulo de "não suportada" — com o texto inteiro à vista dentro do payload.</summary>
    [Fact]
    public async Task TEMPLATE_INTERATIVO_TAMBEM_CHEGA_COM_O_TEXTO()
    {
        var (db, tx, amb) = await PrepararAsync("template-interativo");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Bruta(amb.Instancia, Jid, "WA-TPL2", "templateMessage",
                                   PayloadEvolution.TemplateInterativo), default);

        var m = await MensagemAsync(db, "WA-TPL2");
        Assert.NotNull(m);
        Assert.Contains("Pague o valor total", m!.Texto);
        Assert.DoesNotContain("não suportada", m.Texto);
    }

    /// <summary>===================== O TESTE QUE VALE MAIS QUE OS OUTROS =====================
    ///
    /// Não é sobre os tipos que existem hoje — é sobre o PRÓXIMO. O WhatsApp acrescenta formato
    /// sem avisar, e a regra que precisa valer é: nenhuma linha é gravada sem NADA.
    ///
    /// O último caso é um tipo INVENTADO. Se ele passar, qualquer novidade futura aparece na tela
    /// como "algo chegou" em vez de balão branco — que é a diferença entre o cliente reclamar e o
    /// vendedor abrir o celular para conferir.
    /// ==================================================================================</summary>
    [Theory]
    [InlineData("templateMessage", PayloadEvolution.TemplateDoSorteio)]
    [InlineData("templateMessage", PayloadEvolution.TemplateInterativo)]
    [InlineData("stickerMessage", """{ "stickerMessage": { "mimetype": "image/webp" } }""")]
    [InlineData("locationMessage", """{ "locationMessage": { "degreesLatitude": -7.11, "degreesLongitude": -34.86 } }""")]
    [InlineData("contactMessage", """{ "contactMessage": { "displayName": "João da Oficina" } }""")]
    [InlineData("pollCreationMessage", """{ "pollCreationMessage": { "name": "Qual horário fica melhor?" } }""")]
    [InlineData("buttonsMessage", """{ "buttonsMessage": { "contentText": "Escolha uma opção" } }""")]
    [InlineData("formatoQueAindaNaoExiste", """{ "formatoQueAindaNaoExiste": { "seiLa": 1 } }""")]
    public async Task NENHUMA_ENTRADA_E_GRAVADA_VAZIA(string tipo, string mensagemJson)
    {
        var (db, tx, amb) = await PrepararAsync($"vazia-{tipo.ToLowerInvariant()}");
        using var _ = db; using var __ = tx;

        var waId = $"WA-{tipo}";
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Bruta(amb.Instancia, Jid, waId, tipo, mensagemJson), default);

        var m = await MensagemAsync(db, waId);
        Assert.NotNull(m);

        var semTexto = string.IsNullOrWhiteSpace(m!.Texto);
        var semMidia = m.TipoMidia == TipoMidia.Nenhum;

        Assert.False(semTexto && semMidia,
            $"`{tipo}` foi gravado sem texto E sem mídia — vira balão branco na thread.");
    }

    /// <summary>===================== REAÇÃO NÃO ACENDE O SEMÁFORO =====================
    ///
    /// `AtualizarConversaAsync` acende `aguardando_desde` e soma `nao_lidas` em TODA entrada. Uma
    /// reação virando linha faria um 😘 aparecer como "cliente esperando resposta" — alarme falso
    /// numa tela cuja utilidade inteira depende de o alerta significar alguma coisa.
    ///
    /// Um emoji não pede ação. Por isso a reação sai ANTES de tocar o banco.
    ///
    /// ⚠️ Sem este teste, alguém "conserta" o balão vazio da reação transformando-a em mensagem —
    /// e reintroduz o alarme falso sem que nada mais quebre.
    /// ======================================================================</summary>
    [Fact]
    public async Task REACAO_NAO_ACENDE_O_SEMAFORO()
    {
        var (db, tx, amb) = await PrepararAsync("reacao");
        using var _ = db; using var __ = tx;

        // Uma conversa que JÁ FOI respondida: sem espera aberta, sem não-lidas.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-R0", "consegue hoje?"), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-R1", "consigo sim", fromMe: true), default);

        db.ChangeTracker.Clear();
        var antes = await db.Conversas.IgnoreQueryFilters().SingleAsync();
        Assert.Null(antes.AguardandoDesde);
        Assert.Equal(0, antes.NaoLidas);

        // O cliente reage com 😘.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Bruta(amb.Instancia, Jid, "WA-REACAO", "reactionMessage",
                                   PayloadEvolution.ReacaoBeijo), default);

        db.ChangeTracker.Clear();

        // NENHUMA linha nova — nem vazia, nem com rótulo.
        Assert.Null(await MensagemAsync(db, "WA-REACAO"));

        // E a conversa não mudou de estado: o semáforo continua apagado.
        var depois = await db.Conversas.IgnoreQueryFilters().SingleAsync();
        Assert.Null(depois.AguardandoDesde);
        Assert.Equal(0, depois.NaoLidas);
        Assert.Equal(DirecaoMensagem.Saida, depois.UltimaMensagemDirecao);
        Assert.Equal("consigo sim", depois.UltimaMensagemPrevia);
    }

    /// <summary>A figurinha é `image/webp`, e webp já é permitido desde o MID-1. Ela entra como
    /// IMAGEM — que é o que ela é — em vez de virar rótulo.</summary>
    [Fact]
    public async Task FIGURINHA_ENTRA_COMO_IMAGEM()
    {
        var (db, tx, amb) = await PrepararAsync("figurinha");
        using var _ = db; using var __ = tx;

        // RIFF **e** WEBP: `AssinaturaArquivo` exige os dois desde o MID-1, porque RIFF sozinho
        // também é WAV e AVI.
        byte[] webp = [.. "RIFF"u8, 0x20, 0x00, 0x00, 0x00, .. "WEBP"u8, .. new byte[64]];
        amb.Cliente.MidiaParaDevolver = new MidiaRecebida(
            Convert.ToBase64String(webp), "image/webp", "figurinha.webp");

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Bruta(amb.Instancia, Jid, "WA-STK", "stickerMessage",
                                   """{ "stickerMessage": { "mimetype": "image/webp" } }"""), default);

        var m = await MensagemAsync(db, "WA-STK");
        Assert.NotNull(m);
        Assert.Equal(TipoMidia.Imagem, m!.TipoMidia);
    }

    /// <summary>===================== MÍDIA QUE FALHA DEIXA RASTRO =====================
    ///
    /// Três das oito linhas vazias encontradas em produção eram imagem e áudio — tipos que
    /// DEVERIAM funcionar. O download falhou e a coluna `erro` ficou NULA, então não havia como
    /// distinguir "nunca chegou" de "chegou e se perdeu".
    /// ======================================================================</summary>
    [Fact]
    public async Task MIDIA_QUE_FALHA_GRAVA_O_ERRO()
    {
        var (db, tx, amb) = await PrepararAsync("midia-falha");
        using var _ = db; using var __ = tx;

        // A Evolution não devolve a mídia — é o modo de falha real dos três casos encontrados em
        // produção. O fake devolve `MidiaParaDevolver`, que já nasce nulo.
        amb.Cliente.MidiaParaDevolver = null;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Midia(amb.Instancia, Jid, "WA-FALHA", "image/jpeg"), default);

        var m = await MensagemAsync(db, "WA-FALHA");
        Assert.NotNull(m);

        // A linha EXISTE — não se perde mensagem porque o anexo falhou.
        Assert.Equal(TipoMidia.Nenhum, m!.TipoMidia);
        // E o rastro está lá, nas duas pontas: o marcador para quem lê a thread...
        Assert.False(string.IsNullOrWhiteSpace(m.Texto));
        // ...e a causa para quem investiga.
        Assert.False(string.IsNullOrWhiteSpace(m.Erro));
    }

    // ==================================================================== a edição no celular
    /// <summary>===================== O CASO QUE ORIGINOU ISTO =====================
    ///
    /// O contato (84) 9425-9023 corrigiu "Falr" para "Fale". O celular mostrava uma mensagem; o
    /// painel mostrava "Falr" e, embaixo, "[mensagem não suportada: secretEncryptedMessage]" — e
    /// contava a edição como nova entrada: mais uma não lida, prévia trocada pelo rótulo.
    ///
    /// Editar não é escrever de novo. A edição marca a original e não toca a conversa.
    /// ======================================================================</summary>
    [Fact]
    public async Task EDICAO_NAO_VIRA_LINHA_E_MARCA_A_ORIGINAL()
    {
        var (db, tx, amb) = await PrepararAsync("edicao");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-FALR", "Falr"), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Edicao(amb.Instancia, Jid, "WA-EDIT", alvo: "WA-FALR"), default);

        // Nenhuma linha para a edição — nem com rótulo.
        Assert.Null(await MensagemAsync(db, "WA-EDIT"));

        // Sem LID não abre: a original fica com o texto DELA e ganha a hora da edição.
        var original = await MensagemAsync(db, "WA-FALR");
        Assert.Equal("Falr", original!.Texto);
        Assert.Null(original.TextoOriginal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1780000060).UtcDateTime, original.EditadaEm);

        // A conversa é a de uma mensagem só.
        var conversa = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal(1, conversa.NaoLidas);
        Assert.Equal("Falr", conversa.UltimaMensagemPrevia);

        // O painel não recebe a edição como mensagem nova — recebe o aviso de que ela mudou, para a
        // marca aparecer na hora.
        Assert.Single(amb.Painel.Mensagens);
        Assert.Equal((original.Id, (short?)null), Assert.Single(amb.Painel.Acks));
    }

    /// <summary>Edição de uma mensagem que não temos — anterior ao Nexora, por exemplo. Não há
    /// o que marcar, e ela também não pode virar linha nem criar contato.</summary>
    [Fact]
    public async Task EDICAO_SEM_ORIGINAL_NAO_CRIA_NADA()
    {
        var (db, tx, amb) = await PrepararAsync("edicao-orfa");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Edicao(amb.Instancia, Jid, "WA-EDIT3", alvo: "WA-QUE-NAO-EXISTE"), default);

        db.ChangeTracker.Clear();
        Assert.False(await db.Mensagens.IgnoreQueryFilters().AnyAsync(m => m.EmpresaId == amb.Cenario.Id));
        Assert.False(await db.Contatos.IgnoreQueryFilters().AnyAsync(c => c.EmpresaId == amb.Cenario.Id));
    }

    /// <summary>===================== A CONFIRMAÇÃO NO FORMATO DE VERDADE =====================
    ///
    /// A Evolution 2.3.7 manda a confirmação PLANA (`data.keyId`), e o Nexora lia `data.key.id`:
    /// desde 05/08 nenhum tique avançava. É também ela que traz o LID — às vezes com o aparelho.
    /// ======================================================================================</summary>
    [Fact]
    public async Task CONFIRMACAO_PLANA_AVANCA_O_TIQUE_E_GUARDA_O_LID()
    {
        var (db, tx, amb) = await PrepararAsync("ack-plano");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-SAI", "olá", fromMe: true), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.AckPlano(amb.Instancia, "WA-SAI", "181286291378345:9@lid", "DELIVERY_ACK"), default);

        var m = await MensagemAsync(db, "WA-SAI");
        Assert.Equal((short)3, m!.Ack);

        var contato = await db.Contatos.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == m.ContatoId);
        Assert.Equal("181286291378345@lid", contato.Lid);
    }

    /// <summary>===================== A PALAVRA EDITADA, DE PONTA A PONTA =====================
    ///
    /// O pedido foi esse: "preciso que a palavra seja editada". Nós falamos com o contato, a
    /// confirmação traz o LID, ele manda "Falr" e corrige para "Fale" — e a thread mostra "Fale",
    /// com "Falr" guardado. Os bytes são os reais.
    /// ======================================================================================</summary>
    [Fact]
    public async Task EDICAO_COM_LID_TROCA_O_TEXTO()
    {
        var (db, tx, amb) = await PrepararAsync("edicao-texto");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-OLA2", "olá", fromMe: true, timestamp: 1779999000), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.AckPlano(amb.Instancia, "WA-OLA2", PayloadEvolution.LidQueAbre, "SERVER_ACK"), default);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.OriginalComSegredo(amb.Instancia, Jid, "Falr"), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.EdicaoReal(amb.Instancia, Jid, "WA-EDIT-REAL"), default);

        var original = await MensagemAsync(db, PayloadEvolution.IdQueFoiEditada);
        Assert.Equal("Fale", original!.Texto);
        Assert.Equal("Falr", original.TextoOriginal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1780000060).UtcDateTime, original.EditadaEm);

        // Era a última mensagem: a caixa de entrada mostra a palavra nova também.
        Assert.Equal("Fale", (await ConversaAsync(db, amb.Cenario.Id)).UltimaMensagemPrevia);

        // E a tela é avisada na hora — sem isto, o "Fale" só aparecia quando chegasse a mensagem
        // seguinte. `ack` nulo é o aviso de conteúdo mudado.
        Assert.Contains((original.Id, (short?)null), amb.Painel.Acks);

        // E a edição continua sem virar linha.
        Assert.Null(await MensagemAsync(db, "WA-EDIT-REAL"));
    }

    /// <summary>===================== O CONSERTO DAS EDIÇÕES QUE JÁ ENTRARAM =====================
    ///
    /// O MESMO texto da migração `MensagemEditada`, recortado à empresa do teste. O estado de
    /// partida é o que o código antigo deixava: a edição como linha própria com o rótulo, contada
    /// como a última mensagem da conversa.
    /// ==========================================================================================</summary>
    [Fact]
    public async Task O_CONSERTO_TIRA_O_BALAO_E_DEVOLVE_A_CONVERSA()
    {
        var (db, tx, amb) = await PrepararAsync("edicao-conserto");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-OLA", "Olá?!!@", timestamp: 1780000000), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-FALR4", "Falr", timestamp: 1780000030), default);
        var antes = await ConversaAsync(db, amb.Cenario.Id);

        await EdicaoAntigaAsync(db, amb, "WA-EDIT4", alvo: "WA-FALR4", ts: 1780000060);
        await ConsertarAsync(db, amb);

        Assert.Null(await MensagemAsync(db, "WA-EDIT4"));

        var original = await MensagemAsync(db, "WA-FALR4");
        Assert.Equal(Instante(1780000060), original!.EditadaEm);
        Assert.Equal("Falr", original.Texto);

        // A conversa volta a ser a de antes da edição.
        var c = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal(2, c.NaoLidas);
        Assert.Equal("Falr", c.UltimaMensagemPrevia);
        Assert.Equal(Instante(1780000030), c.UltimaMensagemEm);
        Assert.Equal(antes.AguardandoDesde, c.AguardandoDesde);
    }

    /// <summary>A conversa já RESPONDIDA que terminou em duas edições do cliente. O código antigo
    /// somou duas não lidas e acendeu o semáforo na primeira; as duas têm de voltar.</summary>
    [Fact]
    public async Task O_CONSERTO_DESFAZ_TODAS_AS_EDICOES_DO_FIM()
    {
        var (db, tx, amb) = await PrepararAsync("edicao-conserto-2x");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-PERG", "Quanto custa?", timestamp: 1780000000), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-RESP", "R$ 50", fromMe: true, timestamp: 1780000030), default);

        await EdicaoAntigaAsync(db, amb, "WA-E1", alvo: "WA-PERG", ts: 1780000060);
        await EdicaoAntigaAsync(db, amb, "WA-E2", alvo: "WA-PERG", ts: 1780000090);
        await ConsertarAsync(db, amb);

        Assert.Null(await MensagemAsync(db, "WA-E1"));
        Assert.Null(await MensagemAsync(db, "WA-E2"));
        Assert.Equal(Instante(1780000090), (await MensagemAsync(db, "WA-PERG"))!.EditadaEm);

        // Respondida: ninguém espera, nada por ler, e a última palavra é a nossa.
        var c = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal(0, c.NaoLidas);
        Assert.Null(c.AguardandoDesde);
        Assert.Equal(DirecaoMensagem.Saida, c.UltimaMensagemDirecao);
        Assert.Equal("R$ 50", c.UltimaMensagemPrevia);
        Assert.Equal(Instante(1780000030), c.UltimaMensagemEm);
    }

    /// <summary>Edição de uma mensagem que não temos — anterior ao Nexora. O código antigo criou
    /// a conversa por causa dela, e apagá-la podia deixar a conversa vazia. Fica como estava.</summary>
    [Fact]
    public async Task O_CONSERTO_NAO_MEXE_NA_EDICAO_SEM_ORIGINAL()
    {
        var (db, tx, amb) = await PrepararAsync("edicao-conserto-orfa");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-OI", "oi", timestamp: 1780000000), default);
        await EdicaoAntigaAsync(db, amb, "WA-E5", alvo: "WA-ANTES-DO-NEXORA", ts: 1780000060);
        var antes = await ConversaAsync(db, amb.Cenario.Id);

        await ConsertarAsync(db, amb);

        Assert.NotNull(await MensagemAsync(db, "WA-E5"));

        var c = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal(antes.NaoLidas, c.NaoLidas);
        Assert.Equal(antes.UltimaMensagemPrevia, c.UltimaMensagemPrevia);
        Assert.Equal(antes.UltimaMensagemEm, c.UltimaMensagemEm);
    }

    /// <summary>Mensagem AUTOMÁTICA depois da edição. O despacho dela não mexe na conversa, então
    /// ela nunca foi a prévia — e o conserto não pode promovê-la a isso.</summary>
    [Fact]
    public async Task O_CONSERTO_NAO_POE_MENSAGEM_AUTOMATICA_NA_PREVIA()
    {
        var (db, tx, amb) = await PrepararAsync("edicao-conserto-auto");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-FALR6", "Falr", timestamp: 1780000000), default);
        await EdicaoAntigaAsync(db, amb, "WA-E6", alvo: "WA-FALR6", ts: 1780000060);

        var conversa = await ConversaAsync(db, amb.Cenario.Id);
        var lembrete = Instante(1780000090);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO mensagens (empresa_id, conversa_id, contato_id, conexao_id, instance_name,
                                   direcao, texto, origem, tipo_automacao,
                                   data_disparo, enviada_em, reservado_em, criado_em)
            VALUES ({0}, {1}, {2}, {3}, {4}, 'saida', 'Lembrete: amanhã às 9h', 'automatica', 'lembrete',
                    {5}, {6}, {6}, {6})
            """,
            amb.Cenario.Id, conversa.Id, conversa.ContatoId, amb.Cenario.Conexao.Id, amb.Instancia,
            DateOnly.FromDateTime(lembrete), lembrete);

        await ConsertarAsync(db, amb);

        var c = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal("Falr", c.UltimaMensagemPrevia);
        Assert.Equal(DirecaoMensagem.Entrada, c.UltimaMensagemDirecao);
        Assert.Equal(Instante(1780000000), c.UltimaMensagemEm);
        Assert.Equal(1, c.NaoLidas);
    }

    private static DateTime Instante(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;

    /// <summary>O que o código ANTIGO fazia com uma edição do cliente: linha própria com o rótulo,
    /// e a conversa tratando-a como a entrada mais recente — o mesmo que `AtualizarConversaAsync`
    /// faz com qualquer outra.</summary>
    private static async Task EdicaoAntigaAsync(
        NexoraDbContext db, Ambiente amb, string waId, string alvo, long ts)
    {
        var conversa = await ConversaAsync(db, amb.Cenario.Id);
        var quando = Instante(ts);
        var rotulo = ConteudoLegivel.Desconhecido(EdicaoMensagem.Tipo);

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO mensagens (empresa_id, conversa_id, contato_id, conexao_id, instance_name,
                                   direcao, wa_message_id, texto,
                                   recebida_em, reservado_em, criado_em, payload_raw)
            VALUES ({0}, {1}, {2}, {3}, {4}, 'entrada', {5}, {6}, {7}, {7}, {7}, CAST({8} AS jsonb))
            """,
            amb.Cenario.Id, conversa.Id, conversa.ContatoId, amb.Cenario.Conexao.Id, amb.Instancia,
            waId, rotulo, quando, PayloadEvolution.Edicao(amb.Instancia, Jid, waId, alvo, timestamp: ts));

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == conversa.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.NaoLidas, c => c.NaoLidas + 1)
                .SetProperty(c => c.AguardandoDesde, c => c.AguardandoDesde ?? quando)
                .SetProperty(c => c.UltimaMensagemEm, quando)
                .SetProperty(c => c.UltimaMensagemDirecao, DirecaoMensagem.Entrada)
                .SetProperty(c => c.UltimaMensagemPrevia, rotulo));
    }

    /// <summary>Roda o conserto pela conexão, como o teste da `EtiquetaDaReativacao`: sem
    /// parâmetros, nada no texto pode ser lido como marcador.</summary>
    private static async Task ConsertarAsync(NexoraDbContext db, Ambiente amb)
    {
        await using var comando = db.Database.GetDbConnection().CreateCommand();
        comando.CommandText =
            Nexora.Infra.Persistencia.Migrations.MensagemEditada.SqlConsertar(amb.Cenario.Id);
        comando.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        await comando.ExecuteNonQueryAsync();
        db.ChangeTracker.Clear();
    }

    // ==================================================================== a janela do WhatsApp (INT-XX)
    /// <summary>===================== A ULTIMA ENTRADA, DE ONDE SAI A JANELA DE 24H =====================
    ///
    /// Tres regras, as tres com sintoma silencioso se quebrarem:
    ///   • responder NAO mexe — a janela da Meta conta da ultima mensagem DO CLIENTE;
    ///   • mensagem atrasada nao puxa para tras — fecharia a janela antes da hora;
    ///   • cada numero tem a sua — a janela da Meta e por numero, e o cliente escrever para outro
    ///     numero da empresa nao abre a deste (CONV-XX: vai para a conversa daquele numero).
    /// ======================================================================================</summary>
    [Fact]
    public async Task A_ULTIMA_ENTRADA_SO_AVANCA_E_CADA_NUMERO_TEM_A_SUA()
    {
        var (db, tx, amb) = await PrepararAsync("janela-entrada");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-J1", "oi", timestamp: 1780000100), default);
        Assert.Equal(Instante(1780000100), (await ConversaAsync(db, amb.Cenario.Id)).UltimaEntradaEm);

        // A nossa resposta e uma mensagem atrasada do cliente: nenhuma das duas mexe.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-J2", "olá", fromMe: true, timestamp: 1780000200), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-J0", "antiga", timestamp: 1780000000), default);
        Assert.Equal(Instante(1780000100), (await ConversaAsync(db, amb.Cenario.Id)).UltimaEntradaEm);

        // O cliente escreve para OUTRO numero da empresa: a conversa do outro numero e outra (CONV-XX),
        // e a janela desta nao mexe.
        db.Conexoes.Add(new Conexao
        {
            EmpresaId = amb.Cenario.Id, Nome = "Segundo", InstanceName = amb.Instancia + "-2"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia + "-2", Jid, "WA-J3", "pelo outro", timestamp: 1780000300), default);
        Assert.Equal(Instante(1780000100), (await ConversaAsync(db, amb.Cenario.Id, amb.Instancia)).UltimaEntradaEm);
        Assert.Equal(Instante(1780000300), (await ConversaAsync(db, amb.Cenario.Id, amb.Instancia + "-2")).UltimaEntradaEm);

        // E a seguinte pelo numero da conversa avanca.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-J4", "de novo", timestamp: 1780000400), default);
        Assert.Equal(Instante(1780000400), (await ConversaAsync(db, amb.Cenario.Id, amb.Instancia)).UltimaEntradaEm);
    }

    /// <summary>CONV-XX: o mesmo cliente escrevendo para dois numeros da empresa tem DUAS conversas,
    /// e cada mensagem fica na do numero por onde chegou. Antes, a do segundo numero caia na
    /// conversa do primeiro, e a resposta saia pelo numero errado.</summary>
    [Fact]
    public async Task CONV_O_MESMO_CLIENTE_EM_DOIS_NUMEROS_TEM_DUAS_CONVERSAS()
    {
        var (db, tx, amb) = await PrepararAsync("conv-dois-numeros");
        using var _ = db; using var __ = tx;

        db.Conexoes.Add(new Conexao
        {
            EmpresaId = amb.Cenario.Id, Nome = "Segundo", InstanceName = amb.Instancia + "-2"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-C1", "oi A", timestamp: 1780000100), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia + "-2", Jid, "WA-C2", "oi B", timestamp: 1780000200), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-C3", "de novo A", timestamp: 1780000300), default);

        var a = await ConversaAsync(db, amb.Cenario.Id, amb.Instancia);
        var b = await ConversaAsync(db, amb.Cenario.Id, amb.Instancia + "-2");
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.ContatoId, b.ContatoId);

        var porConversa = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.EmpresaId == amb.Cenario.Id && m.WaMessageId!.StartsWith("WA-C"))
            .OrderBy(m => m.WaMessageId)
            .Select(m => m.ConversaId).ToListAsync();
        Assert.Equal([a.Id, b.Id, a.Id], porConversa);
    }

    /// <summary>O numero exato que a Meta usa para a pessoa (nono digito) fica no contato — e so a
    /// ENTRADA o grava. Mensagem sem ele (a Evolution nao manda) nao apaga o que ja havia.</summary>
    [Fact]
    public async Task O_WA_ID_DA_META_FICA_NO_CONTATO()
    {
        var (db, tx, amb) = await PrepararAsync("wa-id");
        using var _ = db; using var __ = tx;

        var recepcao = new RecepcaoMensagem(
            db, amb.Armazenamento, amb.Painel, PublicadorDeTeste.Novo(db),
            PublicadorConversoesDeTeste.Novo(db),
            LeituraNpsDeTeste.Novo(db, TimeProvider.System, amb.Cliente),
            TimeProvider.System, NullLogger.Instance);
        var conexao = await db.Conexoes.IgnoreQueryFilters().SingleAsync(c => c.Id == amb.Cenario.Conexao.Id);

        MensagemEntrante Mensagem(string id, string? waId, bool entrada) => new(
            WaMessageId: id, Telefone: Telefone, WaId: waId, Entrada: entrada,
            Quando: Instante(1780000000), Texto: "oi", NomePerfil: null, CitadaWaId: null,
            TipoParaRotulo: "text", PayloadRaw: "{}", BaixarMidia: null);

        await recepcao.ReceberAsync(conexao, Mensagem("WA-W1", "558488887777", entrada: true), default);
        await recepcao.ReceberAsync(conexao, Mensagem("WA-W2", null, entrada: true), default);
        await recepcao.ReceberAsync(conexao, Mensagem("WA-W3", "550000000000", entrada: false), default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters().SingleAsync(c => c.Telefone == Telefone);
        Assert.Equal("558488887777", contato.WaId);
    }

    /// <summary>A coluna nasce vazia nas conversas que ja existem: a migracao a preenche pelo
    /// historico, e os geradores de demonstracao fazem o mesmo. Os dois tem de chegar ao valor que
    /// a `RecepcaoMensagem` teria gravado — inclusive ignorando a entrada pelo outro numero.</summary>
    [Fact]
    public async Task O_HISTORICO_REFAZ_A_ULTIMA_ENTRADA_COM_A_MESMA_REGRA()
    {
        var (db, tx, amb) = await PrepararAsync("janela-historico");
        using var _ = db; using var __ = tx;

        db.Conexoes.Add(new Conexao
        {
            EmpresaId = amb.Cenario.Id, Nome = "Segundo", InstanceName = amb.Instancia + "-2"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-H1", "oi", timestamp: 1780000100), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-H2", "olá", fromMe: true, timestamp: 1780000200), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-H3", "e ai?", timestamp: 1780000300), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia + "-2", Jid, "WA-H4", "pelo outro", timestamp: 1780000400), default);

        // ⚠️ O HISTORICO ANTIGO E MISTURADO: antes do CONV-XX, a mensagem pelo outro numero caia na
        // conversa do primeiro. Isto o recria, para a regra provar que ainda ignora aquela entrada.
        var conversaId = (await ConversaAsync(db, amb.Cenario.Id, amb.Instancia)).Id;
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.WaMessageId == "WA-H4")
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ConversaId, conversaId));

        Task ApagarAsync() => db.Conversas.IgnoreQueryFilters().Where(c => c.Id == conversaId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UltimaEntradaEm, (DateTime?)null));

        // ---- a migracao: o MESMO texto, recortado a empresa do teste ----
        await ApagarAsync();
        await using (var comando = db.Database.GetDbConnection().CreateCommand())
        {
            comando.CommandText =
                Nexora.Infra.Persistencia.Migrations.CanalWhatsapp.SqlUltimaEntrada.TrimEnd().TrimEnd(';')
              + " AND c.empresa_id = " + amb.Cenario.Id + ";";
            comando.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            await comando.ExecuteNonQueryAsync();
        }
        Assert.Equal(Instante(1780000300), (await ConversaAsync(db, amb.Cenario.Id, amb.Instancia)).UltimaEntradaEm);

        // ---- os geradores de demonstracao ----
        await ApagarAsync();
        await UltimaEntradaDoHistorico.RecalcularAsync(db, amb.Cenario.Id, default);
        Assert.Equal(Instante(1780000300), (await ConversaAsync(db, amb.Cenario.Id, amb.Instancia)).UltimaEntradaEm);
    }

    // ==================================================================== casamento
    [Fact]
    public async Task Mensagem_de_contato_conhecido_casa_com_o_contato_certo()
    {
        var (db, tx, amb) = await PrepararAsync("conhecido");
        using var _ = db; using var __ = tx;

        // Dois contatos na mesma empresa: o teste falha se casar com o errado.
        var alvo = await CriarContatoAsync(db, amb.Cenario, "Cliente Certo", Telefone);
        await CriarContatoAsync(db, amb.Cenario, "Outro Cliente", "5584911112222");

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-1", "oi, quero um orçamento"), default);

        var mensagem = await MensagemAsync(db, "WA-1");
        Assert.NotNull(mensagem);
        Assert.Equal(alvo.Id, mensagem!.ContatoId);
        Assert.Equal(DirecaoMensagem.Entrada, mensagem.Direcao);
        Assert.Equal("oi, quero um orçamento", mensagem.Texto);
        Assert.Equal(amb.Cenario.Id, mensagem.EmpresaId);
    }

    [Fact]
    public async Task Contato_cadastrado_sem_o_nono_digito_casa_com_a_mensagem_que_vem_com_ele()
    {
        // A armadilha do nono digito na pratica: o cadastro foi feito sem o 9 e o WhatsApp
        // entrega com. Sem as VARIANTES, a mensagem criaria um contato duplicado.
        var (db, tx, amb) = await PrepararAsync("nono");
        using var _ = db; using var __ = tx;

        var alvo = await CriarContatoAsync(db, amb.Cenario, "Sem Nono", "558488887777");

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-N1", "oi"), default);

        var mensagem = await MensagemAsync(db, "WA-N1");
        Assert.Equal(alvo.Id, mensagem!.ContatoId);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Contatos.IgnoreQueryFilters()
            .CountAsync(c => c.EmpresaId == amb.Cenario.Id));
    }

    // ==================================================================== captura de lead
    [Fact]
    public async Task Numero_desconhecido_cria_contato_SEM_NEGOCIO_e_sem_responsavel()
    {
        var (db, tx, amb) = await PrepararAsync("lead");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-L1", "vi o anúncio",
                pushName: "Maria Silva"), default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters()
            .SingleAsync(c => c.EmpresaId == amb.Cenario.Id && c.Telefone == Telefone);

        Assert.Equal("Maria Silva", contato.Nome);            // pushName vira o nome
        Assert.Equal(OrigemLead.Whatsapp, contato.Origem);
        Assert.Null(contato.ResponsavelId);                    // cai em "Nao atribuidas"

        // ===================== ELE NAO ENTRA EM FUNIL NENHUM (E6) =====================
        // O teste exigia "etapa de menor ordem = Novo Lead". Quem manda "vi o anúncio" ainda nao
        // e um negocio — pode ser cliente antigo pedindo suporte, fornecedor, engano. Ele chega na
        // CAIXA, e vira card quando alguem decide que ha negocio ali.
        //
        // O que continua garantido esta logo abaixo: a CONVERSA nasce junto. Sem ela o lead nao
        // apareceria em lugar nenhum, e ai sim seria um lead perdido.
        // =========================================================================
        Assert.False(await db.Negociacoes.IgnoreQueryFilters()
            .AnyAsync(n => n.ContatoId == contato.Id));

        // E a conversa nasceu junto.
        Assert.True(await db.Conversas.IgnoreQueryFilters().AnyAsync(c => c.ContatoId == contato.Id));

        // O painel foi avisado das tres coisas.
        Assert.Single(amb.Painel.Contatos);
        Assert.Single(amb.Painel.Conversas);
        Assert.Single(amb.Painel.Mensagens);
    }

    /// <summary>O evento de mensagem leva o total de não lidas da EMPRESA, contado no servidor
    /// (AUD-XX) — o mesmo número do status do painel. A conversa de outra empresa não entra.</summary>
    [Fact]
    public async Task O_EVENTO_DE_MENSAGEM_LEVA_AS_NAO_LIDAS_DA_EMPRESA()
    {
        var (db, tx, amb) = await PrepararAsync("nao-lidas");
        using var _ = db; using var __ = tx;

        var outra = await Semeador.TenantAsync(db, "nao-lidas-vizinha");
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == outra.Conversa.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.NaoLidas, 9));
        // Uma conversa aberta que já esperava, com 2 não lidas. (O preparo apaga a do cenário.)
        var antigo = new Contato { EmpresaId = amb.Cenario.Id, Nome = "Já esperava", Telefone = "5584980005001" };
        db.Contatos.Add(antigo);
        await db.SaveChangesAsync();
        db.Conversas.Add(new Conversa
        {
            EmpresaId = amb.Cenario.Id, ContatoId = antigo.Id, ConexaoId = amb.Cenario.Conexao.Id,
            UltimaMensagemEm = DateTime.UtcNow, NaoLidas = 2
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-NL1", "oi"), default);

        db.ChangeTracker.Clear();
        var esperado = await db.Conversas.IgnoreQueryFilters()
            .Where(c => c.EmpresaId == amb.Cenario.Id && c.Status == StatusConversa.Aberta)
            .SumAsync(c => c.NaoLidas);

        var evento = Assert.Single(amb.Painel.Mensagens);
        Assert.Equal(3, esperado);              // as 2 que já havia e a que acabou de chegar
        Assert.Equal(esperado, evento.NaoLidas);
    }

    [Fact]
    public async Task Sem_pushName_o_nome_do_contato_vira_o_telefone_formatado()
    {
        // A coluna nome e NOT NULL; deixar em branco seria pior que mostrar o numero.
        var (db, tx, amb) = await PrepararAsync("sem-push");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-P1", "oi", pushName: null), default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters()
            .SingleAsync(c => c.EmpresaId == amb.Cenario.Id && c.Telefone == Telefone);
        Assert.Equal("(84) 98888-7777", contato.Nome);
    }

    // ==================================================================== dedupe
    [Fact]
    public async Task Mesmo_payload_duas_vezes_gera_uma_mensagem_so()
    {
        // A Evolution REENTREGA ate receber 2xx. Sem o dedupe, a mesma mensagem entraria duas
        // vezes na conversa — e o contador de nao lidas contaria duas.
        var (db, tx, amb) = await PrepararAsync("dedupe");
        using var _ = db; using var __ = tx;
        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);

        var payload = PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-D1", "mensagem unica");
        await amb.Processador.ProcessarAsync(payload, default);
        await amb.Processador.ProcessarAsync(payload, default);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Mensagens.IgnoreQueryFilters().CountAsync(m => m.WaMessageId == "WA-D1"));

        // E a reentrega NAO pode inflar o contador nem re-notificar o painel.
        var conversa = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal(1, conversa.NaoLidas);
        Assert.Single(amb.Painel.Mensagens);
    }

    [Fact]
    public async Task Eco_do_proprio_envio_nao_vira_mensagem_nova()
    {
        // A Evolution devolve por webhook (fromMe=true) a mensagem que NOS acabamos de mandar.
        // A linha ja existe no banco, entao o INSERT colide no uq_msg_wa_id e some.
        var (db, tx, amb) = await PrepararAsync("eco");
        using var _ = db; using var __ = tx;
        var contato = await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);
        var conversa = await CriarConversaAsync(db, amb.Cenario, contato);

        // Simula a linha que o envio (bloco 4) ja gravou.
        db.Mensagens.Add(new Mensagem
        {
            EmpresaId = amb.Cenario.Id, ConversaId = conversa.Id, ContatoId = contato.Id,
            ConexaoId = amb.Cenario.Conexao.Id, InstanceName = amb.Instancia,
            Direcao = DirecaoMensagem.Saida, WaMessageId = "WA-ECO", Texto = "resposta do vendedor",
            DataDisparo = DateOnly.FromDateTime(DateTime.UtcNow), EnviadaEm = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-ECO", "resposta do vendedor",
                fromMe: true), default);

        Assert.Equal(1, await db.Mensagens.IgnoreQueryFilters().CountAsync(m => m.WaMessageId == "WA-ECO"));
        Assert.Empty(amb.Painel.Mensagens);
    }

    // ==================================================================== ACK
    [Fact]
    public async Task Ack_fora_de_ordem_READ_seguido_de_DELIVERY_mantem_READ()
    {
        // Os webhooks de ACK chegam fora de ordem. O ack e fonte de verdade e SO AVANCA: um
        // DELIVERY_ACK atrasado nao pode apagar um READ que ja chegou.
        var (db, tx, amb) = await PrepararAsync("ack");
        using var _ = db; using var __ = tx;
        var contato = await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);
        var conversa = await CriarConversaAsync(db, amb.Cenario, contato);

        db.Mensagens.Add(new Mensagem
        {
            EmpresaId = amb.Cenario.Id, ConversaId = conversa.Id, ContatoId = contato.Id,
            ConexaoId = amb.Cenario.Conexao.Id, InstanceName = amb.Instancia,
            Direcao = DirecaoMensagem.Saida, WaMessageId = "WA-ACK", Texto = "oi",
            DataDisparo = DateOnly.FromDateTime(DateTime.UtcNow), EnviadaEm = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Processador.ProcessarAsync(PayloadEvolution.Ack(amb.Instancia, "WA-ACK", "READ"), default);
        Assert.Equal((short)4, (await MensagemAsync(db, "WA-ACK"))!.Ack);

        await amb.Processador.ProcessarAsync(PayloadEvolution.Ack(amb.Instancia, "WA-ACK", "DELIVERY_ACK"), default);
        Assert.Equal((short)4, (await MensagemAsync(db, "WA-ACK"))!.Ack);   // continua READ

        // Só o avanço notifica o painel: o ACK atrasado nao gera evento.
        Assert.Single(amb.Painel.Acks);
        Assert.Equal((short)4, amb.Painel.Acks[0].Ack);
    }

    [Fact]
    public async Task Ack_avanca_de_servidor_para_entregue_e_para_lido()
    {
        var (db, tx, amb) = await PrepararAsync("ack-sobe");
        using var _ = db; using var __ = tx;
        var contato = await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);
        var conversa = await CriarConversaAsync(db, amb.Cenario, contato);

        db.Mensagens.Add(new Mensagem
        {
            EmpresaId = amb.Cenario.Id, ConversaId = conversa.Id, ContatoId = contato.Id,
            ConexaoId = amb.Cenario.Conexao.Id, InstanceName = amb.Instancia,
            Direcao = DirecaoMensagem.Saida, WaMessageId = "WA-SOBE", Texto = "oi",
            DataDisparo = DateOnly.FromDateTime(DateTime.UtcNow), EnviadaEm = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        foreach (var (status, esperado) in new[] { ("SERVER_ACK", 2), ("DELIVERY_ACK", 3), ("READ", 4) })
        {
            await amb.Processador.ProcessarAsync(PayloadEvolution.Ack(amb.Instancia, "WA-SOBE", status), default);
            Assert.Equal((short)esperado, (await MensagemAsync(db, "WA-SOBE"))!.Ack);
        }
        Assert.Equal(3, amb.Painel.Acks.Count);
    }

    // ==================================================================== aguardando_desde
    [Fact]
    public async Task Entrada_grava_aguardando_desde_e_a_segunda_nao_sobrescreve()
    {
        // O CORACAO DO SEMAFORO. O que importa e HA QUANTO TEMPO o contato espera resposta —
        // nao qual foi a ultima mensagem que ele mandou. Sobrescrever faria o semaforo
        // "rejuvenescer" toda vez que o cliente cobrasse, que e o oposto do que ele deve mostrar.
        var (db, tx, amb) = await PrepararAsync("aguardando");
        using var _ = db; using var __ = tx;
        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);

        var primeira = new DateTimeOffset(2026, 5, 20, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var segunda = new DateTimeOffset(2026, 5, 20, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-A1", "bom dia", timestamp: primeira), default);

        var depoisDaPrimeira = await ConversaAsync(db, amb.Cenario.Id);
        var marcado = depoisDaPrimeira.AguardandoDesde;
        Assert.NotNull(marcado);
        Assert.Equal(1, depoisDaPrimeira.NaoLidas);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-A2", "alguém aí?", timestamp: segunda), default);

        var depoisDaSegunda = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal(marcado, depoisDaSegunda.AguardandoDesde);   // NAO mudou: espera desde as 9h
        Assert.Equal(2, depoisDaSegunda.NaoLidas);                // mas conta as duas
        Assert.Equal("alguém aí?", depoisDaSegunda.UltimaMensagemPrevia);
        Assert.Equal(DirecaoMensagem.Entrada, depoisDaSegunda.UltimaMensagemDirecao);
    }

    [Fact]
    public async Task Saida_zera_aguardando_desde_e_nao_lidas()
    {
        var (db, tx, amb) = await PrepararAsync("resposta");
        using var _ = db; using var __ = tx;
        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-R1", "tem disponível?"), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-R2", "e o preço?"), default);

        var esperando = await ConversaAsync(db, amb.Cenario.Id);
        Assert.NotNull(esperando.AguardandoDesde);
        Assert.Equal(2, esperando.NaoLidas);

        // O vendedor responde pelo CELULAR — chega como fromMe pelo webhook.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-R3", "tenho sim!", fromMe: true), default);

        var respondida = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Null(respondida.AguardandoDesde);
        Assert.Equal(0, respondida.NaoLidas);
        Assert.Equal(DirecaoMensagem.Saida, respondida.UltimaMensagemDirecao);
    }

    [Fact]
    public async Task Entrada_depois_de_resposta_reabre_a_espera()
    {
        var (db, tx, amb) = await PrepararAsync("ciclo");
        using var _ = db; using var __ = tx;
        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);

        await amb.Processador.ProcessarAsync(PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-C1", "oi"), default);
        await amb.Processador.ProcessarAsync(PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-C2", "opa", fromMe: true), default);
        Assert.Null((await ConversaAsync(db, amb.Cenario.Id)).AguardandoDesde);

        await amb.Processador.ProcessarAsync(PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-C3", "quanto custa?"), default);

        var conversa = await ConversaAsync(db, amb.Cenario.Id);
        Assert.NotNull(conversa.AguardandoDesde);
        Assert.Equal(1, conversa.NaoLidas);
    }

    // ==================================================================== robustez
    [Fact]
    public async Task Payload_de_grupo_e_ignorado()
    {
        var (db, tx, amb) = await PrepararAsync("grupo");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, "1203630@g.us", "WA-G1", "bom dia pessoal"), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, "status@broadcast", "WA-G2", "status"), default);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == amb.Cenario.Id).ToListAsync());
        Assert.Empty(await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == amb.Cenario.Id).ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nao e json")]
    [InlineData("{ \"event\": ")]                                  // json truncado
    [InlineData("{ \"event\": \"messages.upsert\" }")]             // sem instance
    [InlineData("{ \"event\": \"messages.upsert\", \"instance\": \"inexistente\" }")]
    [InlineData("{ \"event\": \"desconhecido\", \"instance\": \"X\" }")]
    [InlineData("{ \"event\": \"messages.upsert\", \"instance\": \"X\", \"data\": { \"key\": null } }")]
    public async Task Payload_malformado_nao_lanca(string payload)
    {
        // A Evolution REENTREGA ate receber 2xx. Uma excecao que subisse viraria loop eterno do
        // mesmo payload quebrado, e o webhook pararia de processar o resto.
        var (db, tx, amb) = await PrepararAsync("malformado");
        using var _ = db; using var __ = tx;

        var ajustado = payload.Replace("\"X\"", $"\"{amb.Instancia}\"");
        var excecao = await Record.ExceptionAsync(() => amb.Processador.ProcessarAsync(ajustado, default));

        Assert.Null(excecao);
    }

    [Fact]
    public async Task Instancia_desconhecida_e_ignorada_sem_lancar()
    {
        // Uma instancia que nao esta em conexoes nao tem tenant — nao ha onde gravar. Ignorar
        // com log e o certo; lancar entraria em loop de reentrega.
        var (db, tx, amb) = await PrepararAsync("instancia");
        using var _ = db; using var __ = tx;

        var excecao = await Record.ExceptionAsync(() => amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem("instancia-de-ninguem", Jid, "WA-X", "oi"), default));

        Assert.Null(excecao);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Mensagens.IgnoreQueryFilters().ToListAsync());
    }

    // ==================================================================== conexao
    [Fact]
    public async Task Connection_update_open_carimba_numero_e_perfil()
    {
        var (db, tx, amb) = await PrepararAsync("conectou");
        using var _ = db; using var __ = tx;

        amb.Cliente.DetalhesParaDevolver = new Nexora.Core.Whatsapp.DetalhesInstancia(
            "5584999990000@s.whatsapp.net", "Padaria do Bairro", "http://foto", "open");

        await amb.Processador.ProcessarAsync(PayloadEvolution.Conexao(amb.Instancia, "open"), default);

        db.ChangeTracker.Clear();
        var conexao = await db.Conexoes.IgnoreQueryFilters().SingleAsync(c => c.Id == amb.Cenario.Conexao.Id);
        Assert.Equal(StatusConexao.Conectado, conexao.Status);
        Assert.Equal("5584999990000", conexao.Numero);
        Assert.Equal("Padaria do Bairro", conexao.PerfilNome);
        Assert.NotNull(conexao.ConectadoEm);
        Assert.Single(amb.Painel.Conexoes);
    }

    [Fact]
    public async Task Troca_de_chip_guarda_o_numero_anterior_sem_bloquear()
    {
        // O webhook e assincrono: nao ha usuario no loop para confirmar a troca. Grava o novo,
        // guarda o antigo para a tela avisar depois.
        var (db, tx, amb) = await PrepararAsync("troca");
        using var _ = db; using var __ = tx;

        amb.Cliente.DetalhesParaDevolver = new Nexora.Core.Whatsapp.DetalhesInstancia(
            "5584911112222@s.whatsapp.net", null, null, "open");

        await amb.Processador.ProcessarAsync(PayloadEvolution.Conexao(amb.Instancia, "open"), default);

        db.ChangeTracker.Clear();
        var conexao = await db.Conexoes.IgnoreQueryFilters().SingleAsync(c => c.Id == amb.Cenario.Conexao.Id);
        Assert.Equal("5584911112222", conexao.Numero);
        Assert.Equal(amb.Cenario.Conexao.Numero, conexao.NumeroAnterior);
    }

    [Fact]
    public async Task Connection_update_close_marca_desconectado()
    {
        var (db, tx, amb) = await PrepararAsync("caiu");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(PayloadEvolution.Conexao(amb.Instancia, "close"), default);

        db.ChangeTracker.Clear();
        var conexao = await db.Conexoes.IgnoreQueryFilters().SingleAsync(c => c.Id == amb.Cenario.Conexao.Id);
        Assert.Equal(StatusConexao.Desconectado, conexao.Status);
        Assert.NotNull(conexao.DesconectadoEm);
        // Mantem o numero: a tela mostra "estava conectado como...".
        Assert.NotNull(conexao.Numero);
    }

    // ==================================================================== midia
    /// <summary>===================== A MIDIA E O CONTEUDO =====================
    ///
    /// Imagem sem legenda e o caso NORMAL — a foto se explica sozinha, e o cliente raramente
    /// escreve junto. O texto fica nulo, e isso esta certo.
    ///
    /// ⚠️ REGRESSAO REAL, encontrada em producao: o guarda "nenhuma linha vazia" do REC-2 olhava
    /// so o texto. Uma imagem baixada com sucesso, sem legenda, recebia
    /// "[mensagem nao suportada: imageMessage]" — a foto aparecia na tela COM um aviso dizendo
    /// que ela nao era suportada.
    ///
    /// O teste antigo desta mesma mídia nao caiu porque ele nunca olhou o `Texto`. Por isso a
    /// asserção entrou nele também.
    /// ======================================================================</summary>
    [Fact]
    public async Task IMAGEM_SEM_LEGENDA_NAO_GANHA_ROTULO_DE_NAO_SUPORTADA()
    {
        var (db, tx, amb) = await PrepararAsync("img-sem-legenda");
        using var _ = db; using var __ = tx;
        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);

        amb.Cliente.MidiaParaDevolver = new Nexora.Core.Whatsapp.MidiaRecebida(
            Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]), "image/jpeg", "foto.jpg");

        // `legenda: null` — o padrão do helper, e o caso mais comum na vida real.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Midia(amb.Instancia, Jid, "WA-SEM-LEG", "image/jpeg"), default);

        var m = await MensagemAsync(db, "WA-SEM-LEG");
        Assert.NotNull(m);
        Assert.Equal(TipoMidia.Imagem, m!.TipoMidia);

        // O texto fica NULO. A mídia é o conteúdo — não há o que rotular.
        Assert.True(string.IsNullOrEmpty(m.Texto),
            $"imagem sem legenda recebeu texto inventado: \"{m.Texto}\"");
    }

    /// <summary>O mesmo para áudio: nota de voz nunca tem legenda.</summary>
    [Fact]
    public async Task AUDIO_SEM_LEGENDA_NAO_GANHA_ROTULO_DE_NAO_SUPORTADA()
    {
        var (db, tx, amb) = await PrepararAsync("audio-sem-legenda");
        using var _ = db; using var __ = tx;
        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);

        amb.Cliente.MidiaParaDevolver = new Nexora.Core.Whatsapp.MidiaRecebida(
            Convert.ToBase64String([.. "OggS"u8, .. new byte[32]]), "audio/ogg", "voz.ogg");

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Midia(amb.Instancia, Jid, "WA-VOZ-SL", "audio/ogg",
                                   messageType: "audioMessage"), default);

        var m = await MensagemAsync(db, "WA-VOZ-SL");
        Assert.NotNull(m);
        Assert.Equal(TipoMidia.Audio, m!.TipoMidia);
        Assert.True(string.IsNullOrEmpty(m.Texto),
            $"áudio sem legenda recebeu texto inventado: \"{m.Texto}\"");
    }

    [Fact]
    public async Task Midia_permitida_e_baixada_e_gravada_com_chave_deterministica()
    {
        var (db, tx, amb) = await PrepararAsync("midia");
        using var _ = db; using var __ = tx;
        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);

        amb.Cliente.MidiaParaDevolver = new Nexora.Core.Whatsapp.MidiaRecebida(
            Convert.ToBase64String([1, 2, 3, 4, 5]), "image/jpeg", "foto.jpg");

        var payload = PayloadEvolution.Midia(amb.Instancia, Jid, "WA-M1", "image/jpeg", "olha o produto");
        await amb.Processador.ProcessarAsync(payload, default);

        var mensagem = await MensagemAsync(db, "WA-M1");
        Assert.Equal(TipoMidia.Imagem, mensagem!.TipoMidia);
        Assert.Equal("image/jpeg", mensagem.MidiaMime);
        Assert.Equal(5, mensagem.MidiaBytes);
        Assert.Equal($"emp-{amb.Cenario.Id}/WAM1.jpg", mensagem.MidiaChave);
        // A LEGENDA continua sendo o texto. Este assert faltava — e foi por isso que a regressão
        // do rótulo em imagem sem legenda passou pela suíte inteira.
        Assert.Equal("olha o produto", mensagem.Texto);

        // Reentrega: a chave e DETERMINISTICA, entao sobrescreve o mesmo objeto em vez de
        // deixar um orfao no armazenamento (sem linha em mensagens, nunca expurgado).
        await amb.Processador.ProcessarAsync(payload, default);
        Assert.Single(amb.Armazenamento.Objetos);
    }

    [Fact]
    public async Task Midia_de_tipo_nao_permitido_e_recusada_mas_a_mensagem_entra()
    {
        // Recusar o arquivo nao pode fazer a mensagem sumir da conversa — o vendedor precisa
        // ver que o cliente mandou algo.
        var (db, tx, amb) = await PrepararAsync("midia-ruim");
        using var _ = db; using var __ = tx;
        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);

        amb.Cliente.MidiaParaDevolver = new Nexora.Core.Whatsapp.MidiaRecebida(
            Convert.ToBase64String([1, 2, 3]), "application/x-msdownload", "virus.exe");

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Midia(amb.Instancia, Jid, "WA-M2", "application/x-msdownload"), default);

        var mensagem = await MensagemAsync(db, "WA-M2");
        Assert.NotNull(mensagem);
        Assert.Equal(TipoMidia.Nenhum, mensagem!.TipoMidia);
        Assert.Null(mensagem.MidiaChave);
        Assert.Contains("recusado", mensagem.Texto!);
        Assert.Empty(amb.Armazenamento.Objetos);
    }

    [Fact]
    public async Task Audio_de_voz_com_codec_no_mimetype_e_aceito()
    {
        // O WhatsApp manda "audio/ogg; codecs=opus". Comparar a string inteira com a whitelist
        // recusaria audio de voz — que num CRM de vendas e o conteudo mais comum de todos.
        var (db, tx, amb) = await PrepararAsync("audio");
        using var _ = db; using var __ = tx;
        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);

        amb.Cliente.MidiaParaDevolver = new Nexora.Core.Whatsapp.MidiaRecebida(
            Convert.ToBase64String([9, 9, 9]), "audio/ogg; codecs=opus", null);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Midia(amb.Instancia, Jid, "WA-M3", "audio/ogg", messageType: "audioMessage"), default);

        var mensagem = await MensagemAsync(db, "WA-M3");
        Assert.Equal(TipoMidia.Audio, mensagem!.TipoMidia);
        Assert.Equal("audio/ogg", mensagem.MidiaMime);
        Assert.EndsWith(".ogg", mensagem.MidiaChave);
    }

    // ==================================================================== isolamento
    [Fact]
    public async Task Mensagem_de_uma_instancia_nunca_grava_no_tenant_da_outra()
    {
        var (db, tx, amb) = await PrepararAsync("iso-a");
        using var _ = db; using var __ = tx;
        var outro = await Semeador.TenantAsync(db, "iso-b");

        // MESMO telefone cadastrado nas DUAS empresas — legítimo: o mesmo cliente pode comprar
        // de duas empresas diferentes.
        await CriarContatoAsync(db, amb.Cenario, "Cliente de A", Telefone);
        var deB = new Contato
        {
            EmpresaId = outro.Id, Nome = "Cliente de B", Telefone = Telefone
        };
        db.Contatos.Add(deB);
        db.Negociacoes.Add(Semeador.Negocio(deB, outro.PrimeiraEtapa));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-ISO", "oi"), default);

        var mensagem = await MensagemAsync(db, "WA-ISO");
        Assert.Equal(amb.Cenario.Id, mensagem!.EmpresaId);

        // Uma unica linha para este wa_message_id, e ela e do tenant A. (O tenant B tem as
        // proprias mensagens do Semeador — o que se prova aqui e que ESTA nao foi para la.)
        Assert.Equal(1, await db.Mensagens.IgnoreQueryFilters().CountAsync(m => m.WaMessageId == "WA-ISO"));
        Assert.False(await db.Mensagens.IgnoreQueryFilters()
            .AnyAsync(m => m.EmpresaId == outro.Id && m.WaMessageId == "WA-ISO"));

        // E o contato do tenant B nao foi tocado: a conversa dele nao ganhou a mensagem.
        var contatoDeB = await db.Contatos.IgnoreQueryFilters()
            .SingleAsync(c => c.EmpresaId == outro.Id && c.Telefone == Telefone);
        Assert.False(await db.Mensagens.IgnoreQueryFilters()
            .AnyAsync(m => m.ContatoId == contatoDeB.Id));
    }

    // ============================================================== REC-1 · janela de queda
    // ===================== O QUE ESTES TESTES PROTEGEM =====================
    // Nao existe caminho de importacao: a mensagem atrasada entra pelo MESMO webhook. O que muda
    // e que ela chega com timestamp velho — e o processador foi escrito assumindo "agora".
    //
    // O modo de falha e invisivel em teste comum: todo payload de teste usa timestamp fixo e
    // conversa recem-criada, entao "mais recente" e sempre verdade e os guardas nunca sao
    // exercitados. Sem estes testes, o dia da primeira queda de verdade e o dia da descoberta.
    // =======================================================================
    private static long Ts(DateTime q) => new DateTimeOffset(q, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public async Task Mensagem_atrasada_de_numero_DESCONHECIDO_cria_contato_igual_as_outras()
    {
        // O corte e por TEMPO, nao por "contato ja conhecido". O cliente novo que escreveu
        // enquanto o sistema estava fora e exatamente o lead que nao se pode perder — e ele
        // ainda nao esta cadastrado.
        var (db, tx, amb) = await PrepararAsync("rec-lead");
        using var _ = db; using var __ = tx;

        var duasHorasAtras = DateTime.UtcNow.AddHours(-2);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-L1", "vi o anúncio ontem",
                pushName: "Lead Atrasado", timestamp: Ts(duasHorasAtras)), default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters()
            .SingleAsync(c => c.EmpresaId == amb.Cenario.Id && c.Telefone == Telefone);
        Assert.Equal("Lead Atrasado", contato.Nome);
        Assert.Equal(OrigemLead.Whatsapp, contato.Origem);

        var msg = await MensagemAsync(db, "REC-L1");
        Assert.NotNull(msg!.RecuperadaEm);                       // carimbada como atrasada
        Assert.Equal(duasHorasAtras, msg.RecebidaEm!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Mensagem_em_tempo_real_NAO_recebe_carimbo_de_recuperada()
    {
        // O contrapeso do teste acima. Carimbo em mensagem normal faria o aviso da caixa
        // aparecer sem queda nenhuma — e aviso que aparece sempre ensina a ser ignorado.
        var (db, tx, amb) = await PrepararAsync("rec-agora");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-A1", "oi",
                timestamp: Ts(DateTime.UtcNow)), default);

        Assert.Null((await MensagemAsync(db, "REC-A1"))!.RecuperadaEm);
    }

    [Fact]
    public async Task Mensagem_mais_velha_que_o_TETO_de_7_dias_entra_mas_sem_carimbo()
    {
        // O teto governa o AVISO, nao a entrada. Recusar uma mensagem que o WhatsApp nos
        // entregou seria jogar fora dado do cliente; anuncia-la como "o periodo em que o
        // WhatsApp esteve fora" seria mentira, porque tres meses nao e uma queda.
        var (db, tx, amb) = await PrepararAsync("rec-teto");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-T1", "mensagem antiga",
                timestamp: Ts(DateTime.UtcNow.AddDays(-30))), default);

        var msg = await MensagemAsync(db, "REC-T1");
        Assert.NotNull(msg);            // ENTROU
        Assert.Null(msg!.RecuperadaEm); // mas nao entra no aviso
    }

    [Fact]
    public async Task Aguardando_desde_recebe_o_timestamp_DA_MENSAGEM_e_nao_o_de_agora()
    {
        // Uma mensagem de ontem que ficou sem resposta precisa acender VERMELHO. Com `now()`,
        // toda a fila da queda amanheceria verde e o vendedor atenderia na ordem errada.
        var (db, tx, amb) = await PrepararAsync("rec-desde");
        using var _ = db; using var __ = tx;

        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);
        var ontem = DateTime.UtcNow.AddHours(-20);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-D1", "alguém aí?",
                timestamp: Ts(ontem)), default);

        var conversa = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal(ontem, conversa.AguardandoDesde!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Fora_de_ordem_a_espera_fica_na_mensagem_MAIS_ANTIGA()
    {
        // `??=` guardaria a primeira PROCESSADA, que e acidente de entrega. O semaforo mede
        // desde quando o contato espera — entao o menor timestamp e que vale.
        var (db, tx, amb) = await PrepararAsync("rec-ordem-desde");
        using var _ = db; using var __ = tx;

        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);
        var maisNova = DateTime.UtcNow.AddHours(-2);
        var maisVelha = DateTime.UtcNow.AddHours(-6);

        // A mais NOVA chega primeiro — e o que acontece quando a entrega se embaralha.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-O1", "segunda", timestamp: Ts(maisNova)), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-O2", "primeira", timestamp: Ts(maisVelha)), default);

        var conversa = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal(maisVelha, conversa.AguardandoDesde!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Ultima_mensagem_NAO_REGRIDE_com_processamento_fora_de_ordem()
    {
        // Sem o guarda, a conversa descia na caixa de entrada e a previa voltava a um texto
        // velho — a lista parecia embaralhada sem ninguem ter feito nada.
        var (db, tx, amb) = await PrepararAsync("rec-regride");
        using var _ = db; using var __ = tx;

        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);
        var nova = DateTime.UtcNow.AddMinutes(-10);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-R1", "a mais nova", timestamp: Ts(nova)), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-R2", "a atrasada",
                timestamp: Ts(DateTime.UtcNow.AddHours(-5))), default);

        var conversa = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal(nova, conversa.UltimaMensagemEm, TimeSpan.FromSeconds(2));
        Assert.Equal("a mais nova", conversa.UltimaMensagemPrevia);
    }

    [Fact]
    public async Task Entrada_ANTERIOR_a_uma_resposta_nossa_nao_reabre_o_semaforo()
    {
        // A pior das regressoes possiveis: conversa ja respondida voltando a acender porque uma
        // mensagem velha do cliente so agora foi gravada. O vendedor responderia duas vezes.
        var (db, tx, amb) = await PrepararAsync("rec-respondida");
        using var _ = db; using var __ = tx;

        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);

        // Cliente pergunta -> nos respondemos (fromMe) -> so entao a pergunta ANTERIOR atrasa.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-S1", "pergunta",
                timestamp: Ts(DateTime.UtcNow.AddHours(-3))), default);
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-S2", "respondido", fromMe: true,
                timestamp: Ts(DateTime.UtcNow.AddHours(-1))), default);

        var antes = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Null(antes.AguardandoDesde);
        Assert.Equal(0, antes.NaoLidas);

        // Agora chega, atrasada, outra pergunta ANTERIOR a resposta.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "REC-S3", "pergunta esquecida",
                timestamp: Ts(DateTime.UtcNow.AddHours(-2))), default);

        var depois = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Null(depois.AguardandoDesde);
        Assert.Equal(0, depois.NaoLidas);
    }

    [Fact]
    public async Task Nao_lidas_reflete_entrada_saida_entrada_processadas_em_ordem()
    {
        var (db, tx, amb) = await PrepararAsync("rec-naolidas");
        using var _ = db; using var __ = tx;

        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);
        var t0 = DateTime.UtcNow.AddHours(-4);

        foreach (var (id, texto, meu, min) in new[]
        {
            ("REC-N1", "oi", false, 0), ("REC-N2", "oi!", true, 10),
            ("REC-N3", "tem?", false, 20), ("REC-N4", "quanto custa?", false, 30)
        })
        {
            await amb.Processador.ProcessarAsync(
                PayloadEvolution.Mensagem(amb.Instancia, Jid, id, texto, fromMe: meu,
                    timestamp: Ts(t0.AddMinutes(min))), default);
        }

        var conversa = await ConversaAsync(db, amb.Cenario.Id);
        Assert.Equal(2, conversa.NaoLidas);   // as duas DEPOIS da resposta, nao as tres do total
        Assert.Equal(t0.AddMinutes(20), conversa.AguardandoDesde!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Reprocessar_a_mesma_janela_duas_vezes_nao_duplica_nem_infla_o_contador()
    {
        // O `ON CONFLICT DO NOTHING` sobre uq_msg_wa_id ja fazia o trabalho; o que este teste
        // fixa e que o caminho de recuperacao nao passa POR FORA dele.
        var (db, tx, amb) = await PrepararAsync("rec-2x");
        using var _ = db; using var __ = tx;

        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);
        var quando = Ts(DateTime.UtcNow.AddHours(-3));

        for (var volta = 0; volta < 2; volta++)
            foreach (var id in new[] { "REC-2A", "REC-2B", "REC-2C" })
                await amb.Processador.ProcessarAsync(
                    PayloadEvolution.Mensagem(amb.Instancia, Jid, id, "oi", timestamp: quando), default);

        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.Mensagens.IgnoreQueryFilters()
            .CountAsync(m => m.EmpresaId == amb.Cenario.Id));
        Assert.Equal(3, (await ConversaAsync(db, amb.Cenario.Id)).NaoLidas);
    }

    [Fact]
    public async Task NENHUM_envio_sai_durante_a_recuperacao()
    {
        // Dez follow-ups disparados de uma vez ao religar e o caminho curto para o numero ser
        // banido. O webhook nunca envia — este teste existe para que continue assim quando
        // alguem "melhorar" o processador com uma resposta automatica.
        var (db, tx, amb) = await PrepararAsync("rec-sem-envio");
        using var _ = db; using var __ = tx;

        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);
        var t0 = DateTime.UtcNow.AddHours(-6);

        for (var i = 0; i < 5; i++)
            await amb.Processador.ProcessarAsync(
                PayloadEvolution.Mensagem(amb.Instancia, Jid, $"REC-E{i}", "oi",
                    timestamp: Ts(t0.AddMinutes(i))), default);

        Assert.Empty(amb.Cliente.TextosEnviados);

        db.ChangeTracker.Clear();
        Assert.False(await db.Mensagens.IgnoreQueryFilters()
            .AnyAsync(m => m.EmpresaId == amb.Cenario.Id && m.Direcao == DirecaoMensagem.Saida));
    }

    [Fact]
    public async Task A_MIDIA_E_BAIXADA_MANDANDO_A_MENSAGEM_INTEIRA_e_nao_so_a_chave()
    {
        // ===================== O DEFEITO QUE ISTO TRAVA =====================
        // A Evolution decodifica a midia a partir da PROPRIA mensagem (a `mediaKey` vem nela).
        // Mandando so `{key:{id}}` ela procura no banco DELA — e o compose desliga
        // `DATABASE_SAVE_DATA_NEW_MESSAGE` de proposito. A resposta era 400 "Message not found",
        // e TODA midia recebida entrava sem anexo: `tipo_midia = nenhum`, texto vazio, sem erro
        // em lugar nenhum. Verificado contra a Evolution v2.3.7 com uma mensagem real.
        // ====================================================================
        var (db, tx, amb) = await PrepararAsync("midia-payload");
        using var _ = db; using var __ = tx;

        await CriarContatoAsync(db, amb.Cenario, "Cliente", Telefone);
        amb.Cliente.MidiaParaDevolver = new MidiaRecebida(
            Convert.ToBase64String(new byte[64]), "audio/ogg; codecs=opus", "voz.oga");

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Midia(amb.Instancia, Jid, "WA-VOZ", "audio/ogg; codecs=opus",
                messageType: "audioMessage"), default);

        // O que foi PARA a Evolution tem a mensagem inteira, nao so o id.
        var pedido = amb.Cliente.UltimaMensagemJson;
        Assert.NotNull(pedido);
        Assert.Contains("\"key\"", pedido);
        Assert.Contains("audioMessage", pedido);
        Assert.Contains("messageTimestamp", pedido);

        // E o audio entrou COM anexo — nao como mensagem vazia.
        var m = await MensagemAsync(db, "WA-VOZ");
        Assert.Equal(TipoMidia.Audio, m!.TipoMidia);
        Assert.NotNull(m.MidiaChave);
    }


    // ==================================================================== a nota do NPS (NPS-1)

    /// <summary>===================== A NOTA NAO ACENDE O SEMAFORO =====================
    ///
    /// Critério de aceite do prompt, e o ponto inteiro desta etapa: "10" nao e pergunta. Ninguem
    /// tem de responder, e cobrar o vendedor por isso seria o sistema inventando trabalho.
    ///
    /// ⚠️ MAS A MENSAGEM FICA NA CONVERSA, e isso tambem e testado aqui: ela aconteceu, e
    /// esconde-la faria o vendedor ver a nota no relatorio e nao achar de onde veio. O que ela nao
    /// faz e mexer em `aguardando_desde` e `nao_lidas`.
    /// ======================================================================</summary>
    [Fact]
    public async Task A_NOTA_DO_NPS_NAO_ACENDE_O_SEMAFORO_MAS_FICA_NA_CONVERSA()
    {
        var (db, tx, amb) = await PrepararAsync("nps-semaforo");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaEnviadaNpsAsync(db, amb);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-NOTA-1", "10"), default);

        db.ChangeTracker.Clear();
        var conversa = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Contato.Telefone == Telefone);

        Assert.Null(conversa.AguardandoDesde);
        Assert.Equal(0, conversa.NaoLidas);

        // E a mensagem esta la, e e a ultima.
        Assert.Equal(DirecaoMensagem.Entrada, conversa.UltimaMensagemDirecao);
        Assert.Equal("10", conversa.UltimaMensagemPrevia);

        var nota = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.WaMessageId == "WA-NOTA-1");

        Assert.True(nota.TratadaPorAutomacao);

        // E a pesquisa foi respondida.
        Assert.Equal(StatusPesquisaNps.Respondida,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == pesquisa).Select(x => x.Status).SingleAsync());
    }

    /// <summary>===================== A DUVIDA *ACENDE* O SEMAFORO =====================
    ///
    /// ⚠️ O ESPELHO DO TESTE ACIMA, E E ELE QUE IMPEDE O EXCESSO DE ZELO. "quero 2 unidades" tem um
    /// numero de 0 a 10 e NAO e nota — e um pedido esperando resposta. Suprimir a espera dele para
    /// perguntar "isto e uma nota?" trocaria um atendimento perdido por uma duvida respondida.
    /// ======================================================================</summary>
    [Fact]
    public async Task A_DUVIDA_DO_NPS_ACENDE_O_SEMAFORO_NORMALMENTE()
    {
        var (db, tx, amb) = await PrepararAsync("nps-duvida");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaEnviadaNpsAsync(db, amb);

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-DUV-1", "quero 2 unidades"), default);

        db.ChangeTracker.Clear();
        var conversa = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Contato.Telefone == Telefone);

        Assert.NotNull(conversa.AguardandoDesde);
        Assert.Equal(1, conversa.NaoLidas);

        Assert.False(await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.WaMessageId == "WA-DUV-1").Select(m => m.TratadaPorAutomacao).SingleAsync());

        Assert.Equal(StatusPesquisaNps.PossivelNota,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == pesquisa).Select(x => x.Status).SingleAsync());
    }

    /// <summary>===================== O CARD DE VENDA CONTINUA ESPERANDO =====================
    ///
    /// Critério de aceite do prompt: "Contato com card de Venda aguardando resposta continua com o
    /// semaforo correto apos o NPS do card de Pos-venda".
    ///
    /// ⚠️ A CONVERSA DO WHATSAPP E UMA POR CONTATO, e e aí que mora o perigo: o cliente pergunta
    /// algo sobre o orcamento novo (semaforo ACESO), e a nota da compra passada chega depois. Se a
    /// nota apagasse a espera, o vendedor perderia a pergunta que estava em aberto.
    /// ==========================================================================</summary>
    [Fact]
    public async Task A_NOTA_NAO_APAGA_A_ESPERA_DE_UMA_PERGUNTA_ANTERIOR()
    {
        var (db, tx, amb) = await PrepararAsync("nps-outro-card");
        using var _ = db; using var __ = tx;

        await PesquisaEnviadaNpsAsync(db, amb);

        // O cliente pergunta algo — o semaforo acende.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-PERG", "quanto fica o orçamento novo?",
                                      timestamp: 1780000100), default);

        db.ChangeTracker.Clear();
        var espera = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Contato.Telefone == Telefone).Select(c => c.AguardandoDesde).SingleAsync();

        Assert.NotNull(espera);

        // Depois chega a nota da compra passada.
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-NOTA-2", "10",
                                      timestamp: 1780000200), default);

        db.ChangeTracker.Clear();
        var conversa = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Contato.Telefone == Telefone);

        // ⚠️ A ESPERA DA PERGUNTA CONTINUA, com a MESMA data: a nota nao a tocou.
        Assert.Equal(espera, conversa.AguardandoDesde);
        Assert.Equal(1, conversa.NaoLidas);
    }

    /// <summary>Citar a pergunta vence: `data.contextInfo.stanzaId` casando com o `wa_message_id` do
    /// envio transforma em nota o que sozinho seria duvida. ⚠️ O CAMINHO DO CAMPO E O QUE ESTE
    /// TESTE GUARDA — se ele mudar no modelo tipado, a citacao deixa de ser lida em silencio.</summary>
    [Fact]
    public async Task CITAR_A_PERGUNTA_PELO_PAYLOAD_VIRA_NOTA()
    {
        var (db, tx, amb) = await PrepararAsync("nps-citou");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaEnviadaNpsAsync(db, amb, waIdDoEnvio: "WA-PERGUNTA-NPS");

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-CIT-1", "quero 2 unidades",
                                      citando: "WA-PERGUNTA-NPS"), default);

        db.ChangeTracker.Clear();
        var p = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).SingleAsync();

        Assert.Equal(StatusPesquisaNps.Respondida, p.Status);
        Assert.Equal((short)2, p.Nota);
    }

    /// <summary>⚠️ SEM PESQUISA ABERTA, "10" E SO UMA MENSAGEM — e acende o semaforo como qualquer
    /// outra. Sem este teste, uma leitura que ignorasse o estado da pesquisa engoliria a mensagem de
    /// todo cliente que escrevesse um numero.</summary>
    [Fact]
    public async Task SEM_PESQUISA_ABERTA_UM_NUMERO_E_MENSAGEM_NORMAL()
    {
        var (db, tx, amb) = await PrepararAsync("nps-sem-pesquisa");
        using var _ = db; using var __ = tx;

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-NUM-1", "10"), default);

        db.ChangeTracker.Clear();
        var conversa = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Contato.Telefone == Telefone);

        Assert.NotNull(conversa.AguardandoDesde);
        Assert.Equal(1, conversa.NaoLidas);
    }

    /// <summary>Uma pesquisa ja ENVIADA para o contato do cenario, com a pergunta gravada.</summary>

    /// <summary>Liga a pesquisa e configura o agradecimento ao promotor — o que o dono faz na tela.</summary>
    private static async Task LigarAgradecimentoAsync(NexoraDbContext db, Ambiente amb)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.NpsAtivo, true)
                .SetProperty(e => e.NpsMensagemPromotor, "Obrigado pela nota!"));
        db.ChangeTracker.Clear();
    }

    /// <summary>===================== O AGRADECIMENTO SAI DEPOIS DE TUDO GRAVADO =====================
    ///
    /// ⚠️ O CASO DA REVISAO: as acoes da nota rodavam DENTRO da leitura, antes de a conversa ser
    /// gravada e da transacao fechar. No instante do POST, a conversa ainda nao tinha a nota.
    ///
    /// O gancho `AoEnviar` olha o banco no exato momento em que a Evolution seria chamada: a
    /// conversa ja tem de mostrar a nota como ultima mensagem.
    /// ======================================================================================</summary>
    [Fact]
    public async Task O_AGRADECIMENTO_DA_NOTA_SAI_DEPOIS_DE_A_CONVERSA_SER_GRAVADA()
    {
        var (db, tx, amb) = await PrepararAsync("nps-depois");
        using var _ = db; using var __ = tx;

        await PesquisaEnviadaNpsAsync(db, amb);
        await LigarAgradecimentoAsync(db, amb);

        string? previaNoEnvio = null;
        amb.Cliente.AoEnviar = async () =>
        {
            previaNoEnvio = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.Contato.Telefone == Telefone)
                .Select(c => c.UltimaMensagemPrevia).SingleAsync();
        };

        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-NOTA-DEPOIS", "10"), default);

        var agradecimento = Assert.Single(amb.Cliente.TextosEnviados);
        Assert.Equal("Obrigado pela nota!", agradecimento.Texto);
        Assert.Equal("10", previaNoEnvio);
    }

    /// <summary>===================== FALHOU A GRAVACAO, NAO SAI NADA =====================
    ///
    /// ⚠️ O OUTRO LADO, e e o dano que a revisao descreveu: a gravacao da conversa falha depois da
    /// leitura. Antes, o agradecimento JA TINHA SAIDO — e a reentrega do webhook agradeceria de novo.
    /// Agora as acoes correm so depois do commit, e com a falha nao ha commit: nada sai.
    /// ================================================================================</summary>
    [Fact]
    public async Task SE_A_GRAVACAO_DA_CONVERSA_FALHA_O_AGRADECIMENTO_NAO_SAI()
    {
        var falha = new FalhaNoComando("UPDATE conversas");
        var (db, tx, amb) = await PrepararAsync("nps-falha", falha);
        using var _ = db; using var __ = tx;

        await PesquisaEnviadaNpsAsync(db, amb);
        await LigarAgradecimentoAsync(db, amb);

        falha.Armada = true;
        await amb.Processador.ProcessarAsync(
            PayloadEvolution.Mensagem(amb.Instancia, Jid, "WA-NOTA-FALHA", "10"), default);
        falha.Armada = false;

        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    private static async Task<long> PesquisaEnviadaNpsAsync(
        NexoraDbContext db, Ambiente amb, string? waIdDoEnvio = null)
    {
        var contato = await db.Contatos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Telefone == Telefone);

        if (contato is null)
        {
            contato = new Contato
            {
                EmpresaId = amb.Cenario.Id, Nome = "Cliente NPS", Telefone = Telefone
            };
            db.Contatos.Add(contato);
            await db.SaveChangesAsync();
        }

        var conversa = await db.Conversas.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ContatoId == contato.Id);

        if (conversa is null)
        {
            conversa = new Conversa
            {
                EmpresaId = amb.Cenario.Id,
                ContatoId = contato.Id,
                ConexaoId = amb.Cenario.Conexao.Id,
                // ⚠️ ANTES DO TIMESTAMP DO PAYLOAD (1780000000 = 28/05/2026 20:26 UTC). A regra
                // REC-1 ignora mensagem ATRASADA — se a conversa fosse mais nova, a nota nao
                // viraria a ultima mensagem e o teste mediria a protecao contra atraso em vez da
                // supressao do semaforo. Foi o que me custou uma rodada.
                UltimaMensagemEm = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)
            };
            db.Conversas.Add(conversa);
            await db.SaveChangesAsync();
        }

        var etapa = amb.Cenario.Etapas[0];

        var negocio = new Negociacao
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = contato.Id,
            PipelineId = etapa.PipelineId,
            EtapaId = etapa.Id,
            Status = StatusNegociacao.Concluida,
            Valor = 1000m,
            GanhaEm = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            ConcluidaEm = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)
        };
        db.Negociacoes.Add(negocio);
        await db.SaveChangesAsync();

        var pergunta = new Mensagem
        {
            EmpresaId = amb.Cenario.Id,
            ConversaId = conversa.Id,
            ContatoId = contato.Id,
            ConexaoId = amb.Cenario.Conexao.Id,
            InstanceName = amb.Cenario.Conexao.InstanceName,
            Direcao = DirecaoMensagem.Saida,
            Texto = "De 0 a 10, quanto você recomendaria a gente?",
            Origem = OrigemMensagem.Automatica,
            TipoAutomacao = TipoAutomacao.Nps,
            NegociacaoId = negocio.Id,
            DataDisparo = new DateOnly(2026, 8, 6),
            WaMessageId = waIdDoEnvio
        };
        db.Mensagens.Add(pergunta);
        await db.SaveChangesAsync();

        var pesquisa = new PesquisaNps
        {
            EmpresaId = amb.Cenario.Id,
            NegociacaoId = negocio.Id,
            ContatoId = contato.Id,
            MensagemEnvioId = pergunta.Id,
            Status = StatusPesquisaNps.Enviada,
            DataAgendada = new DateOnly(2026, 8, 6),
            DataLimite = new DateOnly(2026, 8, 13),
            DataEnvio = new DateTime(2026, 8, 6, 11, 0, 0, DateTimeKind.Utc)
        };
        db.PesquisasNps.Add(pesquisa);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return pesquisa.Id;
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, string Instancia, ProcessadorEventoEvolution Processador,
        ClienteWhatsAppFalso Cliente, ArmazenamentoFalso Armazenamento, NotificadorFalso Painel);

    /// <summary>Monta o processador com o contexto em TENANT ZERO — como o webhook real roda.
    /// Se algum IgnoreQueryFilters faltar, e aqui que aparece.</summary>
    private async Task<(NexoraDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx, Ambiente Amb)>
        PrepararAsync(string sufixo, FalhaNoComando? falha = null)
    {
        var ctx = new ContextoMutavel();   // EmpresaId = 0
        var db = banco.NovoContexto(ctx, falha: falha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);

        // O Semeador cria contato/conversa/mensagem de exemplo; este bloco testa o webhook do
        // zero, entao limpa o que atrapalha a contagem.
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        // A negociacao sai ANTES do contato: `fk_negociacoes_contato` e `Restrict`, porque a
        // negociacao e o registro do negocio e o contato nao pode leva-la junto ao sumir.
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        var cliente = new ClienteWhatsAppFalso();
        var armazenamento = new ArmazenamentoFalso();
        var painel = new NotificadorFalso();

        var processador = new ProcessadorEventoEvolution(db, cliente, armazenamento, painel, PublicadorDeTeste.Novo(db), PublicadorConversoesDeTeste.Novo(db),
            // A leitura da nota de NPS DE VERDADE, nao um duble: ela roda no caminho quente de
            // toda mensagem recebida, e um duble esconderia o custo e os efeitos dela aqui.
            // ⚠️ COM O MESMO CLIENTE de WhatsApp: o agradecimento da nota sai por ele, e com um
            // cliente separado nenhum teste de webhook enxergaria o que foi mandado ao cliente.
            LeituraNpsDeTeste.Novo(db, TimeProvider.System, cliente), TimeProvider.System,
            NullLogger<ProcessadorEventoEvolution>.Instance);

        return (db, tx, new Ambiente(
            cenario, cenario.Conexao.InstanceName, processador, cliente, armazenamento, painel));
    }

    private static async Task<Contato> CriarContatoAsync(
        NexoraDbContext db, Cenario c, string nome, string telefone)
    {
        var contato = new Contato
        {
            EmpresaId = c.Id, Nome = nome, Telefone = telefone
        };
        db.Contatos.Add(contato);
        db.Negociacoes.Add(Semeador.Negocio(contato, c.PrimeiraEtapa));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return contato;
    }

    private static async Task<Conversa> CriarConversaAsync(NexoraDbContext db, Cenario c, Contato contato)
    {
        var conversa = new Conversa
        {
            EmpresaId = c.Id, ContatoId = contato.Id, ConexaoId = c.Conexao.Id,
            UltimaMensagemEm = DateTime.UtcNow
        };
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return conversa;
    }

    private static async Task<Mensagem?> MensagemAsync(NexoraDbContext db, string waId)
    {
        db.ChangeTracker.Clear();
        return await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.WaMessageId == waId);
    }

    /// <summary>`instancia` escolhe o número: desde o CONV-XX o contato tem uma conversa por
    /// número, e sem ela a primeira que o banco devolver vale.</summary>
    private static async Task<Conversa> ConversaAsync(
        NexoraDbContext db, long empresaId, string? instancia = null)
    {
        db.ChangeTracker.Clear();
        return await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(c => c.EmpresaId == empresaId
                          && (instancia == null || c.Conexao.InstanceName == instancia));
    }
}
