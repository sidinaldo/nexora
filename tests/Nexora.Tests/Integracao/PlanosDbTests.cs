using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Persistencia.Migrations;

namespace Nexora.Tests.Integracao;

/// <summary>OPE-1 — o catálogo de planos e o teto de pessoas.
///
/// ===================== O QUE ESTE ARQUIVO PROTEGE =====================
/// Quatro coisas, e três delas falham em silêncio:
///
///   1. o BACKFILL da migration. É o único passo do bloco que pode machucar um cliente que já
///      existe: um `DEFAULT 3` seco põe qualquer empresa de 4 pessoas acima da cota no instante da
///      migration, e o sintoma aparece dias depois, no convite seguinte do dono;
///   2. a regra de VAGA — contam ativo e convidado, inativo não. Ela existe desde que o status
///      virou enum de três valores, e o backfill é o primeiro código a depender dela;
///   3. `planos` é a ÚNICA tabela sem filtro de tenant. Isso é exceção deliberada, e está testado
///      aqui para quem ler "toda tabela é filtrada" encontrar a que não é, no lugar onde procura;
///   4. o nome do plano é único SEM OLHAR CAIXA. "Pro" e "pro" são o mesmo plano para quem lê.
/// ======================================================================
///
/// ⚠️ O TESTE DO BACKFILL EXECUTA A CONSTANTE DA MIGRATION, não uma cópia dela. Backfill testado
/// por cópia é backfill não testado: a cópia pode estar certa enquanto a migration está errada, e o
/// teste fica verde contando uma verdade sobre o arquivo errado.</summary>
[Collection("banco")]
public class PlanosDbTests(BancoTeste banco)
{
    // ==================================================================== o backfill

    [Fact]
    public async Task O_BACKFILL_DA_A_EMPRESA_PELO_MENOS_O_QUE_ELA_JA_USA()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var c = await Semeador.TenantAsync(db, "backfill-5");
        ctx.EmpresaId = c.Id;

        // O Semeador já deixa o dono. Mais quatro pessoas: 5 vagas ocupadas ao todo.
        await AdicionarAsync(db, c.Id, "b5-a", StatusUsuario.Ativo);
        await AdicionarAsync(db, c.Id, "b5-b", StatusUsuario.Ativo);
        await AdicionarAsync(db, c.Id, "b5-c", StatusUsuario.Convidado);
        await AdicionarAsync(db, c.Id, "b5-d", StatusUsuario.Convidado);

        await DefinirLimiteAsync(db, c.Id, 3);   // o estado em que a migration encontraria a linha
        await db.Database.ExecuteSqlRawAsync(PlanosELimiteDeUsuarios.Backfill);

