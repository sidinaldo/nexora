using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== OS NÚMEROS DO PAINEL INICIAL (AUD-1) =====================
///
/// Dois defeitos, e os dois eram do mesmo tipo — número que a tela tinha de "ajeitar":
///
///   · a linha "Todos" do cartão de funis e a rosca das origens eram SOMADAS e AGRUPADAS no
///     navegador. Agora chegam prontas;
///   · o vendedor via o faturamento, a conversão e o funil da EMPRESA INTEIRA. A permissão
///     `ver_numeros_da_equipe` diz "sem esta, cada um vê só o seu", e o dashboard não aplicava.
///
/// Toda data é de agosto/2026, o mês do relógio dos testes.
/// =========================================================================================</summary>
[Collection("banco")]
public class PainelInicialDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    /// <summary>Segunda, 03/08, 15h UTC: dentro do mês do relógio.</summary>
    private static readonly DateTime NoMes = new(2026, 8, 3, 15, 0, 0, DateTimeKind.Utc);

    // ============================================================ os totais
    [Fact]
    public async Task OS_TOTAIS_DO_FUNIL_VEM_PRONTOS_E_SOMAM_OS_DOIS_FUNIS()
    {
        var (db, tx, amb) = await PrepararAsync("totais");
        using var _ = db; using var __ = tx;

        var (_, etapasDoSegundo) = await Semeador.SegundoFunilAsync(db, amb.Cenario);

        await NegocioAsync(db, amb, "a", amb.Cenario.PrimeiraEtapa, valor: 100m);
        await NegocioAsync(db, amb, "b", etapasDoSegundo[0], valor: 250m);

        var d = await Painel(db, amb).DashboardAsync(default);

        Assert.Equal(2, d.TotalEmNegociacao);
        Assert.Equal(350m, d.TotalValorEmAberto);
        // E a linha "Todos" fecha com as linhas de cima — era essa a soma que a tela fazia.
        Assert.Equal(d.Funil.Sum(f => f.EmNegociacao), d.TotalEmNegociacao);
        Assert.Equal(d.Funil.Sum(f => f.ValorEmAberto), d.TotalValorEmAberto);
    }

    [Fact]
    public async Task A_ROSCA_CHEGA_AGRUPADA_COM_O_TOTAL_E_OS_PERCENTUAIS()
    {
        var (db, tx, amb) = await PrepararAsync("rosca");
        using var _ = db; using var __ = tx;

        await NegocioAsync(db, amb, "i1", amb.Cenario.PrimeiraEtapa, origem: OrigemLead.Instagram);
        await NegocioAsync(db, amb, "i2", amb.Cenario.PrimeiraEtapa, origem: OrigemLead.Instagram);
        await NegocioAsync(db, amb, "w1", amb.Cenario.PrimeiraEtapa, origem: OrigemLead.Whatsapp);

        var d = await Painel(db, amb).DashboardAsync(default);

        Assert.Equal(3, d.LeadsTotal);
        Assert.Equal(["instagram", "whatsapp"], d.Origens.Select(o => o.Origem));
        Assert.Equal([66.67m, 33.33m], d.Origens.Select(o => o.Percentual));
        Assert.Equal(100m, d.Origens.Sum(o => o.Percentual));
    }

    // ============================================================ cada um vê o seu
    /// <summary>⚠️ O VAZAMENTO. O vendedor abria o painel e via o faturamento do mês da empresa, a
    /// conversão de todo mundo e o funil inteiro — números que Relatórios e Evolução já recortavam.
    /// Aqui o mesmo cenário é lido duas vezes: como dono, tudo; como vendedor, só o dele.</summary>
    [Fact]
    public async Task QUEM_NAO_VE_A_EQUIPE_VE_SO_OS_PROPRIOS_NUMEROS()
    {
        var (db, tx, amb) = await PrepararAsync("recorte");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb);
        var dono = amb.Cenario.Dono.Id;

        // Do vendedor: uma venda de 100 no mês e um negócio aberto, num lead do Instagram.
        await NegocioAsync(db, amb, "v-ganho", amb.Cenario.Etapas[^1], valor: 100m,
            responsavel: vendedor, ganhaEm: NoMes, origem: OrigemLead.Instagram);
        await NegocioAsync(db, amb, "v-aberto", amb.Cenario.PrimeiraEtapa, valor: 40m,
            responsavel: vendedor, origem: OrigemLead.Instagram);

        // Do dono: uma venda de 300 e uma perda no mês, em leads do WhatsApp.
        await NegocioAsync(db, amb, "d-ganho", amb.Cenario.Etapas[^1], valor: 300m,
            responsavel: dono, ganhaEm: NoMes);
        await NegocioAsync(db, amb, "d-perda", amb.Cenario.PrimeiraEtapa,
            responsavel: dono, perdidaEm: NoMes);

        var daEmpresa = await Painel(db, amb).DashboardAsync(default);

        Assert.Equal(2, daEmpresa.VendasDoMes);
        Assert.Equal(400m, daEmpresa.FaturamentoDoMes);
        Assert.Equal(66.67m, daEmpresa.TaxaConversaoPercentual);
        Assert.Equal(4, daEmpresa.LeadsTotal);

        amb.Contexto.UsuarioId = vendedor;
        amb.Contexto.Papel = "vendedor";
        var doVendedor = await Painel(db, amb).DashboardAsync(default);

        Assert.Equal(1, doVendedor.VendasDoMes);
        Assert.Equal(100m, doVendedor.FaturamentoDoMes);
        Assert.Equal(100m, doVendedor.TaxaConversaoPercentual);   // a perda é do dono
        // O ganho fica no quadro até o pedido ser concluído: dois cards dele, 100 + 40.
        Assert.Equal(2, doVendedor.TotalEmNegociacao);
        Assert.Equal(140m, doVendedor.TotalValorEmAberto);
        Assert.Equal(2, doVendedor.LeadsTotal);
        Assert.Equal(["instagram"], doVendedor.Origens.Select(o => o.Origem));
    }

    /// <summary>Isolamento de tenant: a venda de outra empresa no mesmo mês não entra.</summary>
    [Fact]
    public async Task OS_NUMEROS_DE_OUTRA_EMPRESA_NAO_ENTRAM()
    {
        var (db, tx, amb) = await PrepararAsync("isolado");
        using var _ = db; using var __ = tx;

        var outra = await Semeador.TenantAsync(db, "painel-outra");
        var contato = new Contato
        {
            EmpresaId = outra.Id, Nome = "Invasor", Telefone = $"5584{Random.Shared.NextInt64(900000000, 999999999)}"
        };
        db.Contatos.Add(contato);
        var negocio = Semeador.Negocio(contato, outra.Etapas[^1], valor: 9_999m);
        negocio.Status = StatusNegociacao.Ganha;
        negocio.GanhaEm = NoMes;
        db.Negociacoes.Add(negocio);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var d = await Painel(db, amb).DashboardAsync(default);

        Assert.Equal(0, d.VendasDoMes);
        Assert.Equal(0m, d.FaturamentoDoMes);
        Assert.Null(d.TaxaConversaoPercentual);
        Assert.Equal(0, d.LeadsTotal);
    }

    // ============================================================ o "hoje" da empresa
    /// <summary>⚠️ 22H30 EM BRASÍLIA JÁ É O DIA SEGUINTE EM UTC. Os relatórios e o gráfico do
    /// dashboard usavam `DateTime.UtcNow` como data final padrão, e pediam "até amanhã" a noite
    /// inteira. O "hoje" da empresa é o dia de Brasília.</summary>
    [Fact]
    public async Task O_HOJE_DA_EMPRESA_E_O_DIA_DE_BRASILIA_MESMO_DEPOIS_DAS_21H()
    {
        var (db, tx, amb) = await PrepararAsync("hoje");
        using var _ = db; using var __ = tx;

        // 07/08 01h30 UTC = 06/08 22h30 em Brasília.
        var relogio = new RelogioFalso(new DateTimeOffset(2026, 8, 7, 1, 30, 0, TimeSpan.Zero));

        var hoje = await new HojeDaEmpresa(db, relogio).HojeAsync(default);

        Assert.Equal(new DateOnly(2026, 8, 6), hoje);
    }

    // ============================================================ apoio
    private sealed record Ambiente(Cenario Cenario, ContextoMutavel Contexto, RelogioFalso Relogio);

    private static ServicoDashboard Painel(NexoraDbContext db, Ambiente amb) =>
        new(db, amb.Relogio, amb.Contexto);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"painel-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        // O Semeador deixa um contato com negócio, conversa e mensagem. Estes testes CONTAM, e o
        // resto entraria na conta.
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == cenario.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        return (db, tx, new Ambiente(cenario, ctx, relogio));
    }

    private static async Task NegocioAsync(
        NexoraDbContext db, Ambiente amb, string marca, EtapaFunil etapa,
        decimal? valor = null, long? responsavel = null, DateTime? ganhaEm = null,
        DateTime? perdidaEm = null, OrigemLead origem = OrigemLead.Whatsapp)
    {
        var contato = new Contato
        {
            EmpresaId = amb.Cenario.Id,
            Nome = $"Contato {marca}",
            Telefone = $"5584{Random.Shared.NextInt64(900000000, 999999999)}",
            ResponsavelId = responsavel,
            Origem = origem
        };
        db.Contatos.Add(contato);

        var negocio = Semeador.Negocio(contato, etapa, valor: valor);
        if (ganhaEm is not null)
        {
            negocio.Status = StatusNegociacao.Ganha;
            negocio.GanhaEm = ganhaEm;
        }
        if (perdidaEm is not null)
        {
            negocio.Status = StatusNegociacao.Perdida;
            negocio.PerdidaEm = perdidaEm;
        }
        db.Negociacoes.Add(negocio);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<long> VendedorAsync(NexoraDbContext db, Ambiente amb)
    {
        var u = new Usuario
        {
            EmpresaId = amb.Cenario.Id, Nome = "Vendedora",
            Email = $"vendedora-{Guid.NewGuid():N}@exemplo.com",
            SenhaHash = Nexora.Core.Seguranca.HashSenha.Gerar("senha-de-teste-123"),
            Papel = PapelUsuario.Vendedor, Status = StatusUsuario.Ativo
        };
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return u.Id;
    }
}
