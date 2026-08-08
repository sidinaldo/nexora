using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O protocolo de envio contra Postgres real.
///
/// O caminho ingenuo (chamar a Evolution e gravar depois) produz mensagem duplicada de formas
/// que so aparecem sob falha: timeout que na verdade chegou, crash entre as duas etapas, webhook
/// reentregue. Estes testes existem para que a inversao do protocolo quebre alto.</summary>
[Collection("banco")]
public class EnvioMensagemDbTests(BancoTeste banco)
{
    // ==================================================================== o protocolo
    [Fact]
    public async Task A_linha_e_gravada_ANTES_de_chamar_a_Evolution()
    {
        // O TESTE MAIS IMPORTANTE DO BLOCO. O gancho do cliente falso consulta o banco no exato
        // momento em que a Evolution estaria sendo chamada. Se alguem inverter o protocolo, a
        // linha nao estara la e este teste quebra.
        var (db, tx, amb) = await PrepararAsync("protocolo");
        using var _ = db; using var __ = tx;

        long? idVistoDuranteAChamada = null;
        DateTime? enviadaEmDuranteAChamada = null;

        amb.Cliente.AoEnviar = async () =>
        {
            var linha = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.ConversaId == amb.Conversa.Id && m.Direcao == DirecaoMensagem.Saida)
                .Select(m => new { m.Id, m.EnviadaEm })
                .FirstOrDefaultAsync();
            idVistoDuranteAChamada = linha?.Id;
            enviadaEmDuranteAChamada = linha?.EnviadaEm;
        };

        var r = await amb.Conversas.ResponderAsync(amb.Conversa.Id, "oi, tudo bem?", default);

        Assert.True(r.Enviada);
        Assert.NotNull(idVistoDuranteAChamada);                 // a linha JA existia
        Assert.Equal(r.MensagemId, idVistoDuranteAChamada);     // e e a mesma
        Assert.Null(enviadaEmDuranteAChamada);                  // ainda nao confirmada

