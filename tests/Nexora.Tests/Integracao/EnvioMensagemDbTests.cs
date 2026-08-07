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

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, Contato Contato, Conversa Conversa, ContextoMutavel Contexto,
        ClienteWhatsAppFalso Cliente, EnviadorMensagem Enviador,
        IServicoConversas Conversas, IServicoConexoes Conexoes);

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

        var conversas = new ServicoConversas(db, ctx, enviador, new ColetorAuditoria(), TimeProvider.System);
        var conexoes = new ServicoConexoes(db, cliente, ctx, TimeProvider.System);

        return (db, tx, new Ambiente(
            cenario, cenario.Contato, cenario.Conversa, ctx, cliente, enviador, conversas, conexoes));
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
