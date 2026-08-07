using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O Meu Dia, o dashboard e os lembretes manuais — as três leituras da camada de tempo.
///
/// SEM TABELA NOVA: o Meu Dia é uma consulta sobre conversas e lembretes que já existem. Uma
/// tabela "tarefas do dia" precisaria ser mantida em sincronia com as duas fontes, e a primeira
/// divergência apareceria como tarefa fantasma na tela do vendedor.</summary>
[Collection("banco")]
public class MeuDiaDbTests(BancoTeste banco)
{
    // Quinta, 10h30 em Brasília — dentro da janela padrão.
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Hoje = new(2026, 8, 6);

    // ============================================================ Meu Dia
    [Fact]
    public async Task Traz_conversas_esperando_resposta_e_lembretes_vencidos()
    {
        var (db, tx, amb) = await PrepararAsync("meu-dia");
        using var _ = db; using var __ = tx;

        await AguardandoDesdeAsync(db, amb.Conversa.Id, QuintaDeManha.UtcDateTime.AddHours(-2));
        await CriarLembreteAsync(db, amb, "Ligar para o cliente", Hoje, amb.Cenario.Dono.Id);

        var dia = await amb.MeuDia.MeuDiaAsync(default);

        Assert.Equal(1, dia.Respondendo);
        Assert.Equal(1, dia.Lembretes);
        Assert.Equal(2, dia.Acoes.Count);

        // MINÚSCULAS: é o que o cliente compara. O DTO envia string, como todo enum do sistema.
        var responder = dia.Acoes.Single(a => a.Tipo == "responder");
        Assert.Equal(amb.Conversa.Id, responder.ConversaId);
        Assert.Equal(amb.Contato.Nome, responder.ContatoNome);
        Assert.NotNull(responder.AguardandoDesde);   // o timestamp vai junto: o cliente PINTA
        Assert.Equal(120, responder.MinutosUteis);   // 2h, tudo dentro do expediente

        var lembrete = dia.Acoes.Single(a => a.Tipo == "lembrete");
        Assert.Equal("Ligar para o cliente", lembrete.Titulo);
        Assert.False(lembrete.Atrasado);
    }

    [Fact]
    public async Task RESPONDER_A_CONVERSA_TIRA_ELA_DO_MEU_DIA()
    {
        // A prova de que o Meu Dia é derivado, não uma lista mantida à mão: responder zera
        // `aguardando_desde` no caminho de envio, e a ação some sozinha. Nenhum código do Meu Dia
        // precisou saber que houve uma resposta.
        var (db, tx, amb) = await PrepararAsync("responder-some");
        using var _ = db; using var __ = tx;

        await AguardandoDesdeAsync(db, amb.Conversa.Id, QuintaDeManha.UtcDateTime.AddHours(-3));
        Assert.Single((await amb.MeuDia.MeuDiaAsync(default)).Acoes);

        await amb.Conversas.ResponderAsync(amb.Conversa.Id, "desculpe a demora!", default);

        db.ChangeTracker.Clear();
        Assert.Empty((await amb.MeuDia.MeuDiaAsync(default)).Acoes);
    }

    [Fact]
    public async Task Concluir_o_lembrete_tira_ele_do_Meu_Dia()
    {
        var (db, tx, amb) = await PrepararAsync("concluir-some");
        using var _ = db; using var __ = tx;

        var id = await CriarLembreteAsync(db, amb, "Enviar proposta", Hoje, amb.Cenario.Dono.Id);
        Assert.Single((await amb.MeuDia.MeuDiaAsync(default)).Acoes);

        await amb.Lembretes.ConcluirAsync(id, default);

        db.ChangeTracker.Clear();
        Assert.Empty((await amb.MeuDia.MeuDiaAsync(default)).Acoes);
    }