        // So depois da volta e que enviada_em e wa_message_id aparecem.
        var depois = await MensagemAsync(db, r.MensagemId);
        Assert.NotNull(depois.EnviadaEm);
        Assert.Equal("WA-FAKE-1", depois.WaMessageId);
        Assert.Equal((short)1, depois.Tentativas);
    }

    [Fact]
    public async Task Evolution_com_erro_deixa_a_linha_com_o_erro_gravado_e_nao_enviada()
    {
        // A linha FICA. Apagar liberaria a invariante de dedupe — e um POST que na verdade
        // chegou (mas deu timeout na resposta) viraria mensagem duplicada no reenvio.
        var (db, tx, amb) = await PrepararAsync("erro");
        using var _ = db; using var __ = tx;

        amb.Cliente.ErroParaLancar = new IntegracaoWhatsAppException("Evolution API inacessivel (timeout).");

        var r = await amb.Conversas.ResponderAsync(amb.Conversa.Id, "vai falhar", default);

        Assert.False(r.Enviada);
        Assert.Contains("inacessivel", r.Erro!);

        var linha = await MensagemAsync(db, r.MensagemId);
        Assert.Null(linha.EnviadaEm);
        Assert.Null(linha.WaMessageId);
        Assert.Contains("inacessivel", linha.Erro!);
        Assert.Equal((short)1, linha.Tentativas);
        Assert.Equal("vai falhar", linha.Texto);   // o conteudo nao se perde
    }

    [Fact]
    public async Task Evolution_com_200_sem_key_id_nao_lanca_e_nao_reenvia()
    {
        // A Evolution as vezes responde 2xx SEM key.id. Lancar aqui faria o envio ser tentado de
        // novo e o contato receber duas vezes; o certo e aceitar, registrar sem id, e perder
        // apenas a capacidade de casar o ACK depois.
        var (db, tx, amb) = await PrepararAsync("sem-id");
        using var _ = db; using var __ = tx;

        amb.Cliente.IdParaDevolver = "";

        var r = await amb.Conversas.ResponderAsync(amb.Conversa.Id, "sem id de volta", default);

        Assert.True(r.Enviada);
        var linha = await MensagemAsync(db, r.MensagemId);
        Assert.NotNull(linha.EnviadaEm);
        Assert.Null(linha.WaMessageId);   // NULLIF('', '') -> NULL, nao string vazia
        Assert.Null(linha.Erro);
    }

    [Fact]
    public async Task Duas_confirmacoes_com_wa_message_id_vazio_ambas_passam()
    {
        // Se o NULLIF sumisse, duas strings vazias colidiriam no uq_msg_wa_id e a segunda
        // mensagem legitima seria recusada.
        var (db, tx, amb) = await PrepararAsync("dois-vazios");
        using var _ = db; using var __ = tx;

        amb.Cliente.IdParaDevolver = "";

        var a = await amb.Conversas.ResponderAsync(amb.Conversa.Id, "primeira", default);
        var b = await amb.Conversas.ResponderAsync(amb.Conversa.Id, "segunda", default);

        Assert.True(a.Enviada);
        Assert.True(b.Enviada);
        Assert.NotEqual(a.MensagemId, b.MensagemId);

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Mensagens.IgnoreQueryFilters()
            .CountAsync(m => m.ConversaId == amb.Conversa.Id && m.WaMessageId == null));
    }

    // ==================================================================== reenvio
    [Fact]
    public async Task Reenvio_da_mesma_linha_nao_cria_linha_nova()
    {
        var (db, tx, amb) = await PrepararAsync("reenvio");
        using var _ = db; using var __ = tx;

        var lembrete = await CriarLembreteAsync(db, amb, "follow-up");

        // Primeira tentativa: a Evolution esta fora.
        amb.Cliente.ErroParaLancar = new IntegracaoWhatsAppException("fora do ar");
        var reserva = NovaReserva(amb, lembrete, "vamos fechar?");
        Assert.Equal(ResultadoEnvio.Falhou,
            await amb.Enviador.EnviarLembreteAsync(reserva, amb.Contato.Telefone, default));

        // Só as de SAÍDA: o Semeador já deixou uma mensagem de entrada na conversa.
        db.ChangeTracker.Clear();
        var antes = await db.Mensagens.IgnoreQueryFilters()
            .CountAsync(m => m.ConversaId == amb.Conversa.Id && m.Direcao == DirecaoMensagem.Saida);
        Assert.Equal(1, antes);

        // Segunda: a Evolution voltou. O reenvio reaproveita a MESMA linha.
        amb.Cliente.ErroParaLancar = null;
        var pendentes = await amb.Enviador.PendentesAsync(amb.Cenario.Id, default);
        var pendente = Assert.Single(pendentes);

        Assert.Equal(ResultadoEnvio.Enviada,
            await amb.Enviador.ReenviarAsync(pendente, amb.Contato.Telefone, default));

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Mensagens.IgnoreQueryFilters()
            .CountAsync(m => m.ConversaId == amb.Conversa.Id && m.Direcao == DirecaoMensagem.Saida));

        var linha = await MensagemAsync(db, pendente.Id);
        Assert.NotNull(linha.EnviadaEm);
        Assert.Null(linha.Erro);                  // a confirmacao limpa o erro anterior
        Assert.Equal((short)2, linha.Tentativas); // mas o contador guarda que houve duas
    }

    // ==================================================================== invariantes
    [Fact]
    public async Task Mesmo_lembrete_nao_reserva_duas_vezes()
    {
        // uq_msg_lembrete. Um crash entre "insere mensagem" e "marca lembrete concluido", ou
        // duas instancias do motor, reenviariam sem isso. O banco e o arbitro.
        var (db, tx, amb) = await PrepararAsync("um-lembrete");
        using var _ = db; using var __ = tx;

        var lembrete = await CriarLembreteAsync(db, amb, "follow-up");

        Assert.Equal(ResultadoEnvio.Enviada,
            await amb.Enviador.EnviarLembreteAsync(NovaReserva(amb, lembrete, "oi"), amb.Contato.Telefone, default));

        // Segunda tentativa do MESMO lembrete: barrada pelo banco, sem excecao.
        Assert.Equal(ResultadoEnvio.Barrada,
            await amb.Enviador.EnviarLembreteAsync(NovaReserva(amb, lembrete, "oi de novo"), amb.Contato.Telefone, default));

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Mensagens.IgnoreQueryFilters()
            .CountAsync(m => m.LembreteId == lembrete.Id));
        Assert.Single(amb.Cliente.TextosEnviados);   // a Evolution so foi chamada uma vez
    }

    [Fact]
    public async Task Dois_lembretes_automaticos_no_mesmo_dia_o_segundo_e_barrado_pelo_banco()
    {
        // O TETO DIARIO ANTI-SPAM (uq_lembrete_teto_diario). Disparo em lote para o mesmo
        // destinatario e o jeito classico de ter o numero banido — e o Nexora roda em rota
        // nao-oficial, onde banimento e risco contratual.
        var (db, tx, amb) = await PrepararAsync("teto");
        using var _ = db; using var __ = tx;

        await CriarLembreteAsync(db, amb, "primeiro");

        var erro = await Assert.ThrowsAsync<DbUpdateException>(
            () => CriarLembreteAsync(db, amb, "segundo no mesmo dia"));
        Assert.NotNull(erro);
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Duas_mensagens_manuais_no_mesmo_dia_ambas_passam()
    {
        // Mensagem manual tem lembrete_id NULL: nao entra no teto diario nem no dedupe por
        // lembrete. O vendedor responde quantas vezes precisar.
        var (db, tx, amb) = await PrepararAsync("manual-x2");
        using var _ = db; using var __ = tx;

        var a = await amb.Conversas.ResponderAsync(amb.Conversa.Id, "bom dia", default);
        var b = await amb.Conversas.ResponderAsync(amb.Conversa.Id, "esqueci de dizer...", default);
        var c = await amb.Conversas.ResponderAsync(amb.Conversa.Id, "e mais uma coisa", default);

        Assert.True(a.Enviada && b.Enviada && c.Enviada);

        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.Mensagens.IgnoreQueryFilters().CountAsync(
            m => m.ConversaId == amb.Conversa.Id
              && m.Direcao == DirecaoMensagem.Saida
              && m.LembreteId == null));
    }

    // ==================================================================== freio por conexao
    [Fact]
    public async Task Conexao_caida_reserva_sem_postar()
    {
        // Com o numero fora, postar so empilha erro e atrasa a fila. A linha fica reservada
        // (enviada_em NULL) para a proxima drenagem recuperar — preservando a data-alvo exata.
        var (db, tx, amb) = await PrepararAsync("freio");
        using var _ = db; using var __ = tx;

        amb.Cliente.EstadoParaDevolver = "close";
        var lembrete = await CriarLembreteAsync(db, amb, "follow-up");

        Assert.False(await amb.Enviador.InstanciaConectadaAsync(amb.Cenario.Conexao.InstanceName, default));

        var reserva = NovaReserva(amb, lembrete, "vamos fechar?");
        Assert.Equal(ResultadoEnvio.Adiada,
            await amb.Enviador.ReservarLembreteAsync(reserva, default));

        db.ChangeTracker.Clear();
        var linha = await db.Mensagens.IgnoreQueryFilters()
            .SingleAsync(m => m.LembreteId == lembrete.Id);
        Assert.Null(linha.EnviadaEm);
        Assert.Null(linha.Erro);                 // nao e falha: nem chegou a tentar
        Assert.Equal((short)0, linha.Tentativas);
        Assert.Empty(amb.Cliente.TextosEnviados);

        // E a drenagem a encontra.
        var pendentes = await amb.Enviador.PendentesAsync(amb.Cenario.Id, default);
        Assert.Single(pendentes);
    }

    [Fact]
    public async Task Responder_com_conexao_caida_recusa_com_mensagem_clara()
    {
        // Diferente do lembrete automatico: o vendedor esta olhando a tela e precisa saber
        // AGORA, nao ficar com a mensagem numa fila invisivel.
        var (db, tx, amb) = await PrepararAsync("freio-manual");
        using var _ = db; using var __ = tx;

        amb.Cliente.EstadoParaDevolver = "close";

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversas.ResponderAsync(amb.Conversa.Id, "oi", default));

        Assert.True(erro.Conflito);
        Assert.Contains("desconectado", erro.Message, StringComparison.OrdinalIgnoreCase);

        // Nenhuma linha de SAÍDA foi criada: recusou antes de gravar.
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.ConversaId == amb.Conversa.Id && m.Direcao == DirecaoMensagem.Saida)
            .ToListAsync());
    }

    // ==================================================================== semaforo
    [Fact]
    public async Task Envio_manual_zera_aguardando_desde_e_nao_lidas()
    {
        // A regra veio do bloco 3 pelo caminho de RECEBIMENTO; o caminho de ENVIO tem que
        // respeita-la tambem, senao o semaforo fica vermelho numa conversa ja respondida.
        var (db, tx, amb) = await PrepararAsync("semaforo");
        using var _ = db; using var __ = tx;

        // Simula duas mensagens do cliente esperando resposta.
        await db.Conversas.IgnoreQueryFilters()
            .Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.AguardandoDesde, DateTime.UtcNow.AddHours(-5))
                .SetProperty(c => c.NaoLidas, 2));
        db.ChangeTracker.Clear();

        await amb.Conversas.ResponderAsync(amb.Conversa.Id, "desculpe a demora!", default);

        var conversa = await ConversaAsync(db, amb.Conversa.Id);
        Assert.Null(conversa.AguardandoDesde);
        Assert.Equal(0, conversa.NaoLidas);
        Assert.Equal(DirecaoMensagem.Saida, conversa.UltimaMensagemDirecao);
        Assert.Equal("desculpe a demora!", conversa.UltimaMensagemPrevia);
    }

    [Fact]
    public async Task Falha_no_envio_ainda_assim_zera_o_semaforo()
    {
        // Decisao consciente: a mensagem EXISTE e aparece na thread. Do ponto de vista de "quem
        // esta esperando resposta", nos ja respondemos — deixar vermelho faria o vendedor
        // responder de novo e o cliente receber duas vezes quando a fila drenasse.
        var (db, tx, amb) = await PrepararAsync("semaforo-falha");
        using var _ = db; using var __ = tx;

        await db.Conversas.IgnoreQueryFilters()
            .Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.AguardandoDesde, DateTime.UtcNow.AddHours(-1))
                .SetProperty(c => c.NaoLidas, 1));
        db.ChangeTracker.Clear();

        amb.Cliente.ErroParaLancar = new IntegracaoWhatsAppException("caiu");
        var r = await amb.Conversas.ResponderAsync(amb.Conversa.Id, "tentei responder", default);

        Assert.False(r.Enviada);
        var conversa = await ConversaAsync(db, amb.Conversa.Id);
        Assert.Null(conversa.AguardandoDesde);
        Assert.Equal(0, conversa.NaoLidas);
    }

    // ==================================================================== atribuicao
    [Fact]
    public async Task Responder_conversa_sem_dono_atribui_o_dono()
    {
        var (db, tx, amb) = await PrepararAsync("atribui");
        using var _ = db; using var __ = tx;

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, (long?)null));
        db.ChangeTracker.Clear();

        await amb.Conversas.ResponderAsync(amb.Conversa.Id, "eu assumo", default);

        var conversa = await ConversaAsync(db, amb.Conversa.Id);
        Assert.Equal(amb.Cenario.Dono.Id, conversa.ResponsavelId);
        Assert.NotNull(conversa.AtribuidoEm);
    }

    [Fact]
    public async Task Assumir_conversa_de_outro_devolve_409()
    {
        var (db, tx, amb) = await PrepararAsync("roubo");
        using var _ = db; using var __ = tx;

        // Um segundo vendedor na mesma empresa, dono da conversa.
        var outro = new Usuario
        {
            EmpresaId = amb.Cenario.Id, Nome = "Outro Vendedor",
            Email = $"outro-roubo@exemplo.com",
            SenhaHash = Nexora.Core.Seguranca.HashSenha.Gerar("senha-de-teste-123"),
            Papel = PapelUsuario.Vendedor, Status = StatusUsuario.Ativo
        };
        db.Usuarios.Add(outro);
        await db.SaveChangesAsync();

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, outro.Id));
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversas.AssumirAsync(amb.Conversa.Id, default));

        Assert.True(erro.Conflito);   // vira 409 no FiltroRegraDeNegocio
        Assert.Contains("outro vendedor", erro.Message, StringComparison.OrdinalIgnoreCase);

        // E a conversa continua com o dono original.
        Assert.Equal(outro.Id, (await ConversaAsync(db, amb.Conversa.Id)).ResponsavelId);
    }

    [Fact]
    public async Task Assumir_conversa_sem_dono_funciona_e_reassumir_a_propria_e_no_op()
    {
        var (db, tx, amb) = await PrepararAsync("assumir");
        using var _ = db; using var __ = tx;

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, (long?)null));
        db.ChangeTracker.Clear();

        await amb.Conversas.AssumirAsync(amb.Conversa.Id, default);
        Assert.Equal(amb.Cenario.Dono.Id, (await ConversaAsync(db, amb.Conversa.Id)).ResponsavelId);

        await amb.Conversas.AssumirAsync(amb.Conversa.Id, default);   // no-op, nao lanca
        Assert.Equal(amb.Cenario.Dono.Id, (await ConversaAsync(db, amb.Conversa.Id)).ResponsavelId);
    }

    /// <summary>===================== ASSUMIR A CONVERSA E FICAR COM O LEAD =====================
    ///
    /// Relatado assim: "na tabela de contato a coluna Responsável vem null".
    ///
    /// Duas colunas para a mesma ideia — `conversas.responsavel_id` e `contatos.responsavel_id` —
    /// e o fluxo REAL só escrevia a primeira. Quem digita o contato à mão preenche a segunda pelo
    /// formulário; quem chega pelo WhatsApp (ou seja, todo lead de verdade) nunca preenchia
    /// nenhuma, porque a única atribuição que acontece é o "Assumir" da caixa.
    ///
    /// Medido no banco de desenvolvimento: na empresa de trabalho, ZERO contatos com responsável
    /// e OITO conversas com responsável. Nas empresas de demonstração o oposto — 400 contatos e
    /// zero conversas —, porque lá quem escreve é o semeador. Nenhuma das duas metades estava
    /// completa, e por isso nada denunciava.
    ///
    /// ⚠️ NÃO ERA SÓ A COLUNA DA TABELA. Leem `contatos.responsavel_id`: a lista de contatos, o
    /// card do kanban, o filtro "por responsável" e o `Meu Dia`. Quatro telas mostrando "sem
    /// responsável" para leads que tinham dono há semanas.
    ///
    /// O semeador já documentava a invariante que faltava: ele copia
    /// `conversa.ResponsavelId = contato.ResponsavelId`. As duas devem andar juntas — o fluxo
    /// vivo é que nunca fechou o laço.
    /// ==================================================================================</summary>
    [Fact]
    public async Task ASSUMIR_A_CONVERSA_TAMBEM_DA_O_LEAD_AO_VENDEDOR()
    {
        var (db, tx, amb) = await PrepararAsync("assumir-lead");
        using var _ = db; using var __ = tx;

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, (long?)null));
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == amb.Contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, (long?)null));
        db.ChangeTracker.Clear();

        await amb.Conversas.AssumirAsync(amb.Conversa.Id, default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters()
            .AsNoTracking().SingleAsync(c => c.Id == amb.Contato.Id);

        Assert.Equal(amb.Cenario.Dono.Id, contato.ResponsavelId);
    }

    /// <summary>Liberar desfaz os dois lados. Soltar a conversa e deixar o lead no nome de quem
    /// saiu faria a lista de contatos e o kanban continuarem apontando para o vendedor errado —
    /// e é justamente o "Não atribuídas" que existe para que alguém pegue.</summary>
    [Fact]
    public async Task LIBERAR_A_CONVERSA_TAMBEM_SOLTA_O_LEAD()
    {
        var (db, tx, amb) = await PrepararAsync("liberar-lead");
        using var _ = db; using var __ = tx;

        await amb.Conversas.AssumirAsync(amb.Conversa.Id, default);
        db.ChangeTracker.Clear();

        await amb.Conversas.LiberarAsync(amb.Conversa.Id, default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters()
            .AsNoTracking().SingleAsync(c => c.Id == amb.Contato.Id);

        Assert.Null(contato.ResponsavelId);
    }

    /// <summary>⚠️ ASSUMIR NÃO ROUBA LEAD DE OUTRO VENDEDOR. Um gestor pode ter atribuído o
    /// contato a alguém pelo formulário; assumir a conversa é dizer "eu atendo", não "o lead
    /// virou meu". Só preenche o que está vago.</summary>
    [Fact]
    public async Task ASSUMIR_NAO_SOBRESCREVE_UM_RESPONSAVEL_JA_DEFINIDO_NO_CONTATO()
    {
        var (db, tx, amb) = await PrepararAsync("assumir-nao-rouba");
        using var _ = db; using var __ = tx;

        var outro = new Usuario
        {
            EmpresaId = amb.Cenario.Id, Nome = "Gestor definiu este",
            Email = "dono-do-lead@exemplo.com",
            SenhaHash = Nexora.Core.Seguranca.HashSenha.Gerar("senha-de-teste-123"),
            Papel = PapelUsuario.Vendedor, Status = StatusUsuario.Ativo
        };
        db.Usuarios.Add(outro);
        await db.SaveChangesAsync();

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, (long?)null));
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == amb.Contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, outro.Id));
        db.ChangeTracker.Clear();

        await amb.Conversas.AssumirAsync(amb.Conversa.Id, default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters()
            .AsNoTracking().SingleAsync(c => c.Id == amb.Contato.Id);
        var conversa = await ConversaAsync(db, amb.Conversa.Id);

        Assert.Equal(outro.Id, contato.ResponsavelId);                 // o lead continua dele
        Assert.Equal(amb.Cenario.Dono.Id, conversa.ResponsavelId);     // o atendimento é meu
    }

    /// <summary>E liberar só solta o lead se ele for de quem está liberando — mesma razão.</summary>
    [Fact]
    public async Task LIBERAR_NAO_SOLTA_O_LEAD_DE_OUTRO_VENDEDOR()
    {
        var (db, tx, amb) = await PrepararAsync("liberar-nao-rouba");
        using var _ = db; using var __ = tx;

        var outro = new Usuario
        {
            EmpresaId = amb.Cenario.Id, Nome = "Dono do lead",
            Email = "dono-do-lead-2@exemplo.com",
            SenhaHash = Nexora.Core.Seguranca.HashSenha.Gerar("senha-de-teste-123"),
            Papel = PapelUsuario.Vendedor, Status = StatusUsuario.Ativo
        };
        db.Usuarios.Add(outro);
        await db.SaveChangesAsync();

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, amb.Cenario.Dono.Id));
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == amb.Contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, outro.Id));
        db.ChangeTracker.Clear();

        await amb.Conversas.LiberarAsync(amb.Conversa.Id, default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.IgnoreQueryFilters()
            .AsNoTracking().SingleAsync(c => c.Id == amb.Contato.Id);

        Assert.Equal(outro.Id, contato.ResponsavelId);
        Assert.Null((await ConversaAsync(db, amb.Conversa.Id)).ResponsavelId);
    }

    // ==================================================================== expiracao
    [Fact]
    public async Task Reserva_que_esgota_a_janela_fica_marcada_como_expirada_e_nao_some()
    {
        // O buraco do Recupera: la a reserva simplesmente sai do alcance da varredura e some do
        // radar — o alerta conta pendentes sem separar "vai ser tentada" de "nunca mais sera".
        var (db, tx, amb) = await PrepararAsync("expira");
        using var _ = db; using var __ = tx;

        var lembrete = await CriarLembreteAsync(db, amb, "antigo");

        // Reserva com data-alvo bem no passado (a Evolution ficou fora por uma semana).
        var reserva = NovaReserva(amb, lembrete, "mensagem velha");
        reserva.DataDisparo = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10);
        Assert.Equal(ResultadoEnvio.Adiada, await amb.Enviador.ReservarLembreteAsync(reserva, default));

        // Fora da janela de reenvio: a drenagem nao a alcanca mais.
        Assert.Empty(await amb.Enviador.PendentesAsync(amb.Cenario.Id, default));

        // Mas ela NAO some: ganha estado terminal explicito.
        Assert.Equal(1, await amb.Enviador.ExpirarVencidasAsync(amb.Cenario.Id, default));

        db.ChangeTracker.Clear();
        var linha = await db.Mensagens.IgnoreQueryFilters().SingleAsync(m => m.LembreteId == lembrete.Id);
        Assert.NotNull(linha.ExpiradaEm);
        Assert.Null(linha.EnviadaEm);
        Assert.Equal("mensagem velha", linha.Texto);   // o conteudo continua auditavel

        // E expirar de novo nao conta duas vezes.
        Assert.Equal(0, await amb.Enviador.ExpirarVencidasAsync(amb.Cenario.Id, default));
    }

    [Fact]
    public async Task Reserva_dentro_da_janela_nao_expira()
    {
        var (db, tx, amb) = await PrepararAsync("nao-expira");
        using var _ = db; using var __ = tx;

        var lembrete = await CriarLembreteAsync(db, amb, "recente");
        var reserva = NovaReserva(amb, lembrete, "de ontem");
        reserva.DataDisparo = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        await amb.Enviador.ReservarLembreteAsync(reserva, default);

        Assert.Equal(0, await amb.Enviador.ExpirarVencidasAsync(amb.Cenario.Id, default));
        Assert.Single(await amb.Enviador.PendentesAsync(amb.Cenario.Id, default));
    }

    // ==================================================================== saude
    [Fact]
    public async Task Saude_separa_enviadas_pendentes_e_expiradas()
    {
        var (db, tx, amb) = await PrepararAsync("saude");
        using var _ = db; using var __ = tx;

        // 1 enviada (manual)
        await amb.Conversas.ResponderAsync(amb.Conversa.Id, "enviada", default);

        // 1 pendente (conexao caida na hora da reserva)
        var l1 = await CriarLembreteAsync(db, amb, "pendente", DateOnly.FromDateTime(DateTime.UtcNow));
        await amb.Enviador.ReservarLembreteAsync(NovaReserva(amb, l1, "esperando"), default);

        // 1 expirada
        var l2 = await CriarLembreteAsync(db, amb, "velho", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-9));
        var velha = NovaReserva(amb, l2, "perdida");
        velha.DataDisparo = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-9);
        await amb.Enviador.ReservarLembreteAsync(velha, default);
        await amb.Enviador.ExpirarVencidasAsync(amb.Cenario.Id, default);

        db.ChangeTracker.Clear();
        amb.Contexto.EmpresaId = amb.Cenario.Id;   // saude roda autenticado

        var saude = await amb.Conexoes.SaudeAsync(amb.Cenario.Conexao.Id, default);

        Assert.Equal(1, saude.EnviadasHoje);
        Assert.Equal(1, saude.Pendentes);
        Assert.Equal(1, saude.Expiradas);
        Assert.Equal(0, saude.FalhasHoje);
    }

    // ============================================================ MID-1 · envio de midia
    /// <summary>Um JPEG minimo VALIDO pelos bytes iniciais. Nao precisa ser imagem decodificavel:
    /// o que o servidor confere e a assinatura.</summary>
    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[64]];
    private static byte[] Pdf() => [.. "%PDF-1.7"u8, .. new byte[64]];

    [Fact]
    public async Task ENVIAR_IMAGEM_GRAVA_A_LINHA_ANTES_DE_CHAMAR_A_EVOLUTION()
    {
        // O protocolo do bloco 4, agora com anexo. Inverter a ordem produziria arquivo entregue
        // ao cliente sem registro nenhum — o unico erro deste desenho que nao tem conserto.
        var (db, tx, amb) = await PrepararAsync("mid-imagem");
        using var _ = db; using var __ = tx;

        amb.Cliente.IdParaDevolver = "WA-IMG";

        // ===== A PROVA DA ORDEM =====
        // Afirmar depois que a linha existe não prova NADA sobre ordem — ela existiria de
        // qualquer jeito. O gancho roda DENTRO da chamada, no instante em que a Evolution
        // estaria sendo chamada: se a gravação viesse depois, aqui não haveria linha.
        long? idNoMomentoDoPost = null;
        //
        // O MESMO `db`, não uma conexão nova: o teste inteiro roda numa transação aberta, e outra
        // conexão não enxergaria escrita ainda não commitada — o espião veria `null` e acusaria
        // um defeito que não existe.
        amb.Cliente.AoEnviar = async () =>
        {
            idNoMomentoDoPost = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.ConversaId == amb.Conversa.Id && m.MidiaChave != null)
                .Select(m => (long?)m.Id).FirstOrDefaultAsync();
        };

        var r = await amb.Conversas.EnviarMidiaAsync(
            amb.Conversa.Id, new ArquivoParaEnvio(Jpeg(), "orcamento.jpg", "image/jpeg"),
            "segue a foto", default);

        Assert.True(r.Enviada);
        Assert.Equal(r.MensagemId, idNoMomentoDoPost);   // a linha JÁ existia no POST

        db.ChangeTracker.Clear();
        var m = await db.Mensagens.AsNoTracking().SingleAsync(x => x.Id == r.MensagemId);

        // E o que saiu foi o arquivo certo, com o mediatype que a Evolution espera.
        var enviada = Assert.Single(amb.Cliente.MidiasEnviadas);
        Assert.Equal("image", enviada.Mediatype);
        Assert.Equal("image/jpeg", enviada.Mime);
        Assert.Equal("segue a foto", enviada.Legenda);

        Assert.Equal(TipoMidia.Imagem, m.TipoMidia);
        Assert.Equal("image/jpeg", m.MidiaMime);
        Assert.Equal("orcamento.jpg", m.MidiaNome);
        Assert.Equal("segue a foto", m.Texto);          // a LEGENDA vai na coluna texto
        Assert.Equal("WA-IMG", m.WaMessageId);          // confirmado depois do POST
        Assert.NotNull(m.EnviadaEm);
        Assert.NotNull(m.MidiaChave);

        // O arquivo foi para o armazenamento, uma vez so.
        Assert.Equal(1, amb.Armazenamento.Gravacoes);
        Assert.True(amb.Armazenamento.Objetos.ContainsKey(m.MidiaChave!));
    }

    [Fact]
    public async Task Enviar_PDF_sem_legenda_deixa_previa_util_na_caixa()
    {
        // Previa vazia faria a linha da caixa parecer que nada foi enviado.
        var (db, tx, amb) = await PrepararAsync("mid-pdf");
        using var _ = db; using var __ = tx;

        await amb.Conversas.EnviarMidiaAsync(
            amb.Conversa.Id, new ArquivoParaEnvio(Pdf(), "proposta.pdf", "application/pdf"),
            null, default);

        db.ChangeTracker.Clear();
        var c = await db.Conversas.AsNoTracking().SingleAsync(x => x.Id == amb.Conversa.Id);

        Assert.Contains("proposta.pdf", c.UltimaMensagemPrevia);
        Assert.Equal(DirecaoMensagem.Saida, c.UltimaMensagemDirecao);
        Assert.Null(c.AguardandoDesde);                 // responder zera o semaforo
    }

    [Fact]
    public async Task EXTENSAO_TROCADA_E_RECUSADA_PELO_CONTEUDO()
    {
        // Nome `.pdf`, Content-Type `application/pdf`, e por dentro um executavel. So a
        // verificacao dos BYTES pega isso.
        var (db, tx, amb) = await PrepararAsync("mid-fake");
        using var _ = db; using var __ = tx;

        byte[] executavel = [0x4D, 0x5A, 0x90, 0x00, .. new byte[32]];

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Conversas.EnviarMidiaAsync(
            amb.Conversa.Id, new ArquivoParaEnvio(executavel, "orcamento.pdf", "application/pdf"),
            null, default));

        // NADA foi gravado: nem linha, nem arquivo, nem POST.
        db.ChangeTracker.Clear();
        Assert.Equal(0, amb.Armazenamento.Gravacoes);
        Assert.Empty(amb.Cliente.TextosEnviados);
        Assert.False(await db.Mensagens.AnyAsync(m => m.MidiaChave != null));
    }

    [Fact]
    public async Task Tipo_fora_da_whitelist_de_ENVIO_e_recusado_mesmo_sendo_aceito_no_RECEBIMENTO()
    {
        var (db, tx, amb) = await PrepararAsync("mid-video");
        using var _ = db; using var __ = tx;

        // MP4 comeca com um box `ftyp`. A assinatura nao o reconhece, entao ele para no primeiro
        // portao — e mesmo que reconhecesse, `PermitidoParaEnvio` recusaria.
        byte[] mp4 = [0x00, 0x00, 0x00, 0x18, .. "ftypmp42"u8, .. new byte[32]];

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Conversas.EnviarMidiaAsync(
            amb.Conversa.Id, new ArquivoParaEnvio(mp4, "video.mp4", "video/mp4"), null, default));

        Assert.Equal(0, amb.Armazenamento.Gravacoes);
    }

    [Fact]
    public async Task Arquivo_acima_do_teto_e_recusado_ANTES_de_ir_para_o_disco()
    {
        var (db, tx, amb) = await PrepararAsync("mid-teto");
        using var _ = db; using var __ = tx;

        var grande = new byte[ValidadorMidia.TamanhoMaximoBytes + 1];
        grande[0] = 0xFF; grande[1] = 0xD8; grande[2] = 0xFF;   // JPEG valido, so grande demais

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Conversas.EnviarMidiaAsync(
            amb.Conversa.Id, new ArquivoParaEnvio(grande, "foto.jpg", "image/jpeg"), null, default));

        Assert.Equal(0, amb.Armazenamento.Gravacoes);
    }

    [Fact]
    public async Task FALHA_NO_ENVIO_MANTEM_A_LINHA_COM_ERRO_e_o_reenvio_REAPROVEITA_ELA()
    {
        var (db, tx, amb) = await PrepararAsync("mid-falha");
        using var _ = db; using var __ = tx;

        amb.Cliente.ErroParaLancar = new IntegracaoWhatsAppException("Evolution fora do ar");

        var r = await amb.Conversas.EnviarMidiaAsync(
            amb.Conversa.Id, new ArquivoParaEnvio(Pdf(), "proposta.pdf", "application/pdf"),
            "segue", default);

        Assert.False(r.Enviada);
        Assert.Contains("fora do ar", r.Erro);

        db.ChangeTracker.Clear();
        var antes = await db.Mensagens.AsNoTracking().SingleAsync(m => m.Id == r.MensagemId);
        Assert.NotNull(antes.Erro);
        Assert.Null(antes.EnviadaEm);
        Assert.NotNull(antes.MidiaChave);               // o arquivo continua guardado

        // Agora a Evolution volta.
        amb.Cliente.ErroParaLancar = null;
        amb.Cliente.IdParaDevolver = "WA-RETRY";

        var r2 = await amb.Conversas.ReenviarAsync(r.MensagemId, default);
        Assert.True(r2.Enviada);
        Assert.Equal(r.MensagemId, r2.MensagemId);      // a MESMA linha

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Mensagens.CountAsync(m => m.ConversaId == amb.Conversa.Id
                                                        && m.Direcao == DirecaoMensagem.Saida));
        var depois = await db.Mensagens.AsNoTracking().SingleAsync(m => m.Id == r.MensagemId);
        Assert.Equal("WA-RETRY", depois.WaMessageId);
        Assert.NotNull(depois.EnviadaEm);

        // E o arquivo nao foi gravado de novo: o reenvio LE o que ja estava la.
        Assert.Equal(1, amb.Armazenamento.Gravacoes);
    }

    [Fact]
    public async Task Reenviar_o_que_JA_FOI_enviado_e_recusado()
    {
        // Sem isto, um duplo clique em "tentar de novo" mandaria a mesma imagem duas vezes.
        var (db, tx, amb) = await PrepararAsync("mid-reenvio-2x");
        using var _ = db; using var __ = tx;

        amb.Cliente.IdParaDevolver = "WA-OK";
        var r = await amb.Conversas.EnviarMidiaAsync(
            amb.Conversa.Id, new ArquivoParaEnvio(Jpeg(), "f.jpg", "image/jpeg"), null, default);

        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversas.ReenviarAsync(r.MensagemId, default));
    }

    [Fact]
    public async Task O_nome_do_arquivo_e_higienizado_e_a_extensao_segue_os_BYTES()
    {
        var (db, tx, amb) = await PrepararAsync("mid-nome");
        using var _ = db; using var __ = tx;

        // Caminho no nome, e extensao que nao bate com o conteudo (e um JPEG).
        var r = await amb.Conversas.EnviarMidiaAsync(
            amb.Conversa.Id,
            new ArquivoParaEnvio(Jpeg(), @"..\..\etc\passwd.png", "image/png"), null, default);

        db.ChangeTracker.Clear();
        var m = await db.Mensagens.AsNoTracking().SingleAsync(x => x.Id == r.MensagemId);

        Assert.Equal("passwd.jpg", m.MidiaNome);        // sem caminho, extensao do CONTEUDO
        Assert.Equal("image/jpeg", m.MidiaMime);        // o mime declarado foi ignorado
    }

    // ============================================================ bloco 13 · nota de voz
    /// <summary>Um WebM/Opus valido e curto, como o Chrome grava. Montado a mao pela mesma razao
    /// do AudioOpusTests: binario no repositorio nao se versiona, e navegador nao roda aqui.</summary>
    private static byte[] WebmDeVoz(int pacotes)
    {
        // Tamanho EBML com a LARGURA CERTA. A primeira versão parava em 2 bytes (16383), e o
        // fixture de 5 minutos tem cluster de ~700 KB: o tamanho saía truncado, o remux recusava
        // o arquivo, e o teste acusava "formato não suportado" em vez do limite de duração.
        static byte[] Tam(int n)
        {
            for (var largura = 1; largura <= 5; largura++)
            {
                if (n >= (1 << (7 * largura)) - 1) continue;

                var bytes = new byte[largura];
                var v = (long)n;
                for (var k = largura - 1; k >= 0; k--) { bytes[k] = (byte)(v & 0xFF); v >>= 8; }
                bytes[0] |= (byte)(0x80 >> (largura - 1));
                return bytes;
            }
            throw new ArgumentOutOfRangeException(nameof(n));
        }
        static byte[] El(byte[] id, byte[] c) => [.. id, .. Tam(c.Length), .. c];

        byte[] head = [.. "OpusHead"u8, 1, 1, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];
        var tracks = El([0x16, 0x54, 0xAE, 0x6B], El([0xAE], El([0x63, 0xA2], head)));

        var blocos = new List<byte>();
        for (var i = 0; i < pacotes; i++)
        {
            byte[] pacote = [(byte)((19 << 3) | 0), .. new byte[40]];   // 1 quadro de 20 ms
            blocos.AddRange(El([0xA3], [(byte)0x81, 0x00, 0x00, 0x80, .. pacote]));
        }

        var segment = El([0x18, 0x53, 0x80, 0x67],
            [.. tracks, .. El([0x1F, 0x43, 0xB6, 0x75], [.. blocos])]);

        return [0x1A, 0x45, 0xDF, 0xA3, 0x81, 0x00, .. segment];
    }

    [Fact]
    public async Task AUDIO_SAI_COMO_OGG_OPUS_e_nao_como_o_WEBM_que_o_navegador_gravou()
    {
        // ⚠️ O SINTOMA DE ERRAR AQUI NÃO É UM ERRO: o WhatsApp entrega WebM como ARQUIVO ANEXO
        // em vez de nota de voz. Chega, e está errado — e ninguém abre chamado por isso.
        var (db, tx, amb) = await PrepararAsync("voz-formato");
        using var _ = db; using var __ = tx;

        amb.Cliente.IdParaDevolver = "WA-VOZ";

        var r = await amb.Conversas.EnviarAudioAsync(
            amb.Conversa.Id,
            new ArquivoParaEnvio(WebmDeVoz(100), "gravacao.webm", "audio/webm;codecs=opus"),
            default);

        Assert.True(r.Enviada);

        // ===================== A ROTA IMPORTA TANTO QUANTO O FORMATO =====================
        // `sendMedia` com mediatype=audio entrega o arquivo como ANEXO comum; nota de voz sai por
        // `sendWhatsAppAudio`. As DUAS devolvem 2xx — a diferença só aparece no celular de quem
        // recebe, e foi assim que o defeito passou: a linha ficava `enviada`, sem erro, e nada
        // chegava ao cliente.
        // ================================================================================
        Assert.Empty(amb.Cliente.MidiasEnviadas);
        var enviado = Assert.Single(amb.Cliente.AudiosEnviados);

        // E o conteúdo é OGG, não o WebM que o navegador gravou.
        Assert.StartsWith("OggS", System.Text.Encoding.ASCII.GetString(
            Convert.FromBase64String(enviado.Base64)[..4]));

        db.ChangeTracker.Clear();
        var m = await db.Mensagens.AsNoTracking().SingleAsync(x => x.Id == r.MensagemId);

        Assert.Equal(TipoMidia.Audio, m.TipoMidia);
        Assert.Equal("audio/ogg", m.MidiaMime);
        Assert.EndsWith(".ogg", m.MidiaNome);
        // 100 pacotes de 20 ms = 2s, menos o pre-skip do fixture.
        Assert.Equal(2, m.MidiaDuracaoSegundos);
        // Nota de voz não tem legenda: texto junto viraria uma segunda mensagem no WhatsApp.
        Assert.Null(m.Texto);
    }

    [Fact]
    public async Task Formato_que_nao_da_para_converter_e_recusado_com_mensagem_util()
    {
        // MP4/AAC do Safari. Sem FFmpeg não há conversão — e mandar assim viraria anexo.
        var (db, tx, amb) = await PrepararAsync("voz-mp4");
        using var _ = db; using var __ = tx;

        byte[] mp4 = [0x00, 0x00, 0x00, 0x18, .. "ftypmp42"u8, .. new byte[64]];

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversas.EnviarAudioAsync(
                amb.Conversa.Id, new ArquivoParaEnvio(mp4, "a.m4a", "audio/mp4"), default));

        Assert.Contains("Chrome", erro.Message);   // diz O QUE FAZER, não só "não suportado"
        Assert.Equal(0, amb.Armazenamento.Gravacoes);
        Assert.Empty(amb.Cliente.AudiosEnviados);
    }

    [Fact]
    public async Task AUDIO_ACIMA_DE_CINCO_MINUTOS_E_RECUSADO_antes_de_ir_para_o_disco()
    {
        var (db, tx, amb) = await PrepararAsync("voz-longa");
        using var _ = db; using var __ = tx;

        // 15.100 pacotes de 20 ms = 302 s, dois segundos acima do teto.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversas.EnviarAudioAsync(
                amb.Conversa.Id,
                new ArquivoParaEnvio(WebmDeVoz(15_100), "longa.webm", "audio/webm"), default));

        Assert.Contains("limite", erro.Message);
        Assert.Equal(0, amb.Armazenamento.Gravacoes);
    }

    [Fact]
    public async Task Falha_no_envio_do_audio_mantem_a_linha_com_erro_sem_duplicar()
    {
        var (db, tx, amb) = await PrepararAsync("voz-falha");
        using var _ = db; using var __ = tx;

        amb.Cliente.ErroParaLancar = new IntegracaoWhatsAppException("Evolution fora do ar");

        var r = await amb.Conversas.EnviarAudioAsync(
            amb.Conversa.Id, new ArquivoParaEnvio(WebmDeVoz(60), "v.webm", "audio/webm"), default);

        Assert.False(r.Enviada);

        db.ChangeTracker.Clear();
        var m = await db.Mensagens.AsNoTracking().SingleAsync(x => x.Id == r.MensagemId);
        Assert.NotNull(m.Erro);
        Assert.Null(m.EnviadaEm);
        Assert.Equal(TipoMidia.Audio, m.TipoMidia);

        Assert.Equal(1, await db.Mensagens.CountAsync(
            x => x.ConversaId == amb.Conversa.Id && x.Direcao == DirecaoMensagem.Saida));
    }

    [Fact]
    public async Task A_previa_da_caixa_diz_que_foi_audio_e_quanto_durou()
    {
        // Prévia em branco faria a linha da caixa parecer que nada foi enviado.
        var (db, tx, amb) = await PrepararAsync("voz-previa");
        using var _ = db; using var __ = tx;

        await amb.Conversas.EnviarAudioAsync(
            amb.Conversa.Id, new ArquivoParaEnvio(WebmDeVoz(150), "v.webm", "audio/webm"), default);

        db.ChangeTracker.Clear();
        var c = await db.Conversas.AsNoTracking().SingleAsync(x => x.Id == amb.Conversa.Id);

        Assert.Contains("Áudio", c.UltimaMensagemPrevia);
        Assert.Contains("3s", c.UltimaMensagemPrevia);
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, Contato Contato, Conversa Conversa, ContextoMutavel Contexto,
        ClienteWhatsAppFalso Cliente, EnviadorMensagem Enviador,
        IServicoConversas Conversas, IServicoConexoes Conexoes,
        ArmazenamentoFalso Armazenamento);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);

        // O envio roda AUTENTICADO (o vendedor na caixa de entrada).
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var cliente = new ClienteWhatsAppFalso();
        var opcoes = new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero };
        var enviador = new EnviadorMensagem(
            new DadosMensagem(db, TimeProvider.System), cliente, opcoes, TimeProvider.System,
            NullLogger<EnviadorMensagem>.Instance);

        var armazenamento = new ArmazenamentoFalso();
        var conversas = new ServicoConversas(db, ctx, enviador, armazenamento, new ColetorAuditoria(), TimeProvider.System);
        var conexoes = new ServicoConexoes(db, cliente, ctx, TimeProvider.System);

        return (db, tx, new Ambiente(
            cenario, cenario.Contato, cenario.Conversa, ctx, cliente, enviador, conversas, conexoes,
            armazenamento));
    }

    private static Mensagem NovaReserva(Ambiente amb, Lembrete lembrete, string texto) => new()
    {
        EmpresaId = amb.Cenario.Id,
        ConversaId = amb.Conversa.Id,
        ContatoId = amb.Contato.Id,
        ConexaoId = amb.Cenario.Conexao.Id,
        InstanceName = amb.Cenario.Conexao.InstanceName,
        Direcao = DirecaoMensagem.Saida,
        Texto = texto,
        LembreteId = lembrete.Id,
        DataDisparo = DateOnly.FromDateTime(DateTime.UtcNow)
    };

    private static async Task<Lembrete> CriarLembreteAsync(
        NexoraDbContext db, Ambiente amb, string titulo, DateOnly? data = null)
    {
        var lembrete = new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Contato.Id,
            ConversaId = amb.Conversa.Id,
            Origem = OrigemLembrete.Automatico,
            DataAlvo = data ?? DateOnly.FromDateTime(DateTime.UtcNow),
            Titulo = titulo,
            EnviaMensagem = true,
            TextoMensagem = "conteudo do follow-up"
        };
        db.Lembretes.Add(lembrete);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return lembrete;
    }

    private static async Task<Mensagem> MensagemAsync(NexoraDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return await db.Mensagens.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private static async Task<Conversa> ConversaAsync(NexoraDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return await db.Conversas.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == id);
    }
}
