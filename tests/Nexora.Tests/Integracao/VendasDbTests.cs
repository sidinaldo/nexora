using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>NEG-1 — o histórico de vendas contra Postgres real.
///
/// ===================== O DEFEITO QUE ISTO CORRIGE =====================
/// A venda morava em COLUNA do contato (`ganho_em`, `valor`). Coluna guarda um valor só.
///
/// João compra em março por 5.000. Volta em julho; para negociar de novo, o vendedor reabre —
/// e reabrir limpa `ganho_em`, porque `ck_contatos_terminal` proíbe estar ganho e em negociação
/// ao mesmo tempo. A venda de março não foi arquivada: a coluna foi SOBRESCRITA. O dashboard,
/// que conta `WHERE ganho_em >= inicioDoMes`, deixa de encontrá-la.
///
/// O sintoma é o pior possível num sistema de vendas: **o faturamento de um mês fechado muda
/// sozinho**, e ninguém sabe dizer por quê.
///
/// Padaria, oficina, clínica e salão vivem de cliente recorrente. O modelo antigo travava no
/// segundo mês de uso.
/// ======================================================================</summary>
[Collection("banco")]
public class VendasDbTests(BancoTeste banco)
{
    // ==================================================================== o teste que prova o bloco
    [Fact]
    public async Task COMPRA_REABRE_E_COMPRA_DE_NOVO_soma_as_duas_no_faturamento()
    {
        // Este foi escrito PRIMEIRO, antes da tabela existir. É o enunciado do problema virado
        // em asserção: se ele passar, o bloco fez o que prometeu; se falhar, nada mais importa.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-recorrente");
        using var _ = db; using var __ = tx;

        var joao = await CriarContatoAsync(db, amb.Cenario, "João Recorrente");

        // Março: 5.000
        await amb.Contatos.MarcarGanhoAsync(joao.Id, 5000m, default);

        // Julho: ele volta. O vendedor reabre para negociar de novo.
        await amb.Contatos.ReabrirAsync(joao.Id, default);

        // E fecha a segunda venda.
        await amb.Contatos.MarcarGanhoAsync(joao.Id, 3000m, default);

        db.ChangeTracker.Clear();
        var painel = await amb.Dashboard.DashboardAsync(default);

        Assert.Equal(2, painel.VendasDoMes);
        Assert.Equal(8000m, painel.FaturamentoDoMes);   // 5.000 + 3.000, não só a última

        // E o contato continua com UM carimbo — o da venda vigente.
        var contato = await db.Contatos.AsNoTracking().SingleAsync(c => c.Id == joao.Id);
        Assert.Equal(3000m, contato.Valor);
    }

    [Fact]
    public async Task Reabrir_NAO_apaga_a_linha_de_vendas()
    {
        // O carimbo é o estado de agora; a linha é o que aconteceu. Reabrir mexe no primeiro.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-reabrir");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 1200m, default);
        await amb.Contatos.ReabrirAsync(c.Id, default);

        db.ChangeTracker.Clear();
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);
        Assert.Equal(1200m, venda.Valor);
        Assert.Null(venda.CanceladaEm);