    [Fact]
    public async Task Traz_o_do_RESPONSAVEL_certo_e_os_sem_dono_mas_nao_os_de_outro_vendedor()
    {
        // Sem esta separação, o vendedor abre o Meu Dia e vê a agenda da equipe inteira — o que
        // é a mesma coisa que não ter Meu Dia.
        var (db, tx, amb) = await PrepararAsync("responsavel");
        using var _ = db; using var __ = tx;

        var outro = await OutroVendedorAsync(db, amb.Cenario.Id, "responsavel");

        await CriarLembreteAsync(db, amb, "meu", Hoje, amb.Cenario.Dono.Id);
        await CriarLembreteAsync(db, amb, "sem dono", Hoje, null, OutroContatoAsync);
        await CriarLembreteAsync(db, amb, "do outro", Hoje, outro.Id, OutroContatoAsync);

        var dia = await amb.MeuDia.MeuDiaAsync(default);

        Assert.Equal(2, dia.Lembretes);
        Assert.Contains(dia.Acoes, a => a.Titulo == "meu");
        Assert.Contains(dia.Acoes, a => a.Titulo == "sem dono");
        Assert.DoesNotContain(dia.Acoes, a => a.Titulo == "do outro");
    }

    [Fact]
    public async Task Lembrete_de_data_futura_nao_aparece_e_o_atrasado_aparece_marcado()
    {
        // `data_alvo <= hoje`, não `= hoje`: com igualdade estrita, um dia de folga do vendedor
        // faria a tarefa sumir da lista para sempre.
        var (db, tx, amb) = await PrepararAsync("atrasado");
        using var _ = db; using var __ = tx;

        await CriarLembreteAsync(db, amb, "de ontem", Hoje.AddDays(-1), amb.Cenario.Dono.Id);
        await CriarLembreteAsync(db, amb, "de amanhã", Hoje.AddDays(1), amb.Cenario.Dono.Id, OutroContatoAsync);

        var dia = await amb.MeuDia.MeuDiaAsync(default);

        var acao = Assert.Single(dia.Acoes);
        Assert.Equal("de ontem", acao.Titulo);
        Assert.True(acao.Atrasado);
    }

    [Fact]
    public async Task MINUTOS_UTEIS_DESCONTAM_AS_HORAS_FORA_DO_EXPEDIENTE()
    {
        // O mesmo desconto do semáforo, agora provado ponta a ponta contra o banco: a mensagem
        // chegou às 23h de ontem; às 10h30 de hoje são 2h30 de espera ÚTIL, não 11h30.
        var (db, tx, amb) = await PrepararAsync("minutos-uteis");
        using var _ = db; using var __ = tx;

        // 23h BRT de 05/08 = 02h UTC de 06/08.
        await AguardandoDesdeAsync(db, amb.Conversa.Id, new DateTime(2026, 8, 6, 2, 0, 0, DateTimeKind.Utc));

        var acao = Assert.Single((await amb.MeuDia.MeuDiaAsync(default)).Acoes);

        Assert.Equal(150, acao.MinutosUteis);    // 8h -> 10h30
        Assert.Equal(690, (int)(QuintaDeManha.UtcDateTime - new DateTime(2026, 8, 6, 2, 0, 0))
            .TotalMinutes);                       // o que seria SEM o desconto
    }

