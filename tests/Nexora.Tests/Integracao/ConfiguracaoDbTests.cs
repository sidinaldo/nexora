using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Api.Controllers;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.FollowUp;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;
using Nexora.Tests.Unidade;

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

    // Penúltimo `DiasParaConcluirVenda` (NEG-2): 7 dias, o padrão do banco. Último, a conclusão
    // automática LIGADA (POS-1) — que é o padrão da coluna.
    private static EditarAtendimento Padrao => new(8, 20, 126, 60, 240, 2, 7, true);

    // ==================================================================== papel
    [Fact]
    public void VENDEDOR_NAO_ALTERA_CONFIGURACAO_DA_EMPRESA()
    {
        // ===================== ONDE ESTA REGRA VIVE =====================
        // O enforcement é a política `ConfigurarEmpresa` no controller, não no serviço. Testar isso
        // sem subir HTTP significa ler o ATRIBUTO — que é exatamente o artefato que decide.
        // Se alguém remover o atributo, este teste quebra; se alguém mudar o serviço, não deve
        // quebrar, porque a regra não mora lá.
        // ===============================================================
        var tipo = typeof(ConfiguracaoController);

        foreach (var metodo in new[] { nameof(ConfiguracaoController.AtualizarDados),
                                       nameof(ConfiguracaoController.AtualizarAtendimento) })
        {
            // Quem ENTRA, e não como o atributo escreve — ver `PapeisDaRota`.
            Assert.Equal("dono", PapeisDaRota.De(tipo, metodo));
        }

        // E a LEITURA continua aberta a qualquer papel: o vendedor precisa saber que horas a
        // empresa atende.
        Assert.Equal("autenticado", PapeisDaRota.De(tipo, nameof(ConfiguracaoController.Obter)));
    }

    [Fact]
    public void Escrever_feriado_e_so_do_dono__NAS_QUATRO_ROTAS()
    {
        // ⚠️ O `Criar` ENTROU NESTA LISTA, e antes era a exceção: ele aceitava dono E gestor, por
        // uma permissão `CadastrarFeriado` que só existia para ele. O gestor criava um feriado e
        // não podia apagá-lo nem marcá-lo como dia de trabalho — e não tinha tela para nenhum dos
        // dois, porque `/configuracoes` sempre foi do dono.
        //
        // Feriado não é anotação de agenda: ele muda o tempo útil (o semáforo da equipe inteira) e
        // o dia em que o follow-up automático sai para o cliente. É configuração, e a própria
        // documentação do enum já dizia isso.
        var tipo = typeof(FeriadosController);
        foreach (var metodo in new[] { nameof(FeriadosController.Criar),
                                       nameof(FeriadosController.Remover),
                                       nameof(FeriadosController.Ignorar),
                                       nameof(FeriadosController.Reativar) })
        {
            Assert.Equal("dono", PapeisDaRota.De(tipo, metodo));
        }

        // E a LEITURA continua aberta: o follow-up e o semáforo do vendedor dependem dela.
        Assert.Equal("autenticado", PapeisDaRota.De(tipo, nameof(FeriadosController.Proximos)));
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

    // ==================================================================== o texto do follow-up
    /// <summary>BUG-XX (T6): o texto do follow-up é editável junto com os dias. NULO mantém o atual —
    /// um painel aberto antes do deploy salva o horário sem este campo e não pode apagar o texto.</summary>
    [Fact]
    public async Task O_TEXTO_DO_FOLLOW_UP_SALVA_E_NULO_MANTEM()
    {
        var (db, tx, amb) = await PrepararAsync("followup-texto");
        using var _ = db; using var __ = tx;

        Assert.Equal(Empresa.FollowUpTextoPadrao, (await amb.Config.ObterAsync(default)).FollowUpTexto);

        await amb.Config.AtualizarAtendimentoAsync(
            Padrao with { FollowUpTexto = "  {{saudacao}} Seu orçamento continua valendo.  " }, default);
        db.ChangeTracker.Clear();
        Assert.Equal("{{saudacao}} Seu orçamento continua valendo.",
            (await amb.Config.ObterAsync(default)).FollowUpTexto);

        await amb.Config.AtualizarAtendimentoAsync(Padrao with { JanelaHoraInicio = 9 }, default);
        db.ChangeTracker.Clear();
        Assert.Equal("{{saudacao}} Seu orçamento continua valendo.",
            (await amb.Config.ObterAsync(default)).FollowUpTexto);
    }

    [Fact]
    public async Task O_TEXTO_DO_FOLLOW_UP_EM_BRANCO_OU_LONGO_DEMAIS_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("followup-branco");
        using var _ = db; using var __ = tx;

        var branco = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Config.AtualizarAtendimentoAsync(Padrao with { FollowUpTexto = "   " }, default));
        Assert.Contains("texto do follow-up", branco.Message);

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Config.AtualizarAtendimentoAsync(
                Padrao with { FollowUpTexto = new string('a', ServicoConfiguracao.LimiteDeTexto + 1) }, default));
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

        var lista = (await amb.Feriados.ProximosAsync(1, 100, default)).Itens;
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
        // configuração — e prova que na primeira ele posta e na segunda não faz nada (BUG-XX:
        // fora do horário a rodada espera a próxima hora; não reserva mais).
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

        Assert.Equal(0, segunda.Gerados);
        Assert.Equal(0, segunda.Enviados);
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
        var painel = new ServicoPainel(db, relogio, amb.Contexto);

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
        var equipe = new ServicoEquipe(
            db, ctx, relogio, new NotificadorEmailFalso(), new FilaSegundoPlanoFalsa(),
            new ColetorAuditoria());

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
    /// <summary>Os feriados paginam no BANCO, com o total pronto (AUD-XX, #21). O total é o dos
    /// feriados que a empresa VÊ: o manual da outra empresa não entra.</summary>
    [Fact]
    public async Task OS_FERIADOS_PAGINAM_NO_BANCO_COM_O_TOTAL()
    {
        var (db, tx, amb) = await PrepararAsync("feriado-pagina");
        using var _ = db; using var __ = tx;

        var outra = await Semeador.TenantAsync(db, "feriado-pagina-b");
        db.Feriados.Add(new Feriado
        {
            EmpresaId = outra.Id, Data = new DateOnly(2026, 9, 30), Nome = "Aniversário da outra",
            Abrangencia = AbrangenciaFeriado.Manual
        });
        foreach (var (data, nome) in new[]
                 {
                     (new DateOnly(2026, 10, 15), "Dia da loja"),
                     (new DateOnly(2026, 11, 20), "Ponto facultativo"),
                     (new DateOnly(2026, 12, 24), "Véspera de Natal")
                 })
        {
            db.Feriados.Add(new Feriado
            {
                EmpresaId = amb.Cenario.Id, Data = data, Nome = nome,
                Abrangencia = AbrangenciaFeriado.Manual
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var todos = await amb.Feriados.ProximosAsync(1, 100, default);

        Assert.True(todos.TotalCount >= 3, $"só {todos.TotalCount} feriados à frente");
        Assert.Equal(todos.Itens.Count, todos.TotalCount);
        Assert.DoesNotContain(todos.Itens, f => f.Nome == "Aniversário da outra");

        var primeira = await amb.Feriados.ProximosAsync(1, 2, default);

        Assert.Equal(2, primeira.Itens.Count);
        Assert.Equal(todos.TotalCount, primeira.TotalCount);
        Assert.Equal(todos.Itens.Take(2).Select(f => f.Id), primeira.Itens.Select(f => f.Id));

        var ultima = await amb.Feriados.ProximosAsync(primeira.TotalPaginas, 2, default);

        Assert.Equal(todos.Itens.Last().Id, ultima.Itens.Last().Id);
    }

    /// <summary>A tabela da Equipe pagina no BANCO, com o total pronto (AUD-XX, #21). A lista
    /// inteira continua existindo para os seletores de responsável.</summary>
    [Fact]
    public async Task A_EQUIPE_PAGINA_NO_BANCO_COM_O_TOTAL()
    {
        var (db, tx, amb) = await PrepararAsync("equipe-pagina");
        using var _ = db; using var __ = tx;

        await Semeador.TenantAsync(db, "equipe-pagina-b");
        foreach (var nome in new[] { "Ana", "Bruno" })
        {
            db.Usuarios.Add(new Usuario
            {
                EmpresaId = amb.Cenario.Id, Nome = nome,
                Email = $"{nome.ToLowerInvariant()}-{Guid.NewGuid():N}@exemplo.com",
                SenhaHash = Nexora.Core.Seguranca.HashSenha.Gerar("senha-de-teste-123"),
                Papel = PapelUsuario.Vendedor, Status = StatusUsuario.Ativo
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var inteira = await amb.Equipe.ListarAsync(default);
        var primeira = await amb.Equipe.PaginaAsync(1, 2, default);
        var segunda = await amb.Equipe.PaginaAsync(2, 2, default);

        Assert.Equal(3, inteira.Count);
        Assert.Equal(3, primeira.TotalCount);
        Assert.Equal(2, primeira.TotalPaginas);
        Assert.Equal(2, primeira.Itens.Count);
        Assert.Single(segunda.Itens);
        Assert.Equal(
            inteira.Select(u => u.Id),
            primeira.Itens.Concat(segunda.Itens).Select(u => u.Id));
    }

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
            new ServicoConfiguracao(db, new ColetorAuditoria()),
            new ServicoFeriados(db, ctx, relogio, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServicoFeriados>.Instance),
            new ServicoEquipe(db, ctx, relogio, new NotificadorEmailFalso(),
                new FilaSegundoPlanoFalsa(), new ColetorAuditoria()),
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

    // ==================================================== POS-1 · o liga/desliga da conclusao

    [Fact]
    public async Task A_CONCLUSAO_AUTOMATICA_VAI_E_VOLTA_E_O_PRAZO_SOBREVIVE()
    {
        // ⚠️ O PRAZO TEM DE SOBREVIVER AO DESLIGAMENTO. E o que faz religar devolver o que a empresa
        // tinha, em vez de 7 por acidente — e e por isso que o liga/desliga e uma coluna propria em
        // vez de um valor sentinela no numero.
        var (db, tx, amb) = await PrepararAsync("pos1-liga-desliga");
        using var _ = db; using var __ = tx;

        await amb.Config.AtualizarAtendimentoAsync(
            Padrao with { DiasParaConcluirVenda = 30 }, default);
        db.ChangeTracker.Clear();

        await amb.Config.AtualizarAtendimentoAsync(
            Padrao with { DiasParaConcluirVenda = 30, ConclusaoAutomatica = false }, default);
        db.ChangeTracker.Clear();

        var desligada = await amb.Config.ObterAsync(default);
        Assert.False(desligada.ConclusaoAutomatica);
        Assert.Equal((short)30, desligada.DiasParaConcluirVenda);

        await amb.Config.AtualizarAtendimentoAsync(
            Padrao with { DiasParaConcluirVenda = 30, ConclusaoAutomatica = true }, default);
        db.ChangeTracker.Clear();

        var religada = await amb.Config.ObterAsync(default);
        Assert.True(religada.ConclusaoAutomatica);
        Assert.Equal((short)30, religada.DiasParaConcluirVenda);
    }

    [Fact]
    public async Task O_CAMPO_DA_CONCLUSAO_OMITIDO_E_RECUSADO_EM_VEZ_DE_DESLIGAR()
    {
        // ===================== O DEFAULT DO TIPO NAO PODE SER UMA ESCOLHA =====================
        // Este PUT manda o documento inteiro. Um `bool` omitido desserializa para `false`, que e
        // VALIDO — a conclusao automatica seria desligada em silencio, por um valor que ninguem
        // escolheu, desfazendo uma decisao do dono. Com `bool?`, o omitido vira erro.
        //
        // E a unica validacao deste metodo que nao protege uma FAIXA.
        // ==================================================================================
        var (db, tx, amb) = await PrepararAsync("pos1-omitido");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Config.AtualizarAtendimentoAsync(
                Padrao with { ConclusaoAutomatica = null }, default));

        Assert.Contains("conclusão automática", erro.Message);
        await NadaMudouAsync(db, amb);
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
        // POS-1: a coluna nova entra na rede. Sem isto, uma recusa que apagasse o interruptor
        // passaria por aqui sem ninguem ver.
        Assert.True(e.ConclusaoAutomatica);
    }
}
