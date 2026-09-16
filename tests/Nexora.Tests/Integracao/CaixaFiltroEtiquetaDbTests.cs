using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O FILTRO POR ETIQUETA NA CAIXA — issue #3.
///
/// ===================== O QUE ESTE ARQUIVO CUIDA =====================
/// Um `Where` a mais numa consulta que já tem filtro, busca, cursor e ordenação. O risco não está
/// no predicado — está na ORDEM em que ele entra.
///
/// Ele vai entre a busca e o cursor, e não depois: o cursor compara contra a última linha JÁ
/// ENTREGUE, e aplicá-lo antes do filtro faria a página seguinte pular as conversas que o filtro
/// tirou do meio. O sintoma é contato sumindo da rolagem, sem erro nenhum — e é por isso que há
/// um teste de PAGINAÇÃO aqui, e não só de "filtrou certo".
/// ====================================================================</summary>
[Collection("banco")]
public class CaixaFiltroEtiquetaDbTests(BancoTeste banco)
{
    [Fact]
    public async Task O_FILTRO_DEVOLVE_SO_QUEM_TEM_A_ETIQUETA()
    {
        var (db, tx, caixa, etiquetas, c) = await PrepararAsync("basico");
        using var _1 = db; using var _2 = tx;

        var marcada = await etiquetas.CriarAsync(new NovaEtiqueta("Revendedor", null), default);
        var outra = await etiquetas.CriarAsync(new NovaEtiqueta("VIP", null), default);

        // O contato do cenário fica com "Revendedor"; um segundo, com "VIP".
        var segundo = await ContatoComConversaAsync(db, c, "Segundo", "5584922220001");
        await etiquetas.AplicarAsync(c.Contato.Id, [marcada], default);
        db.ChangeTracker.Clear();
        await etiquetas.AplicarAsync(segundo, [outra], default);
        db.ChangeTracker.Clear();

        var pagina = await caixa.ConversasAsync(
            FiltroConversa.Todas, null, marcada, null, null, 30, default);

        Assert.Single(pagina.Itens);
        Assert.Equal(c.Contato.Id, pagina.Itens[0].ContatoId);
    }

    [Fact]
    public async Task SEM_FILTRO_VEM_TODO_MUNDO()
    {
        // O parâmetro é opcional, e nulo NÃO pode significar "nenhuma etiqueta".
        var (db, tx, caixa, etiquetas, c) = await PrepararAsync("sem-filtro");
        using var _1 = db; using var _2 = tx;

        await ContatoComConversaAsync(db, c, "Segundo", "5584922220002");
        var id = await etiquetas.CriarAsync(new NovaEtiqueta("Revendedor", null), default);
        await etiquetas.AplicarAsync(c.Contato.Id, [id], default);
        db.ChangeTracker.Clear();

        var pagina = await caixa.ConversasAsync(
            FiltroConversa.Todas, null, null, null, null, 30, default);

        Assert.Equal(2, pagina.Itens.Count);
    }

    /// <summary>⚠️ O TESTE QUE JUSTIFICA A POSIÇÃO DO `Where`.
    ///
    /// Com o filtro aplicado DEPOIS do cursor, a segunda página compararia contra uma linha que o
    /// filtro já tinha descartado — e conversas do meio sumiriam da rolagem. Sem erro: o vendedor
    /// simplesmente não veria um contato que existe.
    ///
    /// Três conversas marcadas, página de duas, e a segunda página tem de trazer a terceira.</summary>
    [Fact]
    public async Task A_PAGINACAO_POR_CURSOR_CONTINUA_CERTA_COM_O_FILTRO()
    {
        var (db, tx, caixa, etiquetas, c) = await PrepararAsync("cursor");
        using var _1 = db; using var _2 = tx;

        var id = await etiquetas.CriarAsync(new NovaEtiqueta("Revendedor", null), default);

        var todos = new List<long> { c.Contato.Id };
        for (var i = 0; i < 2; i++)
            todos.Add(await ContatoComConversaAsync(db, c, $"Marcado {i}", $"558492222010{i}"));

        // Um NÃO marcado no meio: é ele que a ordem errada deixaria "engolir" uma página.
        await ContatoComConversaAsync(db, c, "Nao marcado", "5584922220199");

        foreach (var contatoId in todos)
        {
            await etiquetas.AplicarAsync(contatoId, [id], default);
            db.ChangeTracker.Clear();
        }

        var primeira = await caixa.ConversasAsync(
            FiltroConversa.Todas, null, id, null, null, 2, default);

        Assert.Equal(2, primeira.Itens.Count);
        Assert.True(primeira.TemMais);

        var ultima = primeira.Itens[^1];
        var segunda = await caixa.ConversasAsync(
            FiltroConversa.Todas, null, id, ultima.UltimaMensagemEm, ultima.Id, 2, default);

        Assert.Single(segunda.Itens);
        Assert.False(segunda.TemMais);

        // As três marcadas apareceram, sem repetir nenhuma.
        var vistos = primeira.Itens.Concat(segunda.Itens).Select(x => x.ContatoId).ToList();
        Assert.Equal(3, vistos.Distinct().Count());
        Assert.All(vistos, v => Assert.Contains(v, todos));
    }

