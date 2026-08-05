using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Entidades;
using Nexora.Core.FollowUp;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>A rodada de follow-up contra Postgres real.
///
/// A ELEGIBILIDADE é a parte que não veio do Recupera — lá é vencimento de dívida, aqui é
/// inatividade da conversa. Ela vive inteira no SQL, então testar em memória não provaria nada:
/// índice parcial, teto diário e o filtro por direção da última mensagem só existem no banco.</summary>
[Collection("banco")]
public class FollowUpDbTests(BancoTeste banco)
{
    // Quinta-feira, 10h30 da manhã em Brasília — dentro da janela padrão (8h-20h, seg-sáb).
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ============================================================ a regra de elegibilidade
    [Fact]
    public async Task Conversa_parada_com_ultima_mensagem_de_SAIDA_gera_lembrete()
    {
        var (db, tx, amb) = await PrepararAsync("elegivel");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);

        Assert.Equal(OrigemLembrete.Automatico, lembrete.Origem);
        Assert.True(lembrete.EnviaMensagem);
        Assert.Equal(amb.Cenario.Dono.Id, lembrete.ResponsavelId);   // herda o dono da conversa
        Assert.Contains(amb.Contato.Nome, lembrete.Titulo);
    }

    [Fact]
    public async Task Conversa_cuja_ultima_mensagem_foi_de_ENTRADA_nao_gera_lembrete()
    {
        // A CONDIÇÃO MAIS IMPORTANTE DA ELEGIBILIDADE. Se a última foi de entrada, o CLIENTE
        // está esperando resposta — isso é o semáforo, não follow-up. Sem esta condição o sistema
        // cobra o vendedor duas vezes pela mesma coisa: no vermelho da caixa e no Meu Dia.
        var (db, tx, amb) = await PrepararAsync("entrada");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Entrada, diasAtras: 10);

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(0, r.Gerados);
        db.ChangeTracker.Clear();
        Assert.False(await db.Lembretes.IgnoreQueryFilters().AnyAsync(l => l.ContatoId == amb.Contato.Id));
    }

    [Fact]
    public async Task Conversa_parada_ha_MENOS_dias_que_o_configurado_nao_gera()
    {
        var (db, tx, amb) = await PrepararAsync("recente");
        using var _ = db; using var __ = tx;

        // DiasSemRespostaFollowUp padrão = 2. Um dia parado ainda não vale.
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 1);

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    [Fact]
    public async Task Contato_em_etapa_terminal_nao_gera_lembrete()
    {
        // Ganho ou perdido não se persegue. Mandar follow-up para quem já comprou é o tipo de
        // erro que o cliente percebe antes da gente.
        var (db, tx, amb) = await PrepararAsync("ganho");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == amb.Contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.GanhoEm, DateTime.UtcNow.AddDays(-3)));
        db.ChangeTracker.Clear();

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    [Fact]
    public async Task Contato_perdido_nao_gera_lembrete()
    {
        var (db, tx, amb) = await PrepararAsync("perdido");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == amb.Contato.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.PerdidoEm, DateTime.UtcNow.AddDays(-3))
                .SetProperty(c => c.MotivoPerda, "comprou do concorrente"));
        db.ChangeTracker.Clear();

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    [Fact]
    public async Task Conversa_resolvida_nao_gera_lembrete()
    {
        var (db, tx, amb) = await PrepararAsync("resolvida");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, StatusConversa.Resolvida)
                .SetProperty(c => c.ResolvidoEm, DateTime.UtcNow));
        db.ChangeTracker.Clear();

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    [Fact]
    public async Task Contato_com_lembrete_pendente_nao_ganha_outro()
    {
        // Senão o vendedor recebe a mesma tarefa todo dia até fazer — e para de olhar a lista.
        var (db, tx, amb) = await PrepararAsync("ja-tem");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Contato.Id,
            Origem = OrigemLembrete.Manual,
            Status = StatusLembrete.Pendente,
            DataAlvo = DateOnly.FromDateTime(QuintaDeManha.UtcDateTime).AddDays(3),
            Titulo = "ligar depois"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    // ============================================================ o teto diário anti-spam
    [Fact]
    public async Task Segundo_automatico_no_mesmo_dia_e_BARRADO_pelo_banco_sem_excecao()
    {
        // O motor roda duas vezes (restart, ou duas instâncias sem lock distribuído). O
        // uq_lembrete_teto_diario barra o segundo, e o INSERT ... ON CONFLICT DO NOTHING traduz
        // isso em "barrado" — não em exceção. Se virasse exceção, o `catch` por empresa engoliria
        // a rodada INTEIRA daquele tenant.
        var (db, tx, amb) = await PrepararAsync("teto-motor");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        var primeira = await amb.Motor.ExecutarAsync();
        Assert.Equal(1, primeira.Gerados);

        // O lembrete da primeira rodada já foi concluído (a mensagem saiu), então a conversa
        // volta a ser elegível — e é exatamente aí que o teto tem que segurar.
        var segunda = await amb.Motor.ExecutarAsync();

        Assert.Equal(0, segunda.Gerados);
        Assert.Equal(1, segunda.Barrados);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Lembretes.IgnoreQueryFilters()
            .CountAsync(l => l.ContatoId == amb.Contato.Id && l.Origem == OrigemLembrete.Automatico));
    }

    // ============================================================ reserve-defer
    [Fact]
    public async Task Rodada_FORA_da_janela_reserva_sem_postar()
    {
        // 23h de quinta. O lembrete nasce e a mensagem é RESERVADA com a data do próximo dia
        // permitido — mas nada é postado. Postar aqui é acordar cliente de madrugada.
        var (db, tx, amb) = await PrepararAsync(
            "fora-janela", new DateTimeOffset(2026, 8, 7, 2, 0, 0, TimeSpan.Zero));   // 23h BRT de 06/08
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5,
            agora: new DateTime(2026, 8, 7, 2, 0, 0, DateTimeKind.Utc));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);
        Assert.Equal(0, r.Enviados);
        Assert.Equal(1, r.Adiados);
        Assert.Empty(amb.Cliente.TextosEnviados);   // A EVOLUTION NÃO FOI CHAMADA

        db.ChangeTracker.Clear();
        var linha = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida);

        Assert.Null(linha.EnviadaEm);
        Assert.Null(linha.Erro);                      // não é falha: nem chegou a tentar
        Assert.Equal((short)0, linha.Tentativas);
        // A reserva carimba HOJE: 06/08 é quinta, um dia em que a empresa ATENDE — o que fechou
        // a janela foi a HORA, não o dia. O deslize só muda a data quando o próprio dia está
        // bloqueado (fim de semana ou feriado); ver os testes de feriado abaixo.
        Assert.Equal(new DateOnly(2026, 8, 6), linha.DataDisparo);

        // E o lembrete continua PENDENTE — não foi concluído, porque a mensagem não saiu.
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);
        Assert.Equal(StatusLembrete.Pendente, lembrete.Status);
    }

    [Fact]
    public async Task Conexao_caida_reserva_sem_postar_e_a_rodada_seguinte_drena()
    {
        var (db, tx, amb) = await PrepararAsync("conexao-caida");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        amb.Cliente.EstadoParaDevolver = "close";

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);
        Assert.Equal(1, r.Adiados);
        Assert.Empty(amb.Cliente.TextosEnviados);

        // A Evolution voltou: a drenagem reaproveita a MESMA linha, sem criar outra.
        amb.Cliente.EstadoParaDevolver = "open";
        var segunda = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, segunda.Enviados);
        Assert.Single(amb.Cliente.TextosEnviados);

        db.ChangeTracker.Clear();
        var linhas = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida)
            .ToListAsync();

        Assert.Single(linhas);                  // UMA linha, não duas
        Assert.NotNull(linhas[0].EnviadaEm);
    }

    // ============================================================ isolamento de falha
    [Fact]
    public async Task Excecao_numa_empresa_nao_interrompe_as_outras()
    {
        // Sem o catch por empresa, a primeira exceção interrompe o laço e NINGUÉM depois dela
        // recebe follow-up — em silêncio, porque o job segue "de pé".
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var a = await Semeador.TenantAsync(db, "quebra-a");
        var b = await Semeador.TenantAsync(db, "quebra-b");

        // Só as duas empresas deste teste participam da rodada (a transação isola as demais,
        // mas empresas de outros testes commitados não existem — cada teste faz rollback).
        await DesativarOutrasAsync(db, a.Id, b.Id);

        await PararConversaAsync(db, a.Conversa.Id, a.Contato.Id, DirecaoMensagem.Saida, 5, QuintaDeManha.UtcDateTime);
        await PararConversaAsync(db, b.Conversa.Id, b.Contato.Id, DirecaoMensagem.Saida, 5, QuintaDeManha.UtcDateTime);

        var relogio = new RelogioFalso(QuintaDeManha);
        var cliente = new ClienteWhatsAppFalso();

        // A instância da empresa A explode ao consultar o estado — dado ruim, cenário real.
        var clienteQueQuebraNaA = new ClienteQuebraNaInstancia(cliente, a.Conexao.InstanceName);
        var motor = MontarMotor(db, ctx, clienteQueQuebraNaA, relogio);

        var r = await motor.ExecutarAsync();

        // A empresa B foi atendida apesar da explosão na A.
        db.ChangeTracker.Clear();
        Assert.False(await db.Lembretes.IgnoreQueryFilters().AnyAsync(l => l.EmpresaId == a.Id));
        Assert.True(await db.Lembretes.IgnoreQueryFilters().AnyAsync(l => l.EmpresaId == b.Id));
        Assert.Equal(1, r.Gerados);
    }

    [Fact]
    public async Task Empresa_sem_conexao_e_pulada_sem_derrubar_a_rodada()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var semNumero = new Empresa { Nome = "Ainda nao pareou" };
        db.Empresas.Add(semNumero);
        await db.SaveChangesAsync();

        var comNumero = await Semeador.TenantAsync(db, "com-numero");
        await DesativarOutrasAsync(db, semNumero.Id, comNumero.Id);
        await PararConversaAsync(db, comNumero.Conversa.Id, comNumero.Contato.Id,
            DirecaoMensagem.Saida, 5, QuintaDeManha.UtcDateTime);

        var motor = MontarMotor(db, ctx, new ClienteWhatsAppFalso(), new RelogioFalso(QuintaDeManha));

        Assert.Equal(1, (await motor.ExecutarAsync()).Gerados);
    }

    // ============================================================ o envio
    [Fact]
    public async Task Rodada_dentro_da_janela_gera_envia_e_conclui_o_lembrete()
    {
        var (db, tx, amb) = await PrepararAsync("caminho-feliz");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);
        Assert.Equal(1, r.Enviados);
        Assert.Equal(0, r.Falhas);

        var enviada = Assert.Single(amb.Cliente.TextosEnviados);
        Assert.Equal(amb.Cenario.Conexao.InstanceName, enviada.Instancia);
        Assert.Equal(amb.Contato.Telefone, enviada.Telefone);
        // O texto usa o PRIMEIRO nome, não o nome inteiro do cadastro.
        Assert.Contains(amb.Contato.Nome.Split(' ')[0], enviada.Texto);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);
        Assert.Equal(StatusLembrete.Concluido, lembrete.Status);
        Assert.NotNull(lembrete.ConcluidoEm);

        var mensagem = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.LembreteId == lembrete.Id);
        Assert.NotNull(mensagem.EnviadaEm);
    }

    [Fact]
    public async Task Falha_na_Evolution_nao_conclui_o_lembrete_e_a_linha_fica_com_o_erro()
    {
        var (db, tx, amb) = await PrepararAsync("falha-envio");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        amb.Cliente.ErroParaLancar = new IntegracaoWhatsAppException("Evolution caiu no meio");

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);
        Assert.Equal(1, r.Falhas);
        Assert.Equal(0, r.Enviados);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);
        Assert.Equal(StatusLembrete.Pendente, lembrete.Status);   // NÃO concluiu

        // A linha FICA, com o erro. Apagar liberaria o dedupe e um POST que chegou mas deu
        // timeout viraria mensagem duplicada.
        var mensagem = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.LembreteId == lembrete.Id);
        Assert.Null(mensagem.EnviadaEm);
        Assert.Contains("caiu no meio", mensagem.Erro!);
    }

    // ============================================================ feriado
    [Fact]
    public async Task Feriado_fecha_a_janela_em_pleno_horario_comercial()
    {
        // 10h30 de uma quinta — hora comercial. Mas a empresa marcou o dia como ponto
        // facultativo, e nada é postado. Se a janela olhasse só a hora e o bitmask, o cliente
        // receberia follow-up no feriado.
        var (db, tx, amb) = await PrepararAsync("feriado-hoje");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await MarcarFeriadosAsync(db, amb.Cenario.Id, new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 7));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);
        Assert.Equal(0, r.Enviados);
        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    [Fact]
    public async Task Feriado_desliza_a_data_alvo_do_lembrete_gerado()
    {
        // 06/08/2026 é quinta. Com quinta E sexta em ponto facultativo, o follow-up nasce
        // marcado para SÁBADO — a janela padrão inclui sábado. E NÃO é reservado agora: ele
        // ainda não venceu, então nada de mensagem no banco.
        var (db, tx, amb) = await PrepararAsync("feriado-desliza");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await MarcarFeriadosAsync(db, amb.Cenario.Id, new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 7));

        await amb.Motor.ExecutarAsync();

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);

        Assert.Equal(new DateOnly(2026, 8, 8), lembrete.DataAlvo);   // sábado
        Assert.Equal(StatusLembrete.Pendente, lembrete.Status);
        Assert.False(await db.Mensagens.IgnoreQueryFilters()
            .AnyAsync(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida));
    }

    [Fact]
    public async Task Lembrete_ja_vencido_e_reservado_com_a_data_do_proximo_dia_aberto()
    {
        // O RESERVE-DEFER de verdade: um lembrete que já venceu (data-alvo ontem) numa rodada em
        // dia fechado. A linha é reservada carimbando o próximo dia ABERTO, sem postar — assim a
        // data-alvo é preservada, o envio não se perde e nada é duplicado.
        var (db, tx, amb) = await PrepararAsync("defer");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Entrada, diasAtras: 1);   // não gera novo
        await MarcarFeriadosAsync(db, amb.Cenario.Id, new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 7));

        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Contato.Id,
            ConversaId = amb.Conversa.Id,
            Origem = OrigemLembrete.Automatico,
            Status = StatusLembrete.Pendente,
            DataAlvo = new DateOnly(2026, 8, 5),        // venceu ontem
            Titulo = "retomar",
            EnviaMensagem = true,
            TextoMensagem = "ainda tem interesse?"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Adiados);
        Assert.Empty(amb.Cliente.TextosEnviados);

        var linha = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida);

        Assert.Equal(new DateOnly(2026, 8, 8), linha.DataDisparo);   // sábado, o próximo aberto
        Assert.Null(linha.EnviadaEm);
    }

    [Fact]
    public async Task Feriado_de_uma_empresa_nao_vale_para_outra()
    {
        // O feriado manual é do tenant. O global (empresa_id NULL) é de todos — e é o único que
        // o seed nacional cria.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var a = await Semeador.TenantAsync(db, "fer-a");
        var b = await Semeador.TenantAsync(db, "fer-b");

        db.Feriados.Add(new Feriado
        {
            EmpresaId = a.Id, Data = new DateOnly(2026, 8, 6),
            Nome = "Aniversário da cidade", Abrangencia = AbrangenciaFeriado.Manual
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var dados = new DadosFollowUp(db, new RelogioFalso(QuintaDeManha));

        var deA = await dados.FeriadosAsync(a.Id, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), default);
        var deB = await dados.FeriadosAsync(b.Id, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), default);

        Assert.Contains(new DateOnly(2026, 8, 6), deA);
        Assert.DoesNotContain(new DateOnly(2026, 8, 6), deB);
    }

    // ============================================================ apoio
    private sealed record Ambiente(
        Cenario Cenario, Contato Contato, Conversa Conversa, ContextoMutavel Contexto,
        ClienteWhatsAppFalso Cliente, MotorFollowUp Motor, RelogioFalso Relogio);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo, DateTimeOffset? quando = null)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);
        await DesativarOutrasAsync(db, cenario.Id);

        var relogio = new RelogioFalso(quando ?? QuintaDeManha);
        var cliente = new ClienteWhatsAppFalso();

        // A rodada roda SEM tenant no contexto (é job) — de propósito: é o que prova que o
        // IgnoreQueryFilters + filtro explícito do DadosFollowUp está no lugar.
        return (db, tx, new Ambiente(
            cenario, cenario.Contato, cenario.Conversa, ctx, cliente,
            MontarMotor(db, ctx, cliente, relogio), relogio));
    }

    private static MotorFollowUp MontarMotor(
        NexoraDbContext db, ContextoMutavel ctx, IClienteWhatsApp cliente, TimeProvider relogio)
    {
        var enviador = new EnviadorMensagem(
            new DadosMensagem(db, relogio), cliente,
            new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero },
            relogio, NullLogger<EnviadorMensagem>.Instance);

        return new MotorFollowUp(
            new DadosFollowUp(db, relogio), enviador, relogio, NullLogger<MotorFollowUp>.Instance);
    }

    /// <summary>Deixa ATIVAS só as empresas do teste. A transação isola as linhas dos outros
    /// testes, mas a rodada varre `empresas` inteira — sem isto, um teste que rode em paralelo
    /// com dados commitados de desenvolvimento veria empresas alheias.</summary>
    private static async Task DesativarOutrasAsync(NexoraDbContext db, params long[] manter)
    {
        await db.Empresas.IgnoreQueryFilters()
            .Where(e => !manter.Contains(e.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Ativo, false));
        db.ChangeTracker.Clear();
    }

    private static async Task MarcarFeriadosAsync(NexoraDbContext db, long empresaId, params DateOnly[] datas)
    {
        db.Feriados.AddRange(datas.Select(d => new Feriado
        {
            EmpresaId = empresaId, Data = d,
            Nome = "Ponto facultativo", Abrangencia = AbrangenciaFeriado.Manual
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private Task PararConversaAsync(
        NexoraDbContext db, Ambiente amb, DirecaoMensagem direcao, int diasAtras, DateTime? agora = null) =>
        PararConversaAsync(db, amb.Conversa.Id, amb.Contato.Id, direcao, diasAtras,
            agora ?? QuintaDeManha.UtcDateTime);

    /// <summary>Deixa a conversa "parada há N dias" com a última mensagem na direção pedida.
    /// `aguardando_desde` acompanha a direção: entrada deixa o cliente esperando, saída não.</summary>
    private static async Task PararConversaAsync(
        NexoraDbContext db, long conversaId, long contatoId,
        DirecaoMensagem direcao, int diasAtras, DateTime agora)
    {
        var quando = agora.AddDays(-diasAtras);

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == conversaId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UltimaMensagemEm, quando)
                .SetProperty(c => c.UltimaMensagemDirecao, direcao)
                .SetProperty(c => c.UltimaMensagemPrevia, "última mensagem")
                .SetProperty(c => c.AguardandoDesde,
                    direcao == DirecaoMensagem.Entrada ? quando : (DateTime?)null));

        db.ChangeTracker.Clear();
    }

    /// <summary>Decorador que explode ao consultar UMA instância específica. Simula o dado ruim
    /// de uma empresa sem contaminar as outras.</summary>
    private sealed class ClienteQuebraNaInstancia(IClienteWhatsApp real, string instanciaRuim) : IClienteWhatsApp
    {
        public Task<string> StatusInstanciaAsync(string instanceName, CancellationToken ct) =>
            instanceName == instanciaRuim
                ? throw new InvalidOperationException("instância corrompida")
                : real.StatusInstanciaAsync(instanceName, ct);

        public Task<string> EnviarTextoAsync(string i, string t, string x, CancellationToken ct) =>
            real.EnviarTextoAsync(i, t, x, ct);

        public Task<string> EnviarMidiaAsync(string i, string t, string b, string mt, string mi, string f, string? l, CancellationToken ct) =>
            real.EnviarMidiaAsync(i, t, b, mt, mi, f, l, ct);

        public Task<MidiaRecebida?> ObterMidiaAsync(string i, string w, CancellationToken ct) =>
            real.ObterMidiaAsync(i, w, ct);

        public Task<RespostaQr> ConectarInstanciaAsync(string i, string? n, CancellationToken ct) =>
            real.ConectarInstanciaAsync(i, n, ct);

        public Task<DetalhesInstancia?> ObterDetalhesInstanciaAsync(string i, CancellationToken ct) =>
            real.ObterDetalhesInstanciaAsync(i, ct);

        public Task DesconectarInstanciaAsync(string i, CancellationToken ct) =>
            real.DesconectarInstanciaAsync(i, ct);
    }
}
