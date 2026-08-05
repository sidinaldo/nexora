using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;

namespace Nexora.Tests.Integracao;

/// <summary>O teste mais importante do bloco 1. O isolamento multi-tenant inteiro depende de
/// UM mecanismo — o HasQueryFilter global — e ele falha do jeito mais perigoso possivel: em
/// silencio, devolvendo dados demais ou de menos, sem erro nenhum.
///
/// Nao da para testar isso em provider in-memory: query filter combinado com o SQL real,
/// indice funcional e check constraint so existem no Postgres.</summary>
[Collection("banco")]
public class IsolamentoTenantDbTests(BancoTeste banco)
{
    [Fact]
    public async Task Usuario_do_tenant_A_nao_enxerga_usuario_do_tenant_B()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();   // sempre revertida

        var (empresaA, empresaB) = await SemearDoisTenantsAsync(db);

        // ---- olhando como a empresa A ----
        ctx.EmpresaId = empresaA.Id;
        var vistosPorA = await db.Usuarios.AsNoTracking().ToListAsync();

        Assert.Single(vistosPorA);
        Assert.Equal("ana@empresa-a.com", vistosPorA[0].Email);
        Assert.Equal(empresaA.Id, vistosPorA[0].EmpresaId);

        // ---- olhando como a empresa B ----
        ctx.EmpresaId = empresaB.Id;
        var vistosPorB = await db.Usuarios.AsNoTracking().ToListAsync();

        Assert.Single(vistosPorB);
        Assert.Equal("bruno@empresa-b.com", vistosPorB[0].Email);

        // Buscar pelo id do OUTRO tenant nao devolve nada — nem erro, nem a linha.
        // E exatamente por ser silencioso que este teste existe.
        var idDeB = vistosPorB[0].Id;
        ctx.EmpresaId = empresaA.Id;
        Assert.Null(await db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Id == idDeB));
    }

    [Fact]
    public async Task Empresa_so_enxerga_a_si_mesma()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var (empresaA, _) = await SemearDoisTenantsAsync(db);

        ctx.EmpresaId = empresaA.Id;
        var empresas = await db.Empresas.AsNoTracking().ToListAsync();

        Assert.Single(empresas);
        Assert.Equal(empresaA.Id, empresas[0].Id);
    }

    [Fact]
    public async Task Sem_tenant_no_contexto_a_consulta_volta_vazia_em_silencio()
    {
        // Documenta a armadilha em forma de teste: EmpresaId = 0 (login, webhook, job) NAO
        // levanta erro — devolve zero linhas. Quem esquecer o IgnoreQueryFilters nesses
        // caminhos vai depurar "o banco esta vazio" por horas.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        await SemearDoisTenantsAsync(db);

        ctx.EmpresaId = 0;
        Assert.Empty(await db.Usuarios.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Empresas.AsNoTracking().ToListAsync());

        // E o antidoto: IgnoreQueryFilters + filtro explicito.
        var deA = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Email == "ana@empresa-a.com").ToListAsync();
        Assert.Single(deA);
    }

    [Fact]
    public async Task Interceptor_carimba_criado_em_e_atualizado_em_sem_o_servico_pedir()
    {
        var relogio = new RelogioFalso(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx, relogio);
        using var tx = await db.Database.BeginTransactionAsync();

        // Repare: nenhuma atribuicao de CriadoEm/AtualizadoEm aqui.
        var empresa = new Empresa { Nome = "Sem carimbo manual" };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        Assert.Equal(relogio.GetUtcNow().UtcDateTime, empresa.CriadoEm);
        Assert.Equal(relogio.GetUtcNow().UtcDateTime, empresa.AtualizadoEm);

        relogio.Avancar(TimeSpan.FromHours(3));
        empresa.Nome = "Renomeada";
        await db.SaveChangesAsync();

        Assert.Equal(relogio.GetUtcNow().UtcDateTime, empresa.AtualizadoEm);
        // CriadoEm nao pode andar junto.
        Assert.NotEqual(empresa.CriadoEm, empresa.AtualizadoEm);
    }

    /// <summary>Duas empresas, um usuario em cada. Inserido com EmpresaId 0 no contexto — o
    /// filtro global nao interfere em INSERT, so em leitura.</summary>
    private static async Task<(Empresa A, Empresa B)> SemearDoisTenantsAsync(
        Nexora.Infra.Persistencia.NexoraDbContext db)
    {
        var a = new Empresa { Nome = "Empresa A" };
        var b = new Empresa { Nome = "Empresa B" };
        db.Empresas.AddRange(a, b);
        await db.SaveChangesAsync();

        db.Usuarios.AddRange(
            new Usuario
            {
                EmpresaId = a.Id, Nome = "Ana", Email = "ana@empresa-a.com",
                SenhaHash = HashSenha.Gerar("senha-da-ana"),
                Papel = PapelUsuario.Dono, Status = StatusUsuario.Ativo
            },
            new Usuario
            {
                EmpresaId = b.Id, Nome = "Bruno", Email = "bruno@empresa-b.com",
                SenhaHash = HashSenha.Gerar("senha-do-bruno"),
                Papel = PapelUsuario.Dono, Status = StatusUsuario.Ativo
            });
        await db.SaveChangesAsync();

        return (a, b);
    }
}
