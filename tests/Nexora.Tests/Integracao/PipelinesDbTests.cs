using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>A PIPELINE — o funil como coisa própria, em vez de "as etapas da empresa".
///
/// ===================== O QUE ESTE BLOCO DE FATO FAZ =====================
/// Nenhuma tela muda. O que muda é o ESCOPO de duas invariantes que existiam desde o primeiro dia:
///
///   `uq_etapas_ordem`  era (empresa_id, ordem)             → (empresa_id, pipeline_id, ordem)
///   `uq_etapas_ganho`  era (empresa_id) WHERE e_ganho      → (empresa_id, pipeline_id) WHERE …
///
/// Sem isso a segunda pipeline não consegue nascer: ela precisa da própria etapa de ordem 1 e da
/// própria etapa de ganho, e as duas colidiriam com as da primeira. Os dois primeiros testes daqui
/// são exatamente esse par — o que passou a ser permitido, e o que continua proibido.
/// =======================================================================</summary>
[Collection("banco")]
public class PipelinesDbTests(BancoTeste banco)
{
    // ==================================================================== o que destravou
    /// <summary>⚠️ ESTE É O TESTE QUE JUSTIFICA O BLOCO INTEIRO.
    ///
    /// Antes dele, esta operação estourava violação de índice único. É a linha entre "a empresa
    /// tem um funil" e "a empresa tem funis".</summary>
    [Fact]
    public async Task DUAS_PIPELINES_PODEM_TER_CADA_UMA_A_SUA_ETAPA_DE_GANHO()
    {
        var (db, tx, c) = await PrepararAsync("ganho-por-pipeline");
        using var _1 = db; using var _2 = tx;

        var posVenda = await NovaPipelineAsync(db, c, "Pos-venda");

        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = posVenda.Id, Nome = "Recompra", Ordem = 3, EGanho = true
        });

        // Não estoura: o cenário já tem uma etapa de ganho ("Venda") na pipeline padrão.
        await db.SaveChangesAsync();

        var ganhos = await db.EtapasFunil.AsNoTracking().Where(e => e.EGanho).CountAsync();
        Assert.Equal(2, ganhos);
    }

    [Fact]
    public async Task DUAS_PIPELINES_PODEM_TER_CADA_UMA_A_SUA_ETAPA_DE_ORDEM_1()
    {
        var (db, tx, c) = await PrepararAsync("ordem-por-pipeline");
        using var _1 = db; using var _2 = tx;

        var posVenda = await NovaPipelineAsync(db, c, "Pos-venda");

        // A pipeline padrão já tem uma etapa de ordem 1. Esta é a segunda no banco com ordem 1, e
        // antes deste bloco seria recusada.
        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = posVenda.Id, Nome = "Entrega feita", Ordem = 1
        });
        await db.SaveChangesAsync();

        var deOrdem1 = await db.EtapasFunil.AsNoTracking().Where(e => e.Ordem == 1).CountAsync();
        Assert.Equal(2, deOrdem1);
    }

    // ==================================================================== o que continua proibido
    /// <summary>O escopo encolheu, a regra não sumiu. Se este teste cair, o bloco trocou uma
    /// garantia por nada — e o sintoma em produção seria um funil com duas colunas "Venda",
    /// nenhuma das duas confiável para a conversão.</summary>
    [Fact]
    public async Task DUAS_ETAPAS_DE_GANHO_NA_MESMA_PIPELINE_CONTINUAM_PROIBIDAS()
    {
        var (db, tx, c) = await PrepararAsync("ganho-duplo");
        using var _1 = db; using var _2 = tx;

        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = c.Pipeline.Id, Nome = "Outro Ganho", Ordem = 99, EGanho = true
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DUAS_ETAPAS_NA_MESMA_ORDEM_DA_MESMA_PIPELINE_CONTINUAM_PROIBIDAS()
    {
        // É esta unicidade que a paginação por cursor do quadro usa para não pular nem repetir
        // card. Perdê-la não daria erro — daria contato sumindo da rolagem.
        var (db, tx, c) = await PrepararAsync("ordem-dupla");
        using var _1 = db; using var _2 = tx;

        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = c.Pipeline.Id, Nome = "Colidente", Ordem = 1
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ==================================================================== tenant
    [Fact]
    public async Task UMA_ETAPA_NAO_PODE_APONTAR_PARA_PIPELINE_DE_OUTRA_EMPRESA()
    {
        // ===================== QUEM GARANTE É A FK COMPOSTA =====================
        // O filtro de consulta protege LEITURA, não escrita. Sem `(pipeline_id, empresa_id)` na
        // FK, um bug de aplicação grava a pipeline do vizinho e ninguém percebe — a etapa some do
        // funil de quem a criou e aparece no de outro cliente.
        // =======================================================================
        var (db, tx, c) = await PrepararAsync("tenant-pipeline");
        using var _1 = db; using var _2 = tx;

        var vizinha = await Semeador.TenantAsync(db, "pipelines-vizinha");

        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = c.Id,                    // minha empresa…
            PipelineId = vizinha.Pipeline.Id,    // …pipeline da outra
            Nome = "Invasora", Ordem = 50
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_EMPRESA_NAO_ENXERGA_PIPELINE_DE_OUTRA()
    {
        var (db, tx, _) = await PrepararAsync("tenant-isolado");
        using var _1 = db; using var _2 = tx;

        await Semeador.TenantAsync(db, "pipelines-invisivel");
        db.ChangeTracker.Clear();

        var minhas = await db.Pipelines.AsNoTracking().ToListAsync();
        Assert.Single(minhas);
    }

    // ==================================================================== padrão
    [Fact]
    public async Task SO_EXISTE_UMA_PIPELINE_PADRAO_POR_EMPRESA()
    {
        // É por ela que o lead entra quando nada mais decide. Duas seriam duas respostas para uma
        // pergunta com uma resposta só, e o sintoma seria lead caindo em funil diferente conforme
        // o plano de execução do dia.
        var (db, tx, c) = await PrepararAsync("padrao-unica");
        using var _1 = db; using var _2 = tx;

        db.Pipelines.Add(new Pipeline
        {
            EmpresaId = c.Id, Nome = "Segunda padrão", Ordem = 2, Padrao = true
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DUAS_PIPELINES_NAO_PADRAO_CONVIVEM()
    {
        // O índice é PARCIAL (`WHERE padrao`). Sem o filtro, a empresa só poderia ter uma pipeline
        // no total — que é exatamente o que este bloco veio desfazer.
        var (db, tx, c) = await PrepararAsync("nao-padrao");
        using var _1 = db; using var _2 = tx;

        db.Pipelines.Add(new Pipeline { EmpresaId = c.Id, Nome = "Atacado", Ordem = 2 });
        db.Pipelines.Add(new Pipeline { EmpresaId = c.Id, Nome = "Eventos", Ordem = 3 });
        await db.SaveChangesAsync();

        Assert.Equal(3, await db.Pipelines.CountAsync());
    }

    [Fact]
    public async Task O_NOME_DA_PIPELINE_E_UNICO_NA_EMPRESA()
    {
        var (db, tx, c) = await PrepararAsync("nome-unico");
        using var _1 = db; using var _2 = tx;

        db.Pipelines.Add(new Pipeline { EmpresaId = c.Id, Nome = "Vendas", Ordem = 2 });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ==================================================================== a migração
    /// <summary>O que a migração prometeu para quem já usava o produto: o funil continua igual,
    /// só passou a pendurar numa pipeline. Este teste lê o estado que `Semeador` monta — que é o
    /// mesmo desenho que o backfill produz.</summary>
    [Fact]
    public async Task TODA_ETAPA_PERTENCE_A_UMA_PIPELINE_DA_PROPRIA_EMPRESA()
    {
        var (db, tx, c) = await PrepararAsync("integridade");
        using var _1 = db; using var _2 = tx;

        var etapas = await db.EtapasFunil.AsNoTracking().ToListAsync();
        Assert.NotEmpty(etapas);
        Assert.All(etapas, e => Assert.Equal(c.Pipeline.Id, e.PipelineId));

        var padrao = await db.Pipelines.AsNoTracking().SingleAsync(p => p.Padrao);
        Assert.Equal("Vendas", padrao.Nome);
    }

    // ==================================================================== o serviço
    /// <summary>⚠️ A TELA `/etapas` NÃO SABE QUE PIPELINES EXISTEM, e é assim de propósito neste
    /// bloco. `ServicoEtapas` resolve a padrão sozinho, e o comportamento observável não muda.
    ///
    /// O teste que importa é o SEGUNDO `Assert`: a etapa criada pelo serviço nasce pendurada na
    /// pipeline padrão. Sem isso ela nasceria com `pipeline_id = 0` e a FK recusaria — um erro que
    /// só apareceria no primeiro cliente que criasse uma etapa depois do deploy.</summary>
    [Fact]
    public async Task O_SERVICO_DE_ETAPAS_CRIA_NA_PIPELINE_PADRAO_SEM_SABER_DELA()
    {
        var (db, tx, c) = await PrepararAsync("servico");
        using var _1 = db; using var _2 = tx;

        var ctx = new ContextoMutavel { EmpresaId = c.Id, UsuarioId = c.Dono.Id, Papel = "dono" };
        var servico = new ServicoEtapas(db, ctx);

        var id = await servico.CriarAsync(new NovaEtapa("Visita agendada", null), default);

        var criada = await db.EtapasFunil.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal(c.Pipeline.Id, criada.PipelineId);

        // E a listagem continua devolvendo o funil inteiro, como antes.
        var lista = await servico.ListarAsync(default);
        Assert.Equal(4, lista.Count);
    }

    [Fact]
    public async Task A_LISTAGEM_NAO_MISTURA_ETAPAS_DE_OUTRA_PIPELINE()
    {
        // Hoje a empresa só tem uma pipeline, então este teste parece redundante. Ele não é: é o
        // que vai acusar se, no bloco do menu, alguém remover o filtro por pipeline achando que
        // "é sempre a mesma".
        var (db, tx, c) = await PrepararAsync("listagem-isolada");
        using var _1 = db; using var _2 = tx;

        var outra = await NovaPipelineAsync(db, c, "Atacado");
        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = outra.Id, Nome = "Prospecção", Ordem = 1
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var ctx = new ContextoMutavel { EmpresaId = c.Id, UsuarioId = c.Dono.Id, Papel = "dono" };
        var lista = await new ServicoEtapas(db, ctx).ListarAsync(default);

        Assert.Equal(3, lista.Count);
        Assert.DoesNotContain(lista, e => e.Nome == "Prospecção");
    }

    // ====================================================================
    private static async Task<Pipeline> NovaPipelineAsync(NexoraDbContext db, Cenario c, string nome)
    {
        var p = new Pipeline { EmpresaId = c.Id, Nome = nome, Ordem = 2 };
        db.Pipelines.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private async Task<(NexoraDbContext, IDbContextTransaction, Cenario)> PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"pipelines-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, cenario);
    }
}