    [Fact]
    public async Task O_FILTRO_SE_COMBINA_COM_A_BUSCA()
    {
        // Os dois recortam, e recortam coisas diferentes: a etiqueta diz QUAL conjunto, a busca
        // afina dentro dele. Aplicar um sem o outro é o caso comum; aplicar os dois tem de valer.
        var (db, tx, caixa, etiquetas, c) = await PrepararAsync("busca");
        using var _1 = db; using var _2 = tx;

        var id = await etiquetas.CriarAsync(new NovaEtiqueta("Revendedor", null), default);
        var segundo = await ContatoComConversaAsync(db, c, "Padaria Estrela", "5584922220003");

        await etiquetas.AplicarAsync(c.Contato.Id, [id], default);
        db.ChangeTracker.Clear();
        await etiquetas.AplicarAsync(segundo, [id], default);
        db.ChangeTracker.Clear();

        var pagina = await caixa.ConversasAsync(
            FiltroConversa.Todas, "padaria", id, null, null, 30, default);

        Assert.Single(pagina.Itens);
        Assert.Equal(segundo, pagina.Itens[0].ContatoId);
    }

    [Fact]
    public async Task O_FILTRO_NAO_ATRAVESSA_A_EMPRESA()
    {
        // O filtro de consulta já recorta as conversas; este teste existe para o dia em que
        // alguém "otimizar" a listagem com SQL cru e perder o recorte.
        var (db, tx, caixa, _, c) = await PrepararAsync("tenant");
        using var _1 = db; using var _2 = tx;

        var vizinha = await Semeador.TenantAsync(db, "caixa-etiqueta-vizinha");
        var daVizinha = new Etiqueta { EmpresaId = vizinha.Id, Nome = "Da vizinha" };
        db.Etiquetas.Add(daVizinha);
        await db.SaveChangesAsync();
        db.ContatosEtiquetas.Add(new ContatoEtiqueta
        {
            EmpresaId = vizinha.Id, ContatoId = vizinha.Contato.Id, EtiquetaId = daVizinha.Id
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var pagina = await caixa.ConversasAsync(
            FiltroConversa.Todas, null, daVizinha.Id, null, null, 30, default);

        Assert.Empty(pagina.Itens);
        _ = c;
    }

    // ==================================================================== os chips na linha
    [Fact]
    public async Task A_LINHA_DA_CAIXA_TRAZ_AS_ETIQUETAS_DO_CONTATO()
    {
        // ⚠️ Vem pela NAVEGAÇÃO, porque a projeção da caixa é `static readonly` e citar `db`
        // dentro dela é CS9105. Este teste é o que prova que o caminho funciona.
        var (db, tx, caixa, etiquetas, c) = await PrepararAsync("chips");
        using var _1 = db; using var _2 = tx;

        var a = await etiquetas.CriarAsync(new NovaEtiqueta("Revendedor", null), default);
        var b = await etiquetas.CriarAsync(new NovaEtiqueta("Urgente", null), default);
        await etiquetas.AplicarAsync(c.Contato.Id, [a, b], default);
        db.ChangeTracker.Clear();

        var pagina = await caixa.ConversasAsync(
            FiltroConversa.Todas, null, null, null, null, 30, default);

        var linha = pagina.Itens.Single(x => x.ContatoId == c.Contato.Id);
        Assert.Equal(["Revendedor", "Urgente"], linha.Etiquetas.Select(e => e.Nome));
    }

    // ====================================================================
    private static async Task<long> ContatoComConversaAsync(
        NexoraDbContext db, Cenario c, string nome, string telefone)
    {
        var contato = new Contato
        {
            EmpresaId = c.Id,
            Nome = nome,
            Telefone = telefone
        };
        db.Contatos.Add(contato);
        db.Negociacoes.Add(Semeador.Negocio(contato, c.PrimeiraEtapa, 100m));
        await db.SaveChangesAsync();

        db.Conversas.Add(new Conversa
        {
            EmpresaId = c.Id,
            ContatoId = contato.Id,
            ConexaoId = c.Conexao.Id,
            Status = StatusConversa.Aberta,
            UltimaMensagemEm = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return contato.Id;
    }

    // ==================================================================== a etapa da linha
    /// <summary>⚠️ COM DOIS NEGOCIOS, A CAIXA MOSTRA A ETAPA DO ABERTO.
    ///
    /// `contatos.etapa_id` sai no E4e, e com ele a resposta unica. A caixa e sobre a conversa em
    /// andamento: um pedido fechado esperando conclusao nao e o que se esta conversando.
    ///
    /// ⚠️ A ordenacao e por STATUS, nao por id. A migracao do elo reinseriu as linhas vindas de
    /// venda, e os ids delas ficaram MAIORES que os das abertas — id deixou de ser relogio. O
    /// teste monta essa ordem invertida de proposito, que e a que o banco de desenvolvimento
    /// tem.
    ///
    /// ⚠️ OS DOIS NEGOCIOS ESTAO EM FUNIS DIFERENTES, e a fixture MUDOU por isso: ela inseria os
    /// dois no funil do cenario, e `uq_negociacoes_card_por_funil` passou a recusar. A pergunta
    /// do teste nao e sobre funil nenhum — "com dois negocios, a caixa mostra a etapa do
    /// ABERTO" — e continua valendo palavra por palavra.</summary>
    [Fact]
    public async Task A_LINHA_MOSTRA_A_ETAPA_DO_NEGOCIO_ABERTO()
    {
        var (db, tx, caixa, _, c) = await PrepararAsync("etapa-do-aberto");
        using var _1 = db; using var _2 = tx;

        // ⚠️ NUM SEGUNDO FUNIL: um card por pessoa por funil, entao "esta pessoa tem um pedido
        // a caminho E uma negociacao andando" so existe em funis diferentes.
        var outro = new Pipeline { EmpresaId = c.Id, Nome = "Atacado", Ordem = 2 };
        db.Pipelines.Add(outro);
        await db.SaveChangesAsync();

        var vendido = new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = outro.Id, Nome = "Vendido", Ordem = 1, EGanho = true
        };
        db.EtapasFunil.Add(vendido);
        await db.SaveChangesAsync();

        // A ganha entra DEPOIS da aberta, entao com id maior.
        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = c.Id,
            ContatoId = c.Contato.Id,
            PipelineId = outro.Id,
            EtapaId = vendido.Id,
            Valor = 500m,
            Status = StatusNegociacao.Ganha,
            GanhaEm = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var linha = (await caixa.ConversasAsync(
            FiltroConversa.Todas, null, null, null, null, 30, default)).Itens.Single();

        // A ABERTA manda, mesmo sendo a de id menor.
        Assert.Equal(c.PrimeiraEtapa.Id, linha.EtapaId);
        Assert.Equal(c.PrimeiraEtapa.Nome, linha.EtapaNome);

        // E o resto da linha conta o negocio fechado que existe.
        Assert.True(linha.ContatoGanhou);
        Assert.Equal(1, linha.VendasEmAberto);
    }

    private async Task<(NexoraDbContext, IDbContextTransaction, ServicoCaixa, ServicoEtiquetas, Cenario)>
        PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"caixa-etiqueta-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new ServicoCaixa(db, ctx), new ServicoEtiquetas(db, ctx), cenario);
    }
}