        // O carimbo, esse sim, saiu.
        Assert.Null((await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == c.Id)).GanhoEm);
    }

    [Fact]
    public async Task Mes_fechado_NAO_muda_depois_de_reabrir_um_contato_daquele_mes()
    {
        // A consequência que dava para sentir sem entender: o dono confere o faturamento de
        // março em abril e encontra outro número.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-mes-fechado");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 2500m, default);

        db.ChangeTracker.Clear();
        var antes = await amb.Dashboard.DashboardAsync(default);

        await amb.Contatos.ReabrirAsync(c.Id, default);

        db.ChangeTracker.Clear();
        var depois = await amb.Dashboard.DashboardAsync(default);

        Assert.Equal(antes.VendasDoMes, depois.VendasDoMes);
        Assert.Equal(antes.FaturamentoDoMes, depois.FaturamentoDoMes);
    }

    // ==================================================================== gravação
    [Fact]
    public async Task Marcar_ganho_grava_a_coluna_E_a_linha()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-grava");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 990m, default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);

        Assert.NotNull(contato.GanhoEm);
        Assert.Equal(990m, contato.Valor);

        Assert.Equal(990m, venda.Valor);
        Assert.Equal(contato.GanhoEm, venda.FechadaEm);          // o mesmo instante nos dois
        Assert.Equal(amb.Cenario.Dono.Id, venda.ResponsavelId);  // quem fechou
        Assert.Equal(amb.Cenario.Id, venda.EmpresaId);

        // `etapa_id` congela a etapa de ganho do momento: o nome dela pode mudar depois.
        var etapaGanho = await db.EtapasFunil.AsNoTracking().FirstAsync(e => e.EGanho);
        Assert.Equal(etapaGanho.Id, venda.EtapaId);
    }

    [Fact]
    public async Task Valor_invalido_nao_deixa_nem_carimbo_nem_linha()
    {
        // A metade do "mesma transação" que dá para provar sem derrubar o banco: a recusa
        // acontece antes de qualquer escrita, então não sobra estado pela metade.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-invalido");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarGanhoAsync(c.Id, 0m, default));

        db.ChangeTracker.Clear();
        Assert.Null((await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == c.Id)).GanhoEm);
        Assert.False(await db.Vendas.AsNoTracking().AnyAsync(v => v.ContatoId == c.Id));
    }

    [Fact]
    public async Task A_linha_e_o_carimbo_caem_JUNTOS_quando_o_banco_recusa()
    {
        // A outra metade: falha DEPOIS de a primeira escrita já estar no ar. Forçada por um
        // valor que estoura o CHECK `ck_vendas_valor` — a linha é recusada pelo banco, e o
        // carimbo do contato tem que voltar atrás junto.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-atomico");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");

        // Ponto de salvamento: o teste inteiro roda numa transação, e a falha de banco a
        // invalidaria para as asserções seguintes.
        await db.Database.ExecuteSqlRawAsync("SAVEPOINT antes_do_ganho");
        try
        {
            await amb.Contatos.MarcarGanhoAsync(c.Id, ValorQueOBancoRecusa, default);
            Assert.Fail("O banco deveria ter recusado o valor.");
        }
        catch (Exception e) when (e is not Xunit.Sdk.FailException)
        {
            await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT antes_do_ganho");
        }

        db.ChangeTracker.Clear();
        Assert.Null((await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == c.Id)).GanhoEm);
        Assert.False(await db.Vendas.AsNoTracking().AnyAsync(v => v.ContatoId == c.Id));
    }

    // ==================================================================== cancelamento
    [Fact]
    public async Task Cancelar_MARCA_a_linha_e_nao_apaga()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-cancelar");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 700m, default);

        db.ChangeTracker.Clear();
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);

        await amb.Vendas.CancelarAsync(venda.Id, default);

        db.ChangeTracker.Clear();
        var depois = await db.Vendas.AsNoTracking().SingleAsync(v => v.Id == venda.Id);
        Assert.NotNull(depois.CanceladaEm);                       // marcada
        Assert.Equal(amb.Cenario.Dono.Id, depois.CanceladaPor);   // por quem

        // E saiu da contagem, sem sumir do banco.
        var painel = await amb.Dashboard.DashboardAsync(default);
        Assert.Equal(0, painel.VendasDoMes);
        Assert.Equal(0m, painel.FaturamentoDoMes);
    }

    [Fact]
    public async Task Cancelar_a_venda_VIGENTE_limpa_o_carimbo_do_contato()
    {
        // Senão o card fica na etapa de ganho sem venda nenhuma por trás — o estado divergente
        // que a porta única do funil existe para impedir.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-cancela-vigente");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 700m, default);

        db.ChangeTracker.Clear();
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);
        await amb.Vendas.CancelarAsync(venda.Id, default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        Assert.Null(contato.GanhoEm);
        Assert.Null(contato.Valor);

        var etapa = await db.EtapasFunil.AsNoTracking().SingleAsync(e => e.Id == contato.EtapaId);
        Assert.False(etapa.EGanho);   // voltou ao quadro
    }

    [Fact]
    public async Task Cancelar_uma_venda_ANTIGA_nao_mexe_no_carimbo_da_vigente()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-cancela-antiga");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 5000m, default);
        await amb.Contatos.ReabrirAsync(c.Id, default);
        await amb.Contatos.MarcarGanhoAsync(c.Id, 3000m, default);

        db.ChangeTracker.Clear();
        var antiga = await db.Vendas.AsNoTracking()
            .OrderBy(v => v.Id).FirstAsync(v => v.ContatoId == c.Id);

        await amb.Vendas.CancelarAsync(antiga.Id, default);

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        Assert.NotNull(contato.GanhoEm);          // a de 3.000 continua vigente
        Assert.Equal(3000m, contato.Valor);

        var painel = await amb.Dashboard.DashboardAsync(default);
        Assert.Equal(1, painel.VendasDoMes);
        Assert.Equal(3000m, painel.FaturamentoDoMes);
    }

    [Fact]
    public async Task VENDEDOR_nao_cancela_venda()
    {
        // Cancelar apaga faturamento da contagem. É decisão de quem responde pelo número.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-papel");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 700m, default);

        db.ChangeTracker.Clear();
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);

        amb.Contexto.Papel = "vendedor";
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Vendas.CancelarAsync(venda.Id, default));

        db.ChangeTracker.Clear();
        Assert.Null((await db.Vendas.AsNoTracking().SingleAsync(v => v.Id == venda.Id)).CanceladaEm);

        // E gestor PODE — senão o teste passaria com uma regra que recusa todo mundo.
        amb.Contexto.Papel = "gestor";
        await amb.Vendas.CancelarAsync(venda.Id, default);
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.Vendas.AsNoTracking().SingleAsync(v => v.Id == venda.Id)).CanceladaEm);
    }

    // ==================================================================== isolamento
    [Fact]
    public async Task O_query_filter_isola_vendas_entre_tenants()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg-iso");
        using var _ = db; using var __ = tx;

        var alheia = await Semeador.TenantAsync(db, "neg-iso-vizinha");
        db.ChangeTracker.Clear();

        var meu = await CriarContatoAsync(db, amb.Cenario, "Meu Cliente");
        var dela = await CriarContatoAsync(db, alheia, "Cliente Dela");

        await amb.Contatos.MarcarGanhoAsync(meu.Id, 100m, default);

        // A venda da vizinha entra por baixo do serviço, direto no banco.
        db.Vendas.Add(new Venda
        {
            EmpresaId = alheia.Id, ContatoId = dela.Id, Valor = 999999m,
            FechadaEm = ContatosDbTests.Agora.UtcDateTime, EtapaId = alheia.Etapas[^1].Id
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // O contexto continua no MEU tenant: a dela não pode aparecer nem na lista nem na soma.
        Assert.All(await db.Vendas.AsNoTracking().ToListAsync(),
            v => Assert.Equal(amb.Cenario.Id, v.EmpresaId));

        var painel = await amb.Dashboard.DashboardAsync(default);
        Assert.Equal(100m, painel.FaturamentoDoMes);   // não 1.000.099

        // E cancelar a venda de outro tenant não encontra a linha.
        var daVizinha = await db.Vendas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(v => v.EmpresaId == alheia.Id);
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Vendas.CancelarAsync(daVizinha.Id, default));
    }

    // ============================================================ NEG-2 · o estado da venda
    /// <summary>===================== O TESTE QUE PROVA O BLOCO =====================
    ///
    /// CONCLUIR e CANCELAR mexem no mesmo lugar do modelo e têm efeitos OPOSTOS no relatório:
    ///
    ///   concluída  = "esse pedido acabou". Sai do QUADRO; o dinheiro FICA. É o que impede a
    ///                coluna Venda de acumular para sempre sem custar faturamento a ninguém.
    ///
    ///   cancelada  = a venda NUNCA DEVERIA TER SIDO REGISTRADA (erro de digitação, duplicata).
    ///                Sai RETROATIVAMENTE: o relatório do mês corrige, porque aquilo não
    ///                aconteceu.
    ///
    /// Se concluir tirasse faturamento, ninguém concluiria — e a coluna voltaria a acumular em
    /// três meses, com o bloco inteiro não tendo resolvido nada.
    ///
    /// AS DUAS AO MESMO TEMPO, no mesmo mês, é o que torna o teste difícil de passar por
    /// acidente: uma implementação que trate concluir como cancelar zera o faturamento, e uma
    /// que trate cancelar como concluir mantém os 1000 que deviam ter saído.
    /// ====================================================================== </summary>
    [Fact]
    public async Task CONCLUIR_MANTEM_O_FATURAMENTO_MAS_CANCELAR_TIRA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-concluir-x-cancelar");
        using var _ = db; using var __ = tx;

        // Duas vendas no MESMO mês (o mês corrente do relógio do teste), de valores distintos
        // para dar para saber qual saiu.
        var doCancelamento = await CriarContatoAsync(db, amb.Cenario, "Vai ser cancelada");
        var daConclusao = await CriarContatoAsync(db, amb.Cenario, "Vai ser concluída");

        await amb.Contatos.MarcarGanhoAsync(doCancelamento.Id, 1000m, default);
        await amb.Contatos.MarcarGanhoAsync(daConclusao.Id, 700m, default);

        db.ChangeTracker.Clear();
        var antes = await amb.Dashboard.DashboardAsync(default);
        Assert.Equal(1700m, antes.FaturamentoDoMes);

        var vCancelar = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == doCancelamento.Id);
        var vConcluir = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == daConclusao.Id);

        // ===== 1. CONCLUIR: o pedido acabou. O relatório não muda. =====
        Assert.Equal(1, await amb.Vendas.ConcluirAsync([vConcluir.Id], default));

        db.ChangeTracker.Clear();
        var vDepois = await db.Vendas.AsNoTracking().SingleAsync(v => v.Id == vConcluir.Id);

        Assert.Equal(StatusVenda.Concluida, vDepois.Status);
        Assert.NotNull(vDepois.ConcluidaEm);
        // ⚠️ A COLUNA DE CANCELAMENTO NÃO FOI TOCADA. É a asserção central: se concluir escrevesse
        // em `cancelada_em`, tudo o mais passaria e o faturamento sumiria junto com o card.
        Assert.Null(vDepois.CanceladaEm);
        // E o mês em que ela fechou continua sendo o dela.
        Assert.Equal(vConcluir.FechadaEm, vDepois.FechadaEm);

        // ===== 2. CANCELAR: aquilo não aconteceu. Sai retroativamente. =====
        await amb.Vendas.CancelarAsync(vCancelar.Id, default);

        db.ChangeTracker.Clear();
        var cDepois = await db.Vendas.AsNoTracking().SingleAsync(v => v.Id == vCancelar.Id);
        Assert.Equal(StatusVenda.Cancelada, cDepois.Status);
        Assert.NotNull(cDepois.CanceladaEm);
        Assert.Null(cDepois.ConcluidaEm);

        // ===== 3. OS DOIS EFEITOS, LADO A LADO =====
        var depois = await amb.Dashboard.DashboardAsync(default);

        // A cancelada sumiu da CONTAGEM — nunca existiu. A concluída continua lá.
        Assert.Equal(1, depois.VendasDoMes);

        // 1700 − 1000 (cancelada) = 700. A conclusão NÃO desconta nada.
        Assert.Equal(700m, depois.FaturamentoDoMes);
    }

    // ==================================================================== concluir
    [Fact]
    public async Task CONCLUIR_TIRA_O_CARD_DA_COLUNA_e_MANTEM_o_faturamento()
    {
        // A razão de existir do bloco: a coluna Venda parava de acumular sem que o dinheiro
        // sumisse do relatório.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-concluir");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 500m, default);

        db.ChangeTracker.Clear();
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);
        var etapaGanho = await db.EtapasFunil.AsNoTracking().FirstAsync(e => e.EGanho);

        Assert.Equal(1, (await amb.Funil.QuadroAsync(50, default))
            .Colunas.Single(x => x.EtapaId == etapaGanho.Id).Total);

        await amb.Vendas.ConcluirAsync([venda.Id], default);
        db.ChangeTracker.Clear();

        // Saiu do quadro...
        Assert.Equal(0, (await amb.Funil.QuadroAsync(50, default))
            .Colunas.Single(x => x.EtapaId == etapaGanho.Id).Total);

        // ...e continua no faturamento.
        var painel = await amb.Dashboard.DashboardAsync(default);
        Assert.Equal(1, painel.VendasDoMes);
        Assert.Equal(500m, painel.FaturamentoDoMes);
    }

    [Fact]
    public async Task Concluir_NAO_altera_ganho_em_nem_valor_do_contato()
    {
        // Concluir é sobre o PEDIDO, não sobre o negócio. Mexer no carimbo faria o contato
        // parecer reaberto — e o kanban o devolveria para "Novo Lead".
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-carimbo");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 300m, default);

        db.ChangeTracker.Clear();
        var antes = await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);

        await amb.Vendas.ConcluirAsync([venda.Id], default);

        db.ChangeTracker.Clear();
        var depois = await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        Assert.Equal(antes.GanhoEm, depois.GanhoEm);
        Assert.Equal(antes.Valor, depois.Valor);
        Assert.Equal(antes.EtapaId, depois.EtapaId);
    }

    [Fact]
    public async Task CONCLUIR_EM_LOTE_conclui_todas_de_uma_vez()
    {
        // Sem lote o vendedor não faz — e a coluna volta a acumular.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-lote");
        using var _ = db; using var __ = tx;

        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var c = await CriarContatoAsync(db, amb.Cenario, $"Cliente {i}");
            await amb.Contatos.MarcarGanhoAsync(c.Id, 100m + i, default);
            db.ChangeTracker.Clear();
            ids.Add((await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id)).Id);
        }

        var quantas = await amb.Vendas.ConcluirAsync(ids, default);

        Assert.Equal(3, quantas);
        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.Vendas.CountAsync(v => v.Status == StatusVenda.Concluida));
    }

    [Fact]
    public async Task VENDEDOR_CONCLUI_mas_NAO_CANCELA()
    {
        // Concluir é ação operacional do vendedor sobre o próprio pedido. Cancelar tira dinheiro
        // do relatório — é decisão de quem responde pelo número.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-papel");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 200m, default);
        db.ChangeTracker.Clear();
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);

        amb.Contexto.Papel = "vendedor";
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Vendas.CancelarAsync(venda.Id, default));

        // E CONCLUI, no mesmo papel — senão o teste passaria com uma regra que recusa tudo.
        Assert.Equal(1, await amb.Vendas.ConcluirAsync([venda.Id], default));
    }

    [Fact]
    public async Task Venda_CANCELADA_nao_pode_ser_concluida()
    {
        // Transição explícita, e o silêncio aqui é DELIBERADO: o lote devolve "0 concluídas" em
        // vez de lançar, senão uma linha que outra pessoa cancelou no meio derrubaria as trinta.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-transicao");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 200m, default);
        db.ChangeTracker.Clear();
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);

        await amb.Vendas.CancelarAsync(venda.Id, default);
        db.ChangeTracker.Clear();

        Assert.Equal(0, await amb.Vendas.ConcluirAsync([venda.Id], default));

        db.ChangeTracker.Clear();
        var depois = await db.Vendas.AsNoTracking().SingleAsync(v => v.Id == venda.Id);
        // E o estado NÃO foi sobrescrito: cancelada continua cancelada.
        Assert.Equal(StatusVenda.Cancelada, depois.Status);
        Assert.Null(depois.ConcluidaEm);
    }

    [Fact]
    public async Task CONCLUIR_DUAS_VEZES_e_idempotente()
    {
        // O botão da tela pode ser clicado duas vezes, e duas pessoas podem concluir a mesma
        // venda. A segunda chamada não pode reescrever `concluida_em` — a data em que o pedido
        // acabou é a primeira, não a última.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-idempotente");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Cliente");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 200m, default);
        db.ChangeTracker.Clear();
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);

        Assert.Equal(1, await amb.Vendas.ConcluirAsync([venda.Id], default));
        db.ChangeTracker.Clear();
        var primeira = await db.Vendas.AsNoTracking().SingleAsync(v => v.Id == venda.Id);

        Assert.Equal(0, await amb.Vendas.ConcluirAsync([venda.Id], default));
        db.ChangeTracker.Clear();
        var segunda = await db.Vendas.AsNoTracking().SingleAsync(v => v.Id == venda.Id);

        Assert.Equal(primeira.ConcluidaEm, segunda.ConcluidaEm);
    }

    // ==================================================================== kanban
    [Fact]
    public async Task CONTATO_COM_DUAS_VENDAS_EM_ABERTO_mostra_o_numero_no_card()
    {
        // O card conta CONTATO; contato que comprou duas vezes apareceria uma. O número no card
        // resolve sem mudar o modelo.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-duas");
        using var _ = db; using var __ = tx;

        var c = await CriarContatoAsync(db, amb.Cenario, "Comprou duas vezes");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 100m, default);
        await amb.Contatos.ReabrirAsync(c.Id, default);
        await amb.Contatos.MarcarGanhoAsync(c.Id, 200m, default);
        db.ChangeTracker.Clear();

        var etapaGanho = await db.EtapasFunil.AsNoTracking().FirstAsync(e => e.EGanho);
        var pagina = await amb.Funil.ColunaAsync(etapaGanho.Id, null, null, 50, default);

        var card = Assert.Single(pagina.Itens, x => x.Id == c.Id);
        Assert.Equal(2, card.VendasEmAberto);
    }

    [Fact]
    public async Task A_coluna_de_ganho_mostra_QUANTAS_JA_FORAM_CONCLUIDAS()
    {
        // "2 em aberto · 41 concluídas": o vendedor precisa ver que o histórico não sumiu.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-cabecalho");
        using var _ = db; using var __ = tx;

        var a = await CriarContatoAsync(db, amb.Cenario, "Fica");
        var b = await CriarContatoAsync(db, amb.Cenario, "Conclui");
        await amb.Contatos.MarcarGanhoAsync(a.Id, 100m, default);
        await amb.Contatos.MarcarGanhoAsync(b.Id, 200m, default);

        db.ChangeTracker.Clear();
        var vb = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == b.Id);
        await amb.Vendas.ConcluirAsync([vb.Id], default);
        db.ChangeTracker.Clear();

        var etapaGanho = await db.EtapasFunil.AsNoTracking().FirstAsync(e => e.EGanho);
        var coluna = (await amb.Funil.QuadroAsync(50, default))
            .Colunas.Single(x => x.EtapaId == etapaGanho.Id);

        Assert.Equal(1, coluna.Total);
        Assert.Equal(1, coluna.Concluidas);
    }

    // ==================================================================== automação
    /// <summary>===================== SEM ISTO O BLOCO NÃO RESOLVE NADA =====================
    ///
    /// O botão sozinho não basta: vendedor não gosta de tarefa administrativa, e em três meses a
    /// coluna volta a acumular. A rodada diária é o que mantém o quadro limpo sem ninguém
    /// lembrar dela.
    ///
    /// E o AUTOR precisa ser o sistema — `concluida_por` NULL. Carimbar um usuário aqui
    /// produziria autoria falsa: alguém apareceria como autor de uma ação que não tomou.
    /// ============================================================================</summary>
    [Fact]
    public async Task A_RODADA_DIARIA_CONCLUI_O_QUE_PASSOU_DO_PRAZO_com_autor_sistema()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-rodada");
        using var _ = db; using var __ = tx;

        var antiga = await CriarContatoAsync(db, amb.Cenario, "Comprou faz tempo");
        var recente = await CriarContatoAsync(db, amb.Cenario, "Comprou hoje");
        await amb.Contatos.MarcarGanhoAsync(antiga.Id, 100m, default);
        await amb.Contatos.MarcarGanhoAsync(recente.Id, 200m, default);

        db.ChangeTracker.Clear();
        var vAntiga = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == antiga.Id);

        // Empurra UMA delas para além do prazo padrão de 7 dias. As duas nasceram no mesmo
        // instante do relógio congelado, então mexer na data é o que separa os dois casos.
        await db.Vendas.Where(v => v.Id == vAntiga.Id).ExecuteUpdateAsync(s => s
            .SetProperty(v => v.FechadaEm, ContatosDbTests.Agora.UtcDateTime.AddDays(-30)), default);
        db.ChangeTracker.Clear();

        var quantas = await ConclusaoAutomatica.ExecutarAsync(db, amb.Relogio, default);

        Assert.Equal(1, quantas);

        db.ChangeTracker.Clear();
        var depoisAntiga = await db.Vendas.AsNoTracking().SingleAsync(v => v.Id == vAntiga.Id);
        Assert.Equal(StatusVenda.Concluida, depoisAntiga.Status);
        // ⚠️ NINGUÉM CLICOU. `concluida_por` NULL é o que distingue a rodada da pessoa.
        Assert.Null(depoisAntiga.ConcluidaPor);
        Assert.NotNull(depoisAntiga.ConcluidaEm);

        // Concluir NÃO é cancelar: a linha continua limpa e o valor intacto.
        Assert.Null(depoisAntiga.CanceladaEm);
        Assert.Equal(100m, depoisAntiga.Valor);

        // A recente NÃO foi tocada: ela ainda está dentro do prazo.
        var depoisRecente = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == recente.Id);
        Assert.Equal(StatusVenda.Fechada, depoisRecente.Status);

        // 200, e não 300: a venda antiga foi empurrada 30 dias para trás pelo próprio setup, e o
        // dashboard é do MÊS corrente. Que ela continue contando no mês DELA é o que o
        // `CONCLUIR_MANTEM_O_FATURAMENTO_MAS_CANCELAR_TIRA` já prova.
        Assert.Equal(200m, (await amb.Dashboard.DashboardAsync(default)).FaturamentoDoMes);

        // A trilha registra o evento com ator SISTEMA, não com um usuário inventado.
        var evento = await db.Auditoria.AsNoTracking()
            .SingleAsync(a => a.Entidade == EntidadeAuditada.Venda
                           && a.EntidadeId == vAntiga.Id
                           && a.Acao == AcaoAuditoria.Concluiu);
        Assert.Equal(AtorAuditoria.Sistema, evento.Ator);
        Assert.Null(evento.UsuarioId);
    }

    [Fact]
    public async Task DIAS_ZERO_conclui_na_hora_sem_esperar_a_rodada()
    {
        // Padaria, salão, loja de balcão: a venda nasce e termina no mesmo atendimento. Deixar
        // para a rodada manteria o card na coluna até as 8h do dia seguinte.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "neg2-dias-zero");
        using var _ = db; using var __ = tx;

        await db.Empresas.Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DiasParaConcluirVenda, (short)0), default);
        db.ChangeTracker.Clear();

        var c = await CriarContatoAsync(db, amb.Cenario, "Balcão");
        await amb.Contatos.MarcarGanhoAsync(c.Id, 50m, default);

        db.ChangeTracker.Clear();
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == c.Id);

        // Já nasce concluída — nenhuma rodada rodou entre o ganho e esta linha.
        Assert.Equal(StatusVenda.Concluida, venda.Status);
        Assert.Null(venda.ConcluidaPor);   // foi a regra da empresa, não uma pessoa

        // E o card não fica na coluna de ganho nem por um instante.
        var etapaGanho = await db.EtapasFunil.AsNoTracking().FirstAsync(e => e.EGanho);
        Assert.Equal(0, (await amb.Funil.QuadroAsync(50, default))
            .Colunas.Single(x => x.EtapaId == etapaGanho.Id).Total);

        // O dinheiro, esse, continua contando.
        Assert.Equal(50m, (await amb.Dashboard.DashboardAsync(default)).FaturamentoDoMes);
    }

    // ==================================================================== apoio
    /// <summary>Estoura `numeric(14,2)`: o banco recusa no INSERT da venda, DEPOIS de o carimbo
    /// do contato já ter sido escrito na mesma transação.</summary>
    private const decimal ValorQueOBancoRecusa = 999_999_999_999.99m + 1m;

    private static async Task<Contato> CriarContatoAsync(NexoraDbContext db, Cenario c, string nome)
    {
        var contato = new Contato
        {
            EmpresaId = c.Id, Nome = nome,
            Telefone = $"5584 9{Random.Shared.Next(1000, 9999)}{Random.Shared.Next(1000, 9999)}",
            EtapaId = c.PrimeiraEtapa.Id
        };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return contato;
    }
}
