using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>A LIGAÇÃO entre contato e etiqueta.
///
/// ===================== O QUE ESTE ARQUIVO PROVA =====================
/// A tabela não tem serviço ainda — ninguém a consome. O que ela tem é um conjunto de garantias
/// que moram no BANCO, e é isso que está testado aqui: a chave composta, as duas cascatas e as FKs
/// compostas com `empresa_id`.
///
/// São garantias que nenhum código de aplicação pode afrouxar por engano, e é por isso que elas
/// estão no banco em vez de numa validação. Mas só um teste que escreve DIRETO na tabela, passando
/// por cima de qualquer serviço, prova que elas estão mesmo lá.
/// ====================================================================</summary>
[Collection("banco")]
public class ContatosEtiquetasDbTests(BancoTeste banco)
{
    // ==================================================================== a chave
    [Fact]
    public async Task A_MESMA_ETIQUETA_NAO_COLA_DUAS_VEZES_NO_MESMO_CONTATO()
    {
        // A PK composta (contato_id, etiqueta_id) É a regra de unicidade — não há índice extra
        // para isso, e não deve haver. Mesma escolha de `feriados_ignorados`.
        var (db, tx, c) = await PrepararAsync("duplicada");
        using var _1 = db; using var _2 = tx;

        var etiqueta = await NovaEtiquetaAsync(db, c, "Revendedor");

        db.ContatosEtiquetas.Add(Marcacao(c, c.Contato.Id, etiqueta.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.ContatosEtiquetas.Add(Marcacao(c, c.Contato.Id, etiqueta.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task O_MESMO_CONTATO_RECEBE_ETIQUETAS_DIFERENTES()
    {
        var (db, tx, c) = await PrepararAsync("varias");
        using var _1 = db; using var _2 = tx;

        var a = await NovaEtiquetaAsync(db, c, "Revendedor");
        var b = await NovaEtiquetaAsync(db, c, "VIP");

        db.ContatosEtiquetas.Add(Marcacao(c, c.Contato.Id, a.Id));
        db.ContatosEtiquetas.Add(Marcacao(c, c.Contato.Id, b.Id));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.ContatosEtiquetas.CountAsync(x => x.ContatoId == c.Contato.Id));
    }

    // ==================================================================== cascata
    /// <summary>⚠️ ESTE É O TESTE QUE O `Cascade` EXISTE PARA PASSAR.
    ///
    /// `ServicoEtiquetas.RemoverAsync` é um `db.Etiquetas.Remove` seco, sem perguntar destino —
    /// diferente de `etapas_funil`, que é `RESTRICT` e exige para onde mandar os contatos. Com
    /// `Restrict` aqui, apagar uma etiqueta em uso viraria 500 no rosto do dono, e o único jeito
    /// de descobrir seria um cliente tentando.</summary>
    [Fact]
    public async Task APAGAR_A_ETIQUETA_SOME_COM_AS_MARCACOES()
    {
        var (db, tx, c) = await PrepararAsync("cascata-etiqueta");
        using var _1 = db; using var _2 = tx;

        var etiqueta = await NovaEtiquetaAsync(db, c, "Descartável");
        db.ContatosEtiquetas.Add(Marcacao(c, c.Contato.Id, etiqueta.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Etiquetas.Remove(await db.Etiquetas.SingleAsync(e => e.Id == etiqueta.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await db.ContatosEtiquetas.ToListAsync());

        // E o CONTATO continua lá: apagar vocabulário nunca pode apagar pessoa.
        Assert.True(await db.Contatos.AnyAsync(x => x.Id == c.Contato.Id));
    }

    [Fact]
    public async Task APAGAR_O_CONTATO_SOME_COM_AS_MARCACOES_E_NAO_COM_A_ETIQUETA()
    {
        var (db, tx, c) = await PrepararAsync("cascata-contato");
        using var _1 = db; using var _2 = tx;

        var etiqueta = await NovaEtiquetaAsync(db, c, "Revendedor");

        // Um contato à parte: o do cenário tem conversa e mensagem penduradas, que são RESTRICT.
        var descartavel = new Contato
        {
            EmpresaId = c.Id,
            Nome = "Descartável",
            Telefone = "5584911112222",
            EtapaId = c.PrimeiraEtapa.Id,
            OrdemKanban = 5m
        };
        db.Contatos.Add(descartavel);
        await db.SaveChangesAsync();

        db.ContatosEtiquetas.Add(Marcacao(c, descartavel.Id, etiqueta.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Contatos.Remove(await db.Contatos.SingleAsync(x => x.Id == descartavel.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await db.ContatosEtiquetas.ToListAsync());

        // A etiqueta é da EMPRESA, não do contato — ela sobrevive.
        Assert.True(await db.Etiquetas.AnyAsync(e => e.Id == etiqueta.Id));
    }

    // ==================================================================== tenant
    /// <summary>===================== QUEM GARANTE É A FK COMPOSTA =====================
    /// O filtro de consulta protege LEITURA, não escrita. Sem `(etiqueta_id, empresa_id)` na FK,
    /// um bug de aplicação cola a etiqueta do vizinho num contato meu — e o sintoma seria o nome
    /// de uma etiqueta que não existe na minha lista aparecendo num card.
    /// =========================================================================</summary>
    [Fact]
    public async Task NAO_DA_PARA_COLAR_ETIQUETA_DE_OUTRA_EMPRESA()
    {
        var (db, tx, c) = await PrepararAsync("tenant-etiqueta");
        using var _1 = db; using var _2 = tx;

        var vizinha = await Semeador.TenantAsync(db, "ligacao-vizinha");
        var daVizinha = await NovaEtiquetaAsync(db, vizinha, "Da vizinha");

        db.ContatosEtiquetas.Add(new ContatoEtiqueta
        {
            EmpresaId = c.Id,              // minha empresa, meu contato…
            ContatoId = c.Contato.Id,
            EtiquetaId = daVizinha.Id      // …etiqueta da outra
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task NAO_DA_PARA_MARCAR_CONTATO_DE_OUTRA_EMPRESA()
    {
        var (db, tx, c) = await PrepararAsync("tenant-contato");
        using var _1 = db; using var _2 = tx;

        var vizinha = await Semeador.TenantAsync(db, "ligacao-vizinha-2");
        var minha = await NovaEtiquetaAsync(db, c, "Minha");

        db.ContatosEtiquetas.Add(new ContatoEtiqueta
        {
            EmpresaId = c.Id,
            ContatoId = vizinha.Contato.Id,   // contato da outra empresa
            EtiquetaId = minha.Id
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_EMPRESA_NAO_ENXERGA_MARCACAO_DE_OUTRA()
    {
        var (db, tx, c) = await PrepararAsync("tenant-invisivel");
        using var _1 = db; using var _2 = tx;

        var vizinha = await Semeador.TenantAsync(db, "ligacao-invisivel");
        var daVizinha = await NovaEtiquetaAsync(db, vizinha, "Da vizinha");

        db.ContatosEtiquetas.Add(new ContatoEtiqueta
        {
            EmpresaId = vizinha.Id,
            ContatoId = vizinha.Contato.Id,
            EtiquetaId = daVizinha.Id
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // O contexto é o da MINHA empresa — `PrepararAsync` o definiu.
        Assert.Empty(await db.ContatosEtiquetas.ToListAsync());

        // E a linha existe de verdade: o teste não passa por a tabela estar vazia.
        Assert.Equal(1, await db.ContatosEtiquetas.IgnoreQueryFilters().CountAsync());
    }

    // ==================================================================== navegação
    /// <summary>A coleção em `Contato` não é conveniência: a projeção da caixa é uma
    /// `static readonly Expression`, e citar `db` dentro dela é CS9105. Sem esta navegação, a
    /// lista da caixa não tem como mostrar os chips.</summary>
    [Fact]
    public async Task A_NAVEGACAO_DO_CONTATO_ENXERGA_AS_ETIQUETAS()
    {
        var (db, tx, c) = await PrepararAsync("navegacao");
        using var _1 = db; using var _2 = tx;

        var etiqueta = await NovaEtiquetaAsync(db, c, "Revendedor");
        db.ContatosEtiquetas.Add(Marcacao(c, c.Contato.Id, etiqueta.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var nomes = await db.Contatos.AsNoTracking()
            .Where(x => x.Id == c.Contato.Id)
            .Select(x => x.Etiquetas.Select(l => l.Etiqueta.Nome).ToList())
            .SingleAsync();

        Assert.Equal(["Revendedor"], nomes);
    }

    // ====================================================================
    private static ContatoEtiqueta Marcacao(Cenario c, long contatoId, long etiquetaId) =>
        new() { EmpresaId = c.Id, ContatoId = contatoId, EtiquetaId = etiquetaId };

    private static async Task<Etiqueta> NovaEtiquetaAsync(NexoraDbContext db, Cenario c, string nome)
    {
        var e = new Etiqueta { EmpresaId = c.Id, Nome = nome };
        db.Etiquetas.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private async Task<(NexoraDbContext, IDbContextTransaction, Cenario)> PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"ligacao-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, cenario);
    }
}
