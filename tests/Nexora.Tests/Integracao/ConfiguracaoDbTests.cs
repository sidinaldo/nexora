using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Api.Controllers;
using Nexora.Core.Entidades;
using Nexora.Core.FollowUp;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>As configurações da empresa contra Postgres real.
///
/// Duas das validações aqui existem porque o valor é ACEITÁVEL para o banco e DESASTROSO para o
/// produto — e o desastre é silencioso. Janela sem nenhum dia faz o follow-up nunca disparar;
/// zero dia de inatividade faz o robô escrever para quem acabou de ser atendido.</summary>
[Collection("banco")]
public class ConfiguracaoDbTests(BancoTeste banco)
{
    // Quinta, 10h30 em Brasília — dentro da janela padrão.
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    private static EditarAtendimento Padrao => new(8, 20, 126, 60, 240, 2);

    // ==================================================================== papel
    [Fact]
    public void VENDEDOR_NAO_ALTERA_CONFIGURACAO_DA_EMPRESA()
    {
        // ===================== ONDE ESTA REGRA VIVE =====================
        // O enforcement é [Authorize(Roles="dono")] no controller, não no serviço. Testar isso
        // sem subir HTTP significa ler o ATRIBUTO — que é exatamente o artefato que decide.
        // Se alguém remover o atributo, este teste quebra; se alguém mudar o serviço, não deve
        // quebrar, porque a regra não mora lá.
        // ===============================================================
        var tipo = typeof(ConfiguracaoController);

        foreach (var metodo in new[] { nameof(ConfiguracaoController.AtualizarDados),
                                       nameof(ConfiguracaoController.AtualizarAtendimento) })
        {
            var atributo = tipo.GetMethod(metodo)!
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .FirstOrDefault();

            Assert.NotNull(atributo);
            Assert.Equal("dono", atributo!.Roles);
        }

        // E a LEITURA continua aberta a qualquer papel: o vendedor precisa saber que horas a
        // empresa atende.
        var get = tipo.GetMethod(nameof(ConfiguracaoController.Obter))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true);
        Assert.Empty(get);
    }

    [Fact]
    public void Apagar_feriado_e_marcar_dia_de_trabalho_sao_so_do_dono()
    {
        var tipo = typeof(FeriadosController);
        foreach (var metodo in new[] { nameof(FeriadosController.Remover),
                                       nameof(FeriadosController.Ignorar),
                                       nameof(FeriadosController.Reativar) })
        {
            var atributo = tipo.GetMethod(metodo)!
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Single();
            Assert.Equal("dono", atributo.Roles);
        }
    }

    // ==================================================================== janela
    [Fact]
    public async Task Janela_com_fim_antes_do_inicio_e_recusada()
    {
        var (db, tx, amb) = await PrepararAsync("janela-invertida");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Config.AtualizarAtendimentoAsync(Padrao with { JanelaHoraInicio = 20, JanelaHoraFim = 8 }, default));

        Assert.Contains("antes do de fechamento", erro.Message);
        await NadaMudouAsync(db, amb);
    }

    [Fact]
    public async Task JANELA_SEM_NENHUM_DIA_MARCADO_E_RECUSADA()
    {
        // Bitmask 0 = a empresa não atende nunca. O follow-up para de disparar e o semáforo para
        // de acender — sem erro, sem log. É a configuração mais perigosa da tela.
        var (db, tx, amb) = await PrepararAsync("janela-sem-dia");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Config.AtualizarAtendimentoAsync(Padrao with { JanelaDiasSemana = 0 }, default));

        Assert.Contains("pelo menos um dia", erro.Message);
        await NadaMudouAsync(db, amb);
    }

    [Fact]
    public async Task Janela_valida_e_salva()
    {
        var (db, tx, amb) = await PrepararAsync("janela-ok");
        using var _ = db; using var __ = tx;

        // 9h-18h, segunda a sexta (bitmask 62).
        await amb.Config.AtualizarAtendimentoAsync(Padrao with
        {
            JanelaHoraInicio = 9, JanelaHoraFim = 18, JanelaDiasSemana = 62
        }, default);

        db.ChangeTracker.Clear();
        var e = await db.Empresas.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == amb.Cenario.Id);
        Assert.Equal((short)9, e.JanelaHoraInicio);
        Assert.Equal((short)18, e.JanelaHoraFim);
        Assert.Equal((short)62, e.JanelaDiasSemana);
    }

    // ==================================================================== semáforo
    [Fact]
    public async Task Faixa_amarela_maior_que_a_vermelha_e_recusada()
    {
        var (db, tx, amb) = await PrepararAsync("semaforo-invertido");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Config.AtualizarAtendimentoAsync(Padrao with
            {
                SemaforoAmareloMinutos = 300, SemaforoVermelhoMinutos = 120
            }, default));

        Assert.Contains("vermelho", erro.Message);
        await NadaMudouAsync(db, amb);
    }

    [Fact]
    public async Task Faixa_ZERO_desliga_a_cor_e_e_ACEITA()
    {
        // Zero é comportamento legítimo — quem não quer o alerta amarelo põe zero. Recusar seria
        // impor uma preferência.
        var (db, tx, amb) = await PrepararAsync("semaforo-zero");
        using var _ = db; using var __ = tx;

        await amb.Config.AtualizarAtendimentoAsync(Padrao with
        {
            SemaforoAmareloMinutos = 0, SemaforoVermelhoMinutos = 0
        }, default);

        db.ChangeTracker.Clear();
        var e = await db.Empresas.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == amb.Cenario.Id);
        Assert.Equal((short)0, e.SemaforoAmareloMinutos);
        Assert.Equal((short)0, e.SemaforoVermelhoMinutos);
    }

    // ==================================================================== follow-up
    [Fact]
    public async Task Dias_de_inatividade_ZERO_e_recusado()
    {
        // Zero geraria follow-up para conversa respondida HOJE — o robô escrevendo para quem
        // acabou de ser atendido.
        var (db, tx, amb) = await PrepararAsync("followup-zero");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Config.AtualizarAtendimentoAsync(Padrao with { DiasSemRespostaFollowUp = 0 }, default));

        Assert.Contains("pelo menos 1 dia", erro.Message);
        await NadaMudouAsync(db, amb);
    }

    // ==================================================================== dados da empresa
    [Fact]
    public async Task Documento_guarda_so_digitos_e_recusa_tamanho_errado()
    {
        var (db, tx, amb) = await PrepararAsync("documento");
        using var _ = db; using var __ = tx;

        await amb.Config.AtualizarDadosAsync(
            new EditarDadosEmpresa("Padaria Nova", "12.345.678/0001-90", "America/Sao_Paulo", null), default);

        db.ChangeTracker.Clear();
        var e = await db.Empresas.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == amb.Cenario.Id);
        Assert.Equal("Padaria Nova", e.Nome);
        Assert.Equal("12345678000190", e.Documento);   // máscara removida

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Config.AtualizarDadosAsync(
                new EditarDadosEmpresa("X", "123", "America/Sao_Paulo", null), default));
    }

    // ==================================================================== feriados
    [Fact]
    public async Task Feriado_duplicado_na_mesma_data_e_recusado()
    {
        var (db, tx, amb) = await PrepararAsync("feriado-dup");
        using var _ = db; using var __ = tx;

        var data = new DateOnly(2026, 9, 30);
        await amb.Feriados.CriarManualAsync(new NovoFeriado(data, "Aniversário da cidade"), default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Feriados.CriarManualAsync(new NovoFeriado(data, "Outro nome"), default));

        Assert.True(erro.Conflito);
        Assert.Contains("Já existe um feriado", erro.Message);
    }

    [Fact]
    public async Task Feriado_manual_na_data_de_um_NACIONAL_tambem_e_recusado()
    {
        // Deixar passar criaria dois feriados no mesmo dia, e o motor consultaria os dois à toa.
        var (db, tx, amb) = await PrepararAsync("feriado-sobre-nacional");
        using var _ = db; using var __ = tx;

        var natal = new Feriado
        {
            EmpresaId = null, Data = new DateOnly(2026, 12, 25),
            Nome = "Natal", Abrangencia = AbrangenciaFeriado.Nacional
        };
        db.Feriados.Add(natal);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Feriados.CriarManualAsync(new NovoFeriado(natal.Data, "Meu Natal"), default));
        Assert.True(erro.Conflito);
    }

    [Fact]
    public async Task FERIADO_NACIONAL_NAO_PODE_SER_APAGADO()
    {
        // A linha é GLOBAL: apagá-la apagaria o feriado de todos os tenants.
        var (db, tx, amb) = await PrepararAsync("nacional-imortal");
        using var _ = db; using var __ = tx;

        var nacional = new Feriado
        {
            EmpresaId = null, Data = new DateOnly(2026, 11, 15),
            Nome = "Proclamação da República", Abrangencia = AbrangenciaFeriado.Nacional
        };
        db.Feriados.Add(nacional);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Feriados.RemoverManualAsync(nacional.Id, default));

        Assert.True(erro.Conflito);
        Assert.Contains("não pode ser apagado", erro.Message);
        // E a mensagem ENSINA o caminho certo, em vez de só recusar.
        Assert.Contains("dia de trabalho", erro.Message);

        db.ChangeTracker.Clear();
        Assert.True(await db.Feriados.IgnoreQueryFilters().AnyAsync(f => f.Id == nacional.Id));
    }

    [Fact]
    public async Task Nacional_pode_ser_marcado_como_DIA_DE_TRABALHO_e_isso_e_por_empresa()
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        using var db = banco.NovoContexto(ctx, relogio);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await Semeador.TenantAsync(db, "trabalha-a");
        var vizinha = await Semeador.TenantAsync(db, "trabalha-b");

        var corpus = new Feriado
        {
            EmpresaId = null, Data = new DateOnly(2026, 6, 4),
            Nome = "Corpus Christi", Abrangencia = AbrangenciaFeriado.Nacional
        };
        db.Feriados.Add(corpus);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        ctx.EmpresaId = minha.Id; ctx.UsuarioId = minha.Dono.Id; ctx.Papel = "dono";
        var servico = new ServicoFeriados(db, ctx, relogio, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServicoFeriados>.Instance);
        await servico.IgnorarAsync(corpus.Id, default);
        db.ChangeTracker.Clear();

        // A linha global continua de pé — só a dispensa é do tenant.
        Assert.True(await db.Feriados.IgnoreQueryFilters().AnyAsync(f => f.Id == corpus.Id));

        var dados = new DadosFollowUp(db, relogio);
        var deMinha = await dados.FeriadosAsync(minha.Id, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), default);
        var deVizinha = await dados.FeriadosAsync(vizinha.Id, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), default);

        Assert.DoesNotContain(corpus.Data, deMinha);   // quem dispensou, trabalha
        Assert.Contains(corpus.Data, deVizinha);       // a vizinha continua fechada

        // E reativar desfaz.
        await servico.ReativarAsync(corpus.Id, default);
        db.ChangeTracker.Clear();
        Assert.Contains(corpus.Data,
            await dados.FeriadosAsync(minha.Id, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), default));
    }

    [Fact]
    public async Task Ignorar_feriado_MANUAL_e_recusado()
    {
        var (db, tx, amb) = await PrepararAsync("ignorar-manual");
        using var _ = db; using var __ = tx;

        var id = await amb.Feriados.CriarManualAsync(
            new NovoFeriado(new DateOnly(2026, 10, 20), "Ponto facultativo"), default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Feriados.IgnorarAsync(id, default));

        Assert.True(erro.Conflito);
        Assert.Contains("se apaga", erro.Message);
    }

    [Fact]
    public async Task Lista_de_feriados_MARCA_o_dispensado_em_vez_de_esconder()
    {
        // Sumir com ele esconderia do dono a decisão que ele mesmo tomou.
        var (db, tx, amb) = await PrepararAsync("lista-marca");
        using var _ = db; using var __ = tx;

        var nacional = new Feriado
        {
            EmpresaId = null, Data = new DateOnly(2026, 10, 12),
            Nome = "Nossa Senhora Aparecida", Abrangencia = AbrangenciaFeriado.Nacional
        };
        db.Feriados.Add(nacional);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Feriados.IgnorarAsync(nacional.Id, default);
        db.ChangeTracker.Clear();

        var lista = await amb.Feriados.ProximosAsync(default);
        var item = lista.Single(f => f.Id == nacional.Id);

        Assert.True(item.Ignorado);
        Assert.False(item.EhManual);
    }

    // ==================================================================== O CRITÉRIO 3
    [Fact]
    public async Task MUDAR_A_JANELA_PELA_CONFIGURACAO_MUDA_O_COMPORTAMENTO_DA_RODADA()
    {
        // ===================== O TESTE QUE FECHA O BLOCO =====================
        // Configuração que não muda comportamento não é configuração. Este teste roda o motor do
        // bloco 6 DUAS VEZES, com a mesma conversa parada, mudando só a janela pela API de
        // configuração — e prova que na primeira ele posta e na segunda ele apenas reserva.
        // =====================================================================
        var (db, tx, amb) = await PrepararAsync("janela-muda-rodada");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb);

        // 1ª rodada: janela padrão 8h-20h, e agora são 10h30 -> DENTRO. Posta.
        var primeira = await amb.Motor.ExecutarAsync();
        Assert.Equal(1, primeira.Gerados);
        Assert.Equal(1, primeira.Enviados);
        Assert.Single(amb.Cliente.TextosEnviados);

        // Limpa o rastro da 1ª rodada para a conversa voltar a ser elegível. MENSAGENS ANTES:
        // `mensagens.lembrete_id` referencia `lembretes` (é o índice uq_msg_lembrete que impede
        // reenvio), então apagar o lembrete primeiro viola a FK.
        db.ChangeTracker.Clear();
        await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.EmpresaId == amb.Cenario.Id && m.Direcao == DirecaoMensagem.Saida)
            .ExecuteDeleteAsync();
        await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ExecuteDeleteAsync();
        await PararConversaAsync(db, amb);
        amb.Cliente.TextosEnviados.Clear();

        // O DONO estreita a janela para 8h-9h. Agora 10h30 está FORA.
        await amb.Config.AtualizarAtendimentoAsync(Padrao with { JanelaHoraFim = 9 }, default);
        db.ChangeTracker.Clear();

        // 2ª rodada: mesma conversa, mesma hora — só a configuração mudou.
        var segunda = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, segunda.Gerados);
        Assert.Equal(0, segunda.Enviados);
        Assert.Equal(1, segunda.Adiados);
        Assert.Empty(amb.Cliente.TextosEnviados);   // A EVOLUTION NÃO FOI CHAMADA
    }

    [Fact]
    public async Task Mudar_dias_de_inatividade_muda_quem_e_elegivel()
    {
        var (db, tx, amb) = await PrepararAsync("dias-mudam-elegibilidade");
        using var _ = db; using var __ = tx;

        // Conversa parada há 3 dias. Com o padrão (2), é elegível.
        await PararConversaAsync(db, amb, diasAtras: 3);

        // O dono sobe para 7 dias: deixa de ser.
        await amb.Config.AtualizarAtendimentoAsync(Padrao with { DiasSemRespostaFollowUp = 7 }, default);
        db.ChangeTracker.Clear();

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);

        // Volta para 2: volta a ser.
        await amb.Config.AtualizarAtendimentoAsync(Padrao with { DiasSemRespostaFollowUp = 2 }, default);
        db.ChangeTracker.Clear();

        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    [Fact]
    public async Task MUDAR_A_FAIXA_DO_SEMAFORO_CHEGA_NO_PAINEL_SEM_REDEPLOY()
    {
        // A cor é calculada no CLIENTE a partir do timestamp; o que o servidor manda são os
        // LIMITES. Então "mudar a cor sem redeploy" é, do lado do servidor, o /api/painel/status
        // passar a devolver os limites novos na leitura seguinte.
        var (db, tx, amb) = await PrepararAsync("semaforo-imediato");
        using var _ = db; using var __ = tx;

        var relogio = new RelogioFalso(QuintaDeManha);
        var painel = new ServicoPainel(db, relogio);

        var antes = await painel.StatusAsync(default);
        Assert.Equal((short)60, antes.SemaforoAmareloMinutos);
        Assert.Equal((short)240, antes.SemaforoVermelhoMinutos);

        await amb.Config.AtualizarAtendimentoAsync(Padrao with
        {
            SemaforoAmareloMinutos = 15, SemaforoVermelhoMinutos = 45
        }, default);
        db.ChangeTracker.Clear();

        var depois = await painel.StatusAsync(default);
        Assert.Equal((short)15, depois.SemaforoAmareloMinutos);
        Assert.Equal((short)45, depois.SemaforoVermelhoMinutos);
    }

    [Fact]
    public async Task Mudar_a_janela_NAO_reprocessa_lembrete_ja_carimbado()
    {
        // Reprocessar significaria reescrever data de coisa já decidida — e o vendedor veria o
        // follow-up mudar de dia sozinho, sem entender por quê.
        var (db, tx, amb) = await PrepararAsync("nao-reprocessa");
        using var _ = db; using var __ = tx;

        var dataOriginal = new DateOnly(2026, 8, 6);
        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Cenario.Contato.Id,
            ConversaId = amb.Cenario.Conversa.Id,
            Origem = OrigemLembrete.Automatico,
            Status = StatusLembrete.Pendente,
            DataAlvo = dataOriginal,
            Titulo = "já carimbado",
            EnviaMensagem = true,
            TextoMensagem = "oi"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Muda a janela para dias em que 06/08 (quinta) não é atendido — só domingo.
        await amb.Config.AtualizarAtendimentoAsync(Padrao with { JanelaDiasSemana = 1 }, default);
        db.ChangeTracker.Clear();

        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.Titulo == "já carimbado");
        Assert.Equal(dataOriginal, lembrete.DataAlvo);   // intacto
    }

    // ==================================================================== minha conta
    [Fact]
    public async Task Minha_conta_altera_nome_e_email_do_PROPRIO_usuario()
    {
        var (db, tx, amb) = await PrepararAsync("minha-conta");
        using var _ = db; using var __ = tx;

        await amb.Equipe.AtualizarMinhaContaAsync(
            new EditarMinhaConta("Ana Souza Lima", "ANA.NOVA@Exemplo.com"), default);

        db.ChangeTracker.Clear();
        var u = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == amb.Cenario.Dono.Id);

        Assert.Equal("Ana Souza Lima", u.Nome);
        Assert.Equal("ana.nova@exemplo.com", u.Email);   // normalizado para minúsculas
    }

    [Fact]
    public async Task Email_ja_usado_por_OUTRA_EMPRESA_e_recusado()
    {
        // O índice é FUNCIONAL e GLOBAL (lower(email)), não por tenant. Checar só dentro da
        // empresa deixaria a violação estourar como erro de banco na cara do usuário.
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        using var db = banco.NovoContexto(ctx, relogio);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await Semeador.TenantAsync(db, "email-a");
        var vizinha = await Semeador.TenantAsync(db, "email-b");

        ctx.EmpresaId = minha.Id; ctx.UsuarioId = minha.Dono.Id; ctx.Papel = "dono";
        var equipe = new ServicoEquipe(db, ctx, relogio, new NotificadorEmailFalso(), new FilaSegundoPlanoFalsa());

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => equipe.AtualizarMinhaContaAsync(
                new EditarMinhaConta(minha.Dono.Nome, vizinha.Dono.Email), default));

        Assert.True(erro.Conflito);
        Assert.Contains("já está em uso", erro.Message);
    }

    [Fact]
    public async Task Email_invalido_e_recusado()
    {
        var (db, tx, amb) = await PrepararAsync("email-ruim");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Equipe.AtualizarMinhaContaAsync(new EditarMinhaConta("Ana", "sem-arroba"), default));
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto, ClienteWhatsAppFalso Cliente,
        IServicoConfiguracao Config, IServicoFeriados Feriados, IServicoEquipe Equipe,
        MotorFollowUp Motor);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);

        // Só esta empresa participa da rodada do motor.
        await db.Empresas.IgnoreQueryFilters()
            .Where(e => e.Id != cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Ativo, false));
        db.ChangeTracker.Clear();

        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var cliente = new ClienteWhatsAppFalso();
        var enviador = new EnviadorMensagem(
            new DadosMensagem(db, relogio), cliente,
            new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero },
            relogio, NullLogger<EnviadorMensagem>.Instance);

        var motor = new MotorFollowUp(
            new DadosFollowUp(db, relogio), enviador, relogio, NullLogger<MotorFollowUp>.Instance);

        return (db, tx, new Ambiente(
            cenario, ctx, cliente,
            new ServicoConfiguracao(db),
            new ServicoFeriados(db, ctx, relogio, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServicoFeriados>.Instance),
            new ServicoEquipe(db, ctx, relogio, new NotificadorEmailFalso(), new FilaSegundoPlanoFalsa()),
            motor));
    }

    /// <summary>Deixa a conversa parada há N dias com a última mensagem de SAÍDA — elegível.</summary>
    private static async Task PararConversaAsync(NexoraDbContext db, Ambiente amb, int diasAtras = 5)
    {
        var quando = QuintaDeManha.UtcDateTime.AddDays(-diasAtras);
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Cenario.Conversa.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UltimaMensagemEm, quando)
                .SetProperty(c => c.UltimaMensagemDirecao, DirecaoMensagem.Saida)
                .SetProperty(c => c.AguardandoDesde, (DateTime?)null));
        db.ChangeTracker.Clear();
    }

    /// <summary>Uma recusa NÃO pode gravar nada pela metade.</summary>
    private static async Task NadaMudouAsync(NexoraDbContext db, Ambiente amb)
    {
        db.ChangeTracker.Clear();
        var e = await db.Empresas.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == amb.Cenario.Id);
        Assert.Equal((short)8, e.JanelaHoraInicio);
        Assert.Equal((short)20, e.JanelaHoraFim);
        Assert.Equal((short)126, e.JanelaDiasSemana);
        Assert.Equal((short)60, e.SemaforoAmareloMinutos);
        Assert.Equal((short)240, e.SemaforoVermelhoMinutos);
        Assert.Equal((short)2, e.DiasSemRespostaFollowUp);
    }
}