    [Fact]
    public async Task Feriado_no_meio_da_espera_tambem_e_descontado_no_Meu_Dia()
    {
        // O navegador não tem como saber que a terça foi feriado — por isso os minutos ÚTEIS
        // vêm calculados do servidor, junto do timestamp cru.
        var (db, tx, amb) = await PrepararAsync("feriado-meu-dia");
        using var _ = db; using var __ = tx;

        // 19h BRT de 04/08 (terça) = 22h UTC de 04/08. Quarta 05/08 é feriado.
        await AguardandoDesdeAsync(db, amb.Conversa.Id, new DateTime(2026, 8, 4, 22, 0, 0, DateTimeKind.Utc));
        db.Feriados.Add(new Feriado
        {
            EmpresaId = amb.Cenario.Id, Data = new DateOnly(2026, 8, 5),
            Nome = "Ponto facultativo", Abrangencia = AbrangenciaFeriado.Manual
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var acao = Assert.Single((await amb.MeuDia.MeuDiaAsync(default)).Acoes);

        // 1h de terça (19h->20h) + quarta inteira descontada + 2h30 de quinta (8h->10h30).
        Assert.Equal(60 + 150, acao.MinutosUteis);
    }

    [Fact]
    public async Task Conversa_de_outra_empresa_nao_vaza_para_o_Meu_Dia()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await Semeador.TenantAsync(db, "meudia-a");
        var alheia = await Semeador.TenantAsync(db, "meudia-b");

        await AguardandoDesdeAsync(db, minha.Conversa.Id, QuintaDeManha.UtcDateTime.AddHours(-1));
        await AguardandoDesdeAsync(db, alheia.Conversa.Id, QuintaDeManha.UtcDateTime.AddHours(-1));

        ctx.EmpresaId = minha.Id;
        ctx.UsuarioId = minha.Dono.Id;
        ctx.Papel = "dono";

        var servico = new ServicoMeuDia(db, ctx, new RelogioFalso(QuintaDeManha));
        var acao = Assert.Single((await servico.MeuDiaAsync(default)).Acoes);

        Assert.Equal(minha.Conversa.Id, acao.ConversaId);
    }

    // ============================================================ dashboard
    [Fact]
    public async Task Os_quatro_numeros_saem_do_banco_sem_agregacao_em_memoria()
    {
        var (db, tx, amb) = await PrepararAsync("dashboard");
        using var _ = db; using var __ = tx;

        // O Semeador já deixou 1 contato criado agora = 1 lead de hoje.
        await AguardandoDesdeAsync(db, amb.Conversa.Id, QuintaDeManha.UtcDateTime.AddHours(-1));
        await CriarLembreteAsync(db, amb, "follow-up de hoje", Hoje, amb.Cenario.Dono.Id);

        // Uma venda deste mês, de 1.500.
        var ganho = await OutroContatoAsync(db, amb, "ganho");
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == ganho)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.GanhoEm, QuintaDeManha.UtcDateTime.AddDays(-2))
                .SetProperty(c => c.Valor, 1500m));

        // O carimbo foi escrito direto, sem o `MarcarGanhoAsync`. Desde o NEG-1 o faturamento sai
        // de `vendas`; esta é a mesma reconciliação que os semeadores fazem.
        await ReconciliadorVendas.SincronizarAsync(db, default);

        // Uma perda deste mês — entra só na taxa de conversão.
        var perdido = await OutroContatoAsync(db, amb, "perdido");
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == perdido)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.PerdidoEm, QuintaDeManha.UtcDateTime.AddDays(-1))
                .SetProperty(c => c.MotivoPerda, "preço"));

        // Um lead do mês PASSADO: não conta como lead de hoje.
        var antigo = await OutroContatoAsync(db, amb, "antigo");
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == antigo)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CriadoEm, new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc)));

        db.ChangeTracker.Clear();
        var d = await amb.Dashboard.DashboardAsync(default);

        Assert.Equal(3, d.LeadsHoje);            // o do Semeador + ganho + perdido (antigo fora)
        Assert.Equal(1, d.AguardandoResposta);
        Assert.Equal(1, d.FollowUpsPendentes);
        Assert.Equal(1, d.VendasDoMes);
        Assert.Equal(1500m, d.FaturamentoDoMes);
        Assert.Equal(0.5, d.TaxaConversao);      // 1 ganho / (1 ganho + 1 perdido)
    }

    [Fact]
    public async Task Dashboard_de_empresa_vazia_devolve_zero_e_nao_estoura_no_SUM()
    {
        // SUM sobre conjunto vazio devolve NULL no SQL. Sem o `?? 0`, o mapeamento para decimal
        // não-anulável explodiria — e o dashboard de toda empresa recém-criada quebraria.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var empresa = new Empresa { Nome = "Recem-criada" };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();
        ctx.EmpresaId = empresa.Id;

        var d = await new ServicoDashboard(db, new RelogioFalso(QuintaDeManha)).DashboardAsync(default);

        Assert.Equal(0, d.LeadsHoje);
        Assert.Equal(0m, d.FaturamentoDoMes);
        Assert.Equal(0d, d.TaxaConversao);
        Assert.Empty(d.Funil);
    }

    [Fact]
    public async Task Funil_conta_por_etapa_e_ignora_perdidos()
    {
        var (db, tx, amb) = await PrepararAsync("funil");
        using var _ = db; using var __ = tx;

        var perdido = await OutroContatoAsync(db, amb, "fora-do-quadro");
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == perdido)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.PerdidoEm, QuintaDeManha.UtcDateTime)
                .SetProperty(c => c.MotivoPerda, "sumiu"));
        db.ChangeTracker.Clear();

        var d = await amb.Dashboard.DashboardAsync(default);

        var primeira = d.Funil.Single(e => e.EtapaId == amb.Cenario.PrimeiraEtapa.Id);
        Assert.Equal(1, primeira.Contatos);                     // só o do Semeador
        Assert.Equal(3, d.Funil.Count);
        Assert.Equal([1, 2, 3], d.Funil.Select(e => (int)e.Ordem));
    }

    [Fact]
    public async Task Status_do_painel_expoe_a_janela_e_as_faixas_da_EMPRESA()
    {
        // Sem a janela no payload, o navegador contaria a madrugada como espera e toda conversa
        // amanheceria vermelha. As faixas vêm da empresa, não de constante no código.
        var (db, tx, amb) = await PrepararAsync("status-janela");
        using var _ = db; using var __ = tx;

        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.JanelaHoraInicio, (short)9)
                .SetProperty(e => e.JanelaHoraFim, (short)18)
                .SetProperty(e => e.JanelaDiasSemana, (short)62)      // seg-sex
                .SetProperty(e => e.SemaforoAmareloMinutos, (short)30)
                .SetProperty(e => e.SemaforoVermelhoMinutos, (short)90));

        db.Feriados.Add(new Feriado
        {
            EmpresaId = amb.Cenario.Id, Data = new DateOnly(2026, 8, 3),
            Nome = "Ponto facultativo", Abrangencia = AbrangenciaFeriado.Manual
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var status = await new ServicoPainel(db, new RelogioFalso(QuintaDeManha)).StatusAsync(default);

        Assert.Equal((short)9, status.JanelaHoraInicio);
        Assert.Equal((short)18, status.JanelaHoraFim);
        Assert.Equal((short)62, status.JanelaDiasSemana);
        Assert.Equal((short)30, status.SemaforoAmareloMinutos);
        Assert.Equal((short)90, status.SemaforoVermelhoMinutos);
        Assert.Contains(new DateOnly(2026, 8, 3), status.FeriadosRecentes);
    }

    // ============================================================ lembretes manuais
    [Fact]
    public async Task Lembrete_manual_NAO_entra_no_teto_diario()
    {
        // O teto (uq_lembrete_teto_diario) é a defesa contra o ROBÔ cansar o cliente. Uma pessoa
        // marcando três tarefas para o mesmo contato sabe o que está fazendo — e o índice é
        // parcial justamente para não atrapalhá-la.
        var (db, tx, amb) = await PrepararAsync("manual-x3");
        using var _ = db; using var __ = tx;

        var novo = new NovoLembrete(amb.Contato.Id, Hoje, null, "ligar", null);
        await amb.Lembretes.CriarAsync(novo, default);
        await amb.Lembretes.CriarAsync(novo with { Titulo = "mandar catálogo" }, default);
        await amb.Lembretes.CriarAsync(novo with { Titulo = "visitar" }, default);

        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.Lembretes.IgnoreQueryFilters()
            .CountAsync(l => l.ContatoId == amb.Contato.Id && l.Origem == OrigemLembrete.Manual));
    }

    [Fact]
    public async Task Lembrete_manual_nasce_com_o_criador_como_responsavel()
    {
        var (db, tx, amb) = await PrepararAsync("manual-dono");
        using var _ = db; using var __ = tx;

        var id = await amb.Lembretes.CriarAsync(
            new NovoLembrete(amb.Contato.Id, Hoje, new TimeOnly(14, 30), "reunião", "levar orçamento"),
            default);

        db.ChangeTracker.Clear();
        var l = await db.Lembretes.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);

        Assert.Equal(amb.Cenario.Dono.Id, l.ResponsavelId);
        Assert.Equal(amb.Cenario.Dono.Id, l.CriadoPor);
        Assert.Equal(OrigemLembrete.Manual, l.Origem);
        Assert.False(l.EnviaMensagem);
        Assert.Equal(new TimeOnly(14, 30), l.HoraAlvo);
        Assert.Equal(amb.Conversa.Id, l.ConversaId);   // amarrado à conversa do contato
    }

    [Fact]
    public async Task Concluir_duas_vezes_devolve_conflito_em_vez_de_sobrescrever()
    {
        var (db, tx, amb) = await PrepararAsync("conflito");
        using var _ = db; using var __ = tx;

        var id = await amb.Lembretes.CriarAsync(
            new NovoLembrete(amb.Contato.Id, Hoje, null, "ligar", null), default);

        await amb.Lembretes.ConcluirAsync(id, default);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Lembretes.ConcluirAsync(id, default));
        Assert.True(erro.Conflito);   // vira 409 no FiltroRegraDeNegocio
    }

    [Fact]
    public async Task Lembrete_de_contato_de_outra_empresa_nao_e_criado()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await Semeador.TenantAsync(db, "lemb-a");
        var alheia = await Semeador.TenantAsync(db, "lemb-b");

        ctx.EmpresaId = minha.Id;
        ctx.UsuarioId = minha.Dono.Id;
        ctx.Papel = "dono";

        var servico = new ServicoLembretes(db, ctx, new RelogioFalso(QuintaDeManha));

        // O query filter esconde o contato alheio — o serviço enxerga "não encontrado", que é
        // exatamente o que um tenant deve ver de outro.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.CriarAsync(new NovoLembrete(alheia.Contato.Id, Hoje, null, "invasão", null), default));

        Assert.Contains("não encontrado", erro.Message);
    }

    [Fact]
    public async Task Lembrete_que_envia_mensagem_exige_o_texto()
    {
        var (db, tx, amb) = await PrepararAsync("sem-texto");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Lembretes.CriarAsync(
                new NovoLembrete(amb.Contato.Id, Hoje, null, "avisar", null, EnviaMensagem: true),
                default));

        Assert.Contains("mensagem", erro.Message);
        Assert.False(erro.Conflito);   // é entrada errada (400), não estado (409)
    }

    // ============================================================ feriados
    [Fact]
    public async Task Seed_anual_e_idempotente_e_cobre_o_ano_seguinte()
    {
        // Roda no boot E na rodada diária. Se não fosse idempotente, cada restart duplicaria os
        // feriados — e a virada de ano viraria um bug sazonal se o ano seguinte não entrasse.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var servico = new ServicoFeriados(db, ctx, new RelogioFalso(QuintaDeManha), Microsoft.Extensions.Logging.Abstractions.NullLogger<ServicoFeriados>.Instance);

        await servico.GarantirAtualEProximoAsync(default);
        var depoisDaPrimeira = await db.Feriados.IgnoreQueryFilters()
            .CountAsync(f => f.EmpresaId == null);

        await servico.GarantirAtualEProximoAsync(default);
        var depoisDaSegunda = await db.Feriados.IgnoreQueryFilters()
            .CountAsync(f => f.EmpresaId == null);

        Assert.Equal(depoisDaPrimeira, depoisDaSegunda);
        Assert.True(depoisDaPrimeira >= 26);   // 13 nacionais x 2 anos

        // O Natal de 2027 (ano seguinte) está lá.
        Assert.True(await db.Feriados.IgnoreQueryFilters()
            .AnyAsync(f => f.EmpresaId == null && f.Data == new DateOnly(2027, 12, 25)));
    }

    [Fact]
    public async Task Feriado_global_aparece_para_a_empresa_e_o_manual_de_outra_nao()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await Semeador.TenantAsync(db, "fer-view-a");
        var alheia = await Semeador.TenantAsync(db, "fer-view-b");

        db.Feriados.AddRange(
            new Feriado { EmpresaId = null, Data = new DateOnly(2026, 12, 25), Nome = "Natal", Abrangencia = AbrangenciaFeriado.Nacional },
            new Feriado { EmpresaId = alheia.Id, Data = new DateOnly(2026, 9, 30), Nome = "Aniversário da outra", Abrangencia = AbrangenciaFeriado.Manual });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        ctx.EmpresaId = minha.Id;
        var lista = await new ServicoFeriados(db, ctx, new RelogioFalso(QuintaDeManha), Microsoft.Extensions.Logging.Abstractions.NullLogger<ServicoFeriados>.Instance).ProximosAsync(default);

        Assert.Contains(lista, f => f.Nome == "Natal" && !f.EhManual);
        Assert.DoesNotContain(lista, f => f.Nome == "Aniversário da outra");
    }

    [Fact]
    public async Task Remover_feriado_global_nao_e_permitido()
    {
        // O nacional é compartilhado por TODOS os tenants — uma empresa apagando o Natal
        // apagaria o Natal de todo mundo.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await Semeador.TenantAsync(db, "fer-del");
        var global = new Feriado
        {
            EmpresaId = null, Data = new DateOnly(2026, 12, 25),
            Nome = "Natal", Abrangencia = AbrangenciaFeriado.Nacional
        };
        db.Feriados.Add(global);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        ctx.EmpresaId = minha.Id;
        var servico = new ServicoFeriados(db, ctx, new RelogioFalso(QuintaDeManha), Microsoft.Extensions.Logging.Abstractions.NullLogger<ServicoFeriados>.Instance);

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => servico.RemoverManualAsync(global.Id, default));

        db.ChangeTracker.Clear();
        Assert.True(await db.Feriados.IgnoreQueryFilters().AnyAsync(f => f.Id == global.Id));
    }

    // ============================================================ apoio
    private sealed record Ambiente(
        Cenario Cenario, Contato Contato, Conversa Conversa, ContextoMutavel Contexto,
        IServicoMeuDia MeuDia, IServicoDashboard Dashboard, IServicoLembretes Lembretes,
        IServicoConversas Conversas);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);

        // O relógio falso vai TAMBÉM para o interceptor de auditoria: sem isso o `criado_em` das
        // linhas semeadas sai com a data real da máquina, e "leads de hoje" conta zero contra um
        // "hoje" que está semanas à frente.
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);

        // Estas leituras rodam AUTENTICADAS (o vendedor abrindo o sistema).
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var cliente = new ClienteWhatsAppFalso();
        var enviador = new Nexora.Core.Whatsapp.EnviadorMensagem(
            new DadosMensagem(db, relogio), cliente,
            new Nexora.Core.Whatsapp.OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero },
            relogio, Microsoft.Extensions.Logging.Abstractions.NullLogger<Nexora.Core.Whatsapp.EnviadorMensagem>.Instance);

        return (db, tx, new Ambiente(
            cenario, cenario.Contato, cenario.Conversa, ctx,
            new ServicoMeuDia(db, ctx, relogio),
            new ServicoDashboard(db, relogio),
            new ServicoLembretes(db, ctx, relogio),
            new ServicoConversas(db, ctx, enviador, new ColetorAuditoria(), relogio)));
    }

    private static async Task AguardandoDesdeAsync(NexoraDbContext db, long conversaId, DateTime quando)
    {
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == conversaId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.AguardandoDesde, quando)
                .SetProperty(c => c.UltimaMensagemEm, quando)
                .SetProperty(c => c.UltimaMensagemDirecao, DirecaoMensagem.Entrada)
                .SetProperty(c => c.NaoLidas, 1));
        db.ChangeTracker.Clear();
    }

    private static async Task<long> CriarLembreteAsync(
        NexoraDbContext db, Ambiente amb, string titulo, DateOnly data, long? responsavelId,
        Func<NexoraDbContext, Ambiente, string, Task<long>>? outroContato = null)
    {
        // Contatos diferentes quando o teste cria mais de um AUTOMÁTICO no mesmo dia — o teto
        // diário barraria o segundo. Manuais não têm essa restrição.
        var contatoId = outroContato is null
            ? amb.Contato.Id
            : await outroContato(db, amb, titulo);

        var lembrete = new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = contatoId,
            Origem = OrigemLembrete.Manual,
            Status = StatusLembrete.Pendente,
            DataAlvo = data,
            Titulo = titulo,
            ResponsavelId = responsavelId
        };
        db.Lembretes.Add(lembrete);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return lembrete.Id;
    }

    private static async Task<long> OutroContatoAsync(NexoraDbContext db, Ambiente amb, string marca)
    {
        var contato = new Contato
        {
            EmpresaId = amb.Cenario.Id,
            Nome = $"Contato {marca}",
            Telefone = $"55849{Math.Abs((amb.Cenario.Empresa.Nome + marca).GetHashCode()) % 100000000:D8}",
            EtapaId = amb.Cenario.PrimeiraEtapa.Id,
            OrdemKanban = 2000m
        };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return contato.Id;
    }

    private static async Task<Usuario> OutroVendedorAsync(NexoraDbContext db, long empresaId, string sufixo)
    {
        var u = new Usuario
        {
            EmpresaId = empresaId,
            Nome = "Outro Vendedor",
            Email = $"outro-{sufixo}@exemplo.com",
            SenhaHash = HashSenha.Gerar("senha-de-teste-123"),
            Papel = PapelUsuario.Vendedor,
            Status = StatusUsuario.Ativo
        };
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return u;
    }
}