        // 5, e não 3: sem isto o dono desta empresa levaria um 409 no convite seguinte, por uma
        // migration que rodou dias antes e que ninguém ligaria ao erro.
        Assert.Equal((short)5, await LimiteAsync(db, c.Id));
    }

    [Fact]
    public async Task O_BACKFILL_NAO_DESCE_ABAIXO_DE_TRES()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var c = await Semeador.TenantAsync(db, "backfill-piso");
        ctx.EmpresaId = c.Id;

        // Só o dono. O `GREATEST(3, …)` é o que impede uma empresa nova de nascer com teto 1 e o
        // dono não conseguir convidar ninguém.
        await DefinirLimiteAsync(db, c.Id, 1);
        await db.Database.ExecuteSqlRawAsync(PlanosELimiteDeUsuarios.Backfill);

        Assert.Equal((short)3, await LimiteAsync(db, c.Id));
    }

    [Fact]
    public async Task INATIVO_NAO_OCUPA_VAGA_NO_BACKFILL_E_CONVIDADO_OCUPA()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var c = await Semeador.TenantAsync(db, "backfill-status");
        ctx.EmpresaId = c.Id;

        // dono (ativo) + 1 convidado = 2 vagas. Os três inativos não contam — e é por isso que
        // desativar alguém é a saída que o dono tem quando bate no teto.
        await AdicionarAsync(db, c.Id, "bs-conv", StatusUsuario.Convidado);
        await AdicionarAsync(db, c.Id, "bs-in1", StatusUsuario.Inativo);
        await AdicionarAsync(db, c.Id, "bs-in2", StatusUsuario.Inativo);
        await AdicionarAsync(db, c.Id, "bs-in3", StatusUsuario.Inativo);

        await DefinirLimiteAsync(db, c.Id, 1);
        await db.Database.ExecuteSqlRawAsync(PlanosELimiteDeUsuarios.Backfill);

        // 3 (o piso), e não 5: se inativo contasse, a empresa ganharia teto por gente desligada.
        Assert.Equal((short)3, await LimiteAsync(db, c.Id));
    }

    // ==================================================================== os limites do CHECK

    [Theory]
    [InlineData(0)]    // piso: toda empresa tem ao menos o dono, e 0 tornaria a linha dele ilegal
    [InlineData(51)]   // teto: freio de digitação, igual ao 20 de conexões
    public async Task O_BANCO_RECUSA_LIMITE_DE_USUARIOS_FORA_DA_FAIXA(int valor)
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var c = await Semeador.TenantAsync(db, $"check-{valor}");
        ctx.EmpresaId = c.Id;

        // ⚠️ `PostgresException`, e nao `DbUpdateException`: o `ExecuteUpdateAsync` nao passa pelo
        // change tracker, entao o EF nao embrulha o erro do banco. Afirmar so "alguma excecao"
        // deixaria este teste passar com um erro de digitacao no SQL -- dai o SQLSTATE e o NOME da
        // restricao, que e o que prova que foi ESTE CHECK que barrou.
        var erro = await Assert.ThrowsAsync<PostgresException>(
            () => DefinirLimiteAsync(db, c.Id, (short)valor));

        Assert.Equal("23514", erro.SqlState);
        Assert.Equal("ck_empresas_limite_usuarios", erro.ConstraintName);
    }

    // ==================================================================== o catálogo

    [Fact]
    public async Task PLANOS_E_A_UNICA_TABELA_SEM_FILTRO_DE_TENANT()
    {
        // ===================== EXCEÇÃO DELIBERADA, REGISTRADA AQUI =====================
        // Toda entidade deste sistema tem `HasQueryFilter` por `empresa_id`. `planos` não tem, e
        // não é esquecimento: é catálogo, igual para todo mundo, e só o operador escreve nele. Uma
        // sessão de cliente enxerga a lista inteira — aceitável, porque nome e preço de plano é o
        // que uma página de vendas publica.
        //
        // ⚠️ O QUE TORNARIA ISTO UM VAZAMENTO é uma coluna POR CLIENTE aqui: desconto negociado,
        // número de contrato, observação do comercial. Se este teste um dia precisar mudar, é
        // porque alguém pôs dado de um cliente numa tabela que todos leem.
        // ===============================================================================
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var c = await Semeador.TenantAsync(db, "plano-sem-filtro");
        db.Planos.Add(new Plano { Nome = "Catalogo Visivel", Preco = 99.90m });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        ctx.EmpresaId = c.Id;
        var vistos = await db.Planos.AsNoTracking().ToListAsync();

        Assert.Contains(vistos, p => p.Nome == "Catalogo Visivel");
    }

    [Fact]
    public async Task O_NOME_DO_PLANO_E_UNICO_SEM_OLHAR_CAIXA()
    {
        using var db = banco.NovoContexto(new ContextoMutavel());
        using var tx = await db.Database.BeginTransactionAsync();

        db.Planos.Add(new Plano { Nome = "Profissional", Preco = 199m });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // "profissional" tem de bater no índice funcional sobre `lower(nome)`. Sem ele, dois planos
        // com o mesmo nome tornam a lista indecifrável no dia em que alguém precisar saber em qual
        // deles a empresa está.
        db.Planos.Add(new Plano { Nome = "profissional", Preco = 199m });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task PLANO_EM_USO_NAO_PODE_SER_APAGADO()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var c = await Semeador.TenantAsync(db, "plano-restrict");

        var plano = new Plano { Nome = "Em Uso", Preco = 149m };
        db.Planos.Add(plano);
        await db.SaveChangesAsync();

        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == c.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PlanoId, plano.Id));
        db.ChangeTracker.Clear();

        // A FK é RESTRICT. O banco recusa mesmo um DELETE manual — e tem de recusar, porque
        // `empresas.plano_id` é o único traço do que foi vendido: uma empresa cujo plano sumiu fica
        // com limites sem explicação.
        db.Planos.Remove(await db.Planos.FirstAsync(p => p.Id == plano.Id));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ==================================================================== auxiliares

    private static async Task AdicionarAsync(
        NexoraDbContext db, long empresaId, string marca, StatusUsuario status)
    {
        db.Usuarios.Add(new Usuario
        {
            EmpresaId = empresaId,
            Nome = marca,
            Email = $"{marca}@teste.local",
            Papel = PapelUsuario.Vendedor,
            Status = status,
            // `ck_usuarios_senha`: ou está convidado, ou tem hash.
            SenhaHash = status == StatusUsuario.Convidado ? null : "pbkdf2$1$x$y"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task DefinirLimiteAsync(NexoraDbContext db, long empresaId, short limite)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == empresaId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LimiteUsuarios, limite));
        db.ChangeTracker.Clear();
    }

    private static async Task<short> LimiteAsync(NexoraDbContext db, long empresaId) =>
        await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == empresaId).Select(e => e.LimiteUsuarios).FirstAsync();
}
