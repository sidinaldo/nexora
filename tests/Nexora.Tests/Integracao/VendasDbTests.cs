using Microsoft.EntityFrameworkCore;
using Nexora.Core;
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
