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

        var id = await servico.CriarAsync(c.Pipeline.Id, new NovaEtapa("Visita agendada", null), default);

        var criada = await db.EtapasFunil.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal(c.Pipeline.Id, criada.PipelineId);

        // E a listagem continua devolvendo o funil inteiro, como antes.
        var lista = await servico.ListarAsync(c.Pipeline.Id, default);
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
        var lista = await new ServicoEtapas(db, ctx).ListarAsync(c.Pipeline.Id, default);

        Assert.Equal(3, lista.Count);
        Assert.DoesNotContain(lista, e => e.Nome == "Prospecção");
    }

    // ==================================================================== a ambiguidade
    /// <summary>===================== O QUE SO QUEBRA COM A SEGUNDA PIPELINE =====================
    /// Tres consultas do produto perguntavam "a etapa de ganho" e "a etapa de menor ordem" sem
    /// dizer DE QUAL FUNIL. Com uma pipeline so, cada pergunta tinha uma resposta unica e o codigo
    /// estava correto. Com duas, elas devolvem a etapa de um funil qualquer — e o sintoma nao e
    /// erro, e card aparecendo no quadro errado.
    ///
    /// Estes testes existem porque nenhum dos 768 anteriores pega isso: todos rodam com uma
    /// pipeline so, que e exatamente o caso em que o bug nao aparece.
    /// ================================================================================</summary>
    [Fact]
    public async Task GANHAR_CARIMBA_NA_ETAPA_DE_GANHO_DO_PROPRIO_FUNIL()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pipelines-ganho-do-proprio");
        var c = amb.Cenario;
        using var _1 = db; using var _2 = tx;

        // ⚠️ A ARMADILHA PRECISA SER DETERMINÍSTICA. A consulta antiga era `Where(e => e.EGanho)`
        // SEM ordenação — pôr a etapa de ganho da outra pipeline em qualquer lugar não garante que
        // ela seria a escolhida, e o teste passaria mesmo com o defeito presente.
        //
        // Então o contato vai para a OUTRA pipeline. Agora a resposta certa é a etapa de ganho
        // DELA, e a consulta antiga — que devolve a primeira do banco, a do cenário — erra sempre.
        var outra = await NovaPipelineAsync(db, c, "Atacado");
        var entradaDaOutra = new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = outra.Id, Nome = "Prospecção", Ordem = 1
        };
        var ganhoDaOutra = new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = outra.Id, Nome = "Fechado", Ordem = 2, EGanho = true
        };
        db.EtapasFunil.AddRange(entradaDaOutra, ganhoDaOutra);
        await db.SaveChangesAsync();

        // ⚠️ MOVER O CONTATO NAO MOVE MAIS NADA (E4e/4): o funil e da negociacao. Antes o
        // servico lia `contato.EtapaId` para descobrir a pipeline; hoje le a da propria linha que
        // vai fechar, e e por isso que a fixture mudou de alvo.
        var negocio = await db.Negociacoes.SingleAsync(n => n.ContatoId == c.Contato.Id);
        negocio.EtapaId = entradaDaOutra.Id;
        negocio.PipelineId = outra.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Contatos.MarcarGanhoAsync(c.Contato.Id, 500m, null, null, default);
        db.ChangeTracker.Clear();

        var depois = await db.Negociacoes.AsNoTracking().SingleAsync(n => n.ContatoId == c.Contato.Id);
        Assert.Equal(ganhoDaOutra.Id, depois.EtapaId);
    }

    [Fact]
    public async Task REABRIR_DEVOLVE_O_CARD_AO_PROPRIO_FUNIL()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pipelines-reabrir-proprio");
        var c = amb.Cenario;
        using var _1 = db; using var _2 = tx;

        var outra = await NovaPipelineAsync(db, c, "Pos-venda");
        var entradaDaOutra = new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = outra.Id, Nome = "Entrega feita", Ordem = 1
        };
        db.EtapasFunil.AddRange(
            entradaDaOutra,
            new EtapaFunil
            {
                EmpresaId = c.Id, PipelineId = outra.Id, Nome = "Recompra", Ordem = 2, EGanho = true
            });
        await db.SaveChangesAsync();

        var negocio = await db.Negociacoes.SingleAsync(n => n.ContatoId == c.Contato.Id);
        negocio.EtapaId = entradaDaOutra.Id;
        negocio.PipelineId = outra.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Contatos.MarcarGanhoAsync(c.Contato.Id, 500m, null, null, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.AbrirNegociacaoAsync(c.Contato.Id, null, default);
        db.ChangeTracker.Clear();

        // Volta para a entrada DA OUTRA pipeline. A consulta antiga devolveria "Novo Lead", do
        // funil do cenário — trocando o funil num gesto que não tem nada a ver com isso.
        var aberta = await db.Negociacoes.AsNoTracking()
            .SingleAsync(n => n.ContatoId == c.Contato.Id && n.Status == StatusNegociacao.Aberta);
        Assert.Equal(entradaDaOutra.Id, aberta.EtapaId);
    }

    /// <summary>⚠️ ESTE E O MAIS CARO DOS TRES. E por aqui que entra TODO lead novo do produto —
    /// mensagem de numero desconhecido no WhatsApp. Se ele nascer na pipeline errada, o contato
    /// existe, a conversa existe, e o vendedor simplesmente nunca ve o card.</summary>
    [Fact]
    public async Task O_CONTATO_CRIADO_A_MAO_ENTRA_PELA_PIPELINE_PADRAO()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pipelines-entrada-padrao");
        var c = amb.Cenario;
        using var _1 = db; using var _2 = tx;

        // ⚠️ ORDEM 0, MENOR que a do cenário. A consulta antiga era `OrderBy(e => e.Ordem).First()`
        // sobre a empresa inteira — com ordem 1 nos dois funis o desempate seria físico e o teste
        // passaria por acaso. Com 0 ela escolhe esta, sempre, e o teste de fato prova a correção.
        var outra = await NovaPipelineAsync(db, c, "Atacado");
        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = outra.Id, Nome = "Prospecção", Ordem = 0
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var id = await amb.Contatos.CriarAsync(
            new NovoContato("Cliente novo", "84988887777", null, null, null, null, null), default);
        db.ChangeTracker.Clear();

        var criado = await db.Negociacoes.AsNoTracking().SingleAsync(n => n.ContatoId == id);
        var etapa = await db.EtapasFunil.AsNoTracking().SingleAsync(e => e.Id == criado.EtapaId);

        Assert.Equal(c.Pipeline.Id, etapa.PipelineId);
        Assert.True(await db.Pipelines.AsNoTracking().AnyAsync(p => p.Id == etapa.PipelineId && p.Padrao));
    }

    // ==================================================================== a contagem do menu
    /// <summary>⚠️ ESTE TESTE É A RAZÃO DE A CONTAGEM PODER EXISTIR.
    ///
    /// O número ao lado do nome da pipeline no menu é uma QUARTA escrita da regra "o que aparece
    /// no quadro" — regra que já divergiu uma vez entre o quadro e o dashboard, e que fez o
    /// cliente ver 72 numa etapa onde havia 69 cards.
    ///
    /// Eu quase não implementei a contagem por causa disso. Estava errado: não mostrar o número
    /// não elimina o risco, adia. O que elimina é exigir que as duas contas deem o mesmo
    /// resultado — e é isso que está aqui. Se alguém mexer no menu ou no quadro sem mexer no
    /// outro, este teste cai antes de o cliente ver a divergência.</summary>
    [Fact]
    public async Task A_CONTAGEM_DO_MENU_BATE_COM_A_SOMA_DO_QUADRO()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pipelines-contagem");
        var c = amb.Cenario;
        using var _1 = db; using var _2 = tx;

        // Os três casos que a regra trata de formas diferentes — e que só juntos provam algo:
        // um contato comum conta, um perdido não conta, um ganho conta só enquanto a venda está
        // em aberto.
        var ctx = new ContextoMutavel { EmpresaId = c.Id, UsuarioId = c.Dono.Id, Papel = "dono" };

        var comum = await amb.Contatos.CriarAsync(
            new NovoContato("Comum", "84980000001", null, null, null, null, null), default);
        var perdido = await amb.Contatos.CriarAsync(
            new NovoContato("Perdido", "84980000002", null, null, null, null, null), default);
        var ganho = await amb.Contatos.CriarAsync(
            new NovoContato("Ganho", "84980000003", null, null, null, null, null), default);

        // ⚠️ O QUARTO CASO, E FOI ELE QUE FALTAVA: a pessoa com DOIS negócios vivos.
        //
        // Enquanto os dois lados contavam contato, os três casos acima bastavam. Depois que o
        // quadro passou a ler `negociacoes`, o menu (que ainda contava pessoa) só divergia neste
        // arranjo — e o teste ficou verde enquanto o cliente via menu 12 e quadro 13.
        //
        // Ganhar e reabrir deixa a venda fechada esperando conclusão E uma negociação nova: dois
        // cards, uma pessoa.
        var voltou = await amb.Contatos.CriarAsync(
            new NovoContato("Voltou", "84980000004", null, null, null, null, null), default);

        await amb.Contatos.MarcarPerdidoAsync(perdido, "Sem interesse", null, default);
        await amb.Contatos.MarcarGanhoAsync(ganho, 900m, null, null, default);

        await amb.Contatos.MarcarGanhoAsync(voltou, 500m, null, null, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.AbrirNegociacaoAsync(voltou, null, default);
        db.ChangeTracker.Clear();

        var doMenu = (await new ServicoPipelines(db, ctx).ListarAsync(default))
            .Single(p => p.Id == c.Pipeline.Id).Contatos;

        var doQuadro = (await amb.Funil.QuadroAsync(c.Pipeline.Id, 50, default))
            .Colunas.Sum(col => col.Total);

        Assert.Equal(doQuadro, doMenu);

        // E o número não é trivialmente zero dos dois lados — senão o teste passaria sem provar
        // nada. São o contato do cenário + "Comum" + "Ganho" + os DOIS de "Voltou"; "Perdido"
        // fica de fora.
        Assert.Equal(5, doMenu);

        _ = comum;
    }

    [Fact]
    public async Task A_CONTAGEM_NAO_SOMA_CONTATO_DE_OUTRA_PIPELINE()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pipelines-contagem-isolada");
        var c = amb.Cenario;
        using var _1 = db; using var _2 = tx;

        var outra = await NovaPipelineAsync(db, c, "Atacado");
        var entradaDaOutra = new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = outra.Id, Nome = "Prospecção", Ordem = 1
        };
        db.EtapasFunil.Add(entradaDaOutra);
        await db.SaveChangesAsync();

        // O negócio do cenário muda de funil, que é o que `ServicoFunil.MoverAsync` faz.
        var negociacao = await db.Negociacoes.SingleAsync(n => n.ContatoId == c.Contato.Id);
        negociacao.EtapaId = entradaDaOutra.Id;
        negociacao.PipelineId = outra.Id;

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var ctx = new ContextoMutavel { EmpresaId = c.Id, UsuarioId = c.Dono.Id, Papel = "dono" };
        var lista = await new ServicoPipelines(db, ctx).ListarAsync(default);

        Assert.Equal(0, lista.Single(p => p.Id == c.Pipeline.Id).Contatos);
        Assert.Equal(1, lista.Single(p => p.Id == outra.Id).Contatos);
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
