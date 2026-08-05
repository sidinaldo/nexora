using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>A semente de DESENVOLVIMENTO (`POST /api/dev/semear`), que popula o tenant logado.
///
/// Não confundir com o seed de DEMONSTRAÇÃO: aquele cria um tenant próprio e tem as três
/// barreiras contra envio real. Este enche o tenant de quem chamou, para se avaliar as telas.</summary>
[Collection("banco")]
public class SementeDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task SEMEAR_DUAS_VEZES_NO_MESMO_TENANT_NAO_DUPLICA()
    {
        var (db, tx, semente, _) = await PrepararAsync("mesmo-tenant");
        using var _1 = db; using var _2 = tx;

        var primeira = await semente.SemearAsync(default);
        var segunda = await semente.SemearAsync(default);

        Assert.Equal(primeira.Contatos, segunda.Contatos);
        Assert.Equal(primeira.Usuarios, segunda.Usuarios);

        // O `LimparAsync` roda antes de semear, então a contagem no banco é a de UMA execução.
        db.ChangeTracker.Clear();
        Assert.Equal(segunda.Usuarios, await db.Usuarios
            .CountAsync(u => u.Email.EndsWith("@semente.dev")));
    }

    [Fact]
    public async Task SEMEAR_UM_SEGUNDO_TENANT_NAO_COLIDE_COM_O_PRIMEIRO()
    {
        // ===================== O BUG QUE ISTO CONSERTA =====================
        // `uq_usuarios_email` é GLOBAL, não por tenant. Com o e-mail fixo (`beatriz@semente.dev`),
        // a semente rodava uma vez por BANCO: o segundo tenant colidia com o primeiro e o comando
        // estourava com violação de unicidade — 500 na cara de quem chamou.
        //
        // Reexecutar no MESMO tenant sempre funcionou (o `LimparAsync` roda antes, recortado pelo
        // query filter), e é por isso que ninguém tinha percebido.
        // ==================================================================
        var (db, tx, semente, ctx) = await PrepararAsync("tenant-a");
        using var _1 = db; using var _2 = tx;

        var primeiro = await semente.SemearAsync(default);

        // Segundo tenant, MESMO banco.
        var outro = await Semeador.TenantAsync(db, "semente-tenant-b");
        ctx.EmpresaId = outro.Id;
        ctx.UsuarioId = outro.Dono.Id;
        db.ChangeTracker.Clear();

        var segundo = await semente.SemearAsync(default);   // antes: violação de unicidade

        Assert.Equal(primeiro.Usuarios, segundo.Usuarios);

        // Os dois tenants coexistem, cada um com a sua equipe.
        db.ChangeTracker.Clear();
        var todos = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Email.EndsWith("@semente.dev"))
            .Select(u => new { u.EmpresaId, u.Email })
            .ToListAsync();

        Assert.Equal(primeiro.Usuarios + segundo.Usuarios, todos.Count);
        // E nenhum e-mail se repete — que é exatamente o que o índice único exige.
        Assert.Equal(todos.Count, todos.Select(u => u.Email).Distinct().Count());
    }

    [Fact]
    public async Task Limpar_apaga_so_o_que_foi_semeado()
    {
        // Contato digitado à mão não tem a marca e precisa sobreviver: quem semeia em cima do
        // próprio tenant de desenvolvimento perderia o dado que estava testando.
        var (db, tx, semente, _) = await PrepararAsync("limpar");
        using var _1 = db; using var _2 = tx;

        await semente.SemearAsync(default);

        var meu = new Contato
        {
            EmpresaId = (await db.Empresas.AsNoTracking().FirstAsync()).Id,
            Nome = "Cliente que eu cadastrei",
            Telefone = "5584911112222",
            EtapaId = (await db.EtapasFunil.AsNoTracking().FirstAsync()).Id,
            OrdemKanban = 1m
        };
        db.Contatos.Add(meu);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await semente.LimparAsync(default);

        db.ChangeTracker.Clear();
        Assert.True(await db.Contatos.AnyAsync(c => c.Id == meu.Id));
        Assert.False(await db.Contatos.AnyAsync(c => c.OrigemDetalhe == ServicoSemente.Marca));
    }

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx,
                        IServicoSemente Semente, ContextoMutavel Ctx)>
        PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"semente-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new ServicoSemente(db, ctx, relogio), ctx);
    }
}
