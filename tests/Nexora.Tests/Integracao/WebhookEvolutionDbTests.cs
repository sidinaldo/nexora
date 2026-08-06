using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Entidades;
using Nexora.Infra.Evolution;
using Nexora.Infra.Persistencia;

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
    public async Task Numero_desconhecido_cria_contato_em_Novo_Lead_sem_responsavel()
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

        // Etapa de MENOR ordem = Novo Lead.
        var etapa = await db.EtapasFunil.IgnoreQueryFilters().SingleAsync(e => e.Id == contato.EtapaId);
        Assert.Equal(1, etapa.Ordem);

        // E a conversa nasceu junto.
        Assert.True(await db.Conversas.IgnoreQueryFilters().AnyAsync(c => c.ContatoId == contato.Id));

        // O painel foi avisado das tres coisas.
        Assert.Single(amb.Painel.Contatos);
        Assert.Single(amb.Painel.Conversas);
        Assert.Single(amb.Painel.Mensagens);
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
        db.Contatos.Add(new Contato
        {
            EmpresaId = outro.Id, Nome = "Cliente de B", Telefone = Telefone,
            EtapaId = outro.PrimeiraEtapa.Id
        });
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

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, string Instancia, ProcessadorEventoEvolution Processador,
        ClienteWhatsAppFalso Cliente, ArmazenamentoFalso Armazenamento, NotificadorFalso Painel);

    /// <summary>Monta o processador com o contexto em TENANT ZERO — como o webhook real roda.
    /// Se algum IgnoreQueryFilters faltar, e aqui que aparece.</summary>
    private async Task<(NexoraDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx, Ambiente Amb)>
        PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();   // EmpresaId = 0
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);

        // O Semeador cria contato/conversa/mensagem de exemplo; este bloco testa o webhook do
        // zero, entao limpa o que atrapalha a contagem.
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        var cliente = new ClienteWhatsAppFalso();
        var armazenamento = new ArmazenamentoFalso();
        var painel = new NotificadorFalso();

        var processador = new ProcessadorEventoEvolution(
            db, cliente, armazenamento, painel, PublicadorDeTeste.Novo(db), TimeProvider.System,
            NullLogger<ProcessadorEventoEvolution>.Instance);

        return (db, tx, new Ambiente(
            cenario, cenario.Conexao.InstanceName, processador, cliente, armazenamento, painel));
    }

    private static async Task<Contato> CriarContatoAsync(
        NexoraDbContext db, Cenario c, string nome, string telefone)
    {
        var contato = new Contato
        {
            EmpresaId = c.Id, Nome = nome, Telefone = telefone, EtapaId = c.PrimeiraEtapa.Id
        };
        db.Contatos.Add(contato);
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

    private static async Task<Conversa> ConversaAsync(NexoraDbContext db, long empresaId)
    {
        db.ChangeTracker.Clear();
        return await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(c => c.EmpresaId == empresaId);
    }
}
