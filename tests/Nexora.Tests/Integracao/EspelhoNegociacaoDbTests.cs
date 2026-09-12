using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>O ESPELHO: `negociacoes` acompanhando `contatos`/`vendas` (E4b).
///
/// ===================== POR QUE ESTES TESTES SÃO O ENTREGÁVEL =====================
/// Nada LÊ `negociacoes` ainda. Isso significa que a suíte inteira continua verde mesmo que o
/// espelho esteja completamente errado — e continuaria verde até o E4c virar as leituras, quando
/// o defeito apareceria como card sumido ou faturamento trocado, longe da causa.
///
/// Estes testes são a única coisa que prova que a escrita está certa enquanto ninguém lê.
/// ====================================================================================</summary>
[Collection("banco")]
public class EspelhoNegociacaoDbTests(BancoTeste banco)
{
    // ==================================================================== nascer
    [Fact]
    public async Task O_CONTATO_NOVO_JA_NASCE_COM_A_NEGOCIACAO_ABERTA()
    {
        var (db, tx, amb) = await PrepararAsync("nasce");
        using var _1 = db; using var _2 = tx;

        var id = await amb.Contatos.CriarAsync(
            new NovoContato("Padaria Estrela", "5584988887777", Valor: 250m), default);
        db.ChangeTracker.Clear();

        var negociacao = await db.Negociacoes.SingleAsync(n => n.ContatoId == id);
        var contato = await db.Contatos.SingleAsync(c => c.Id == id);

        Assert.Equal(StatusNegociacao.Aberta, negociacao.Status);
        Assert.Equal(contato.EtapaId, negociacao.EtapaId);
        Assert.Equal(contato.OrdemKanban, negociacao.OrdemKanban);
        Assert.Equal(250m, negociacao.Valor);
        Assert.Equal(amb.Cenario.Pipeline.Id, negociacao.PipelineId);
    }

    // ==================================================================== arrastar
    [Fact]
    public async Task ARRASTAR_O_CARD_MOVE_A_NEGOCIACAO_JUNTO()
    {
        var (db, tx, amb) = await PrepararAsync("arrastar");
        using var _1 = db; using var _2 = tx;

        var destino = amb.Cenario.Etapas[1];
        var ordem = await amb.Funil.MoverAsync(
            amb.Cenario.Contato.Id, new MoverContato(destino.Id, null), default);
        db.ChangeTracker.Clear();

        var negociacao = await db.Negociacoes.SingleAsync();
        Assert.Equal(destino.Id, negociacao.EtapaId);
        Assert.Equal(ordem, negociacao.OrdemKanban);
    }

    // ==================================================================== ganhar
    /// <summary>⚠️ A MESMA LINHA MUDA DE ESTADO — não nasce outra.
    ///
    /// Se o ganho criasse uma negociação nova, o contato ficaria com duas (a aberta velha e a
    /// ganha), e o quadro mostraria o mesmo negócio em duas colunas. O `Single` abaixo é o teste.</summary>
    [Fact]
    public async Task REGISTRAR_A_VENDA_TRANSFORMA_A_ABERTA_EM_GANHA_E_LIGA_NA_VENDA()
    {
        var (db, tx, amb) = await PrepararAsync("ganhar");
        using var _1 = db; using var _2 = tx;

        var antes = await db.Negociacoes.SingleAsync();

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 900m, null, default);
        db.ChangeTracker.Clear();

        var negociacao = await db.Negociacoes.SingleAsync();
        Assert.Equal(antes.Id, negociacao.Id);                 // é a MESMA linha
        Assert.Equal(StatusNegociacao.Ganha, negociacao.Status);
        Assert.Equal(900m, negociacao.Valor);
        Assert.NotNull(negociacao.GanhaEm);

        // O elo com a venda: é ele que `Concluir` e `Cancelar` usam.
        var venda = await db.Vendas.SingleAsync();
        Assert.Equal(venda.Id, negociacao.VendaId);
        Assert.Equal(venda.EtapaId, negociacao.EtapaId);
    }

    /// <summary>Prazo zero conclui na hora (NEG-2): padaria, salão, loja de balcão. O espelho tem
    /// de sair de aberta direto para concluída — passar por ganha deixaria o card no quadro até
    /// alguém clicar em concluir, que é justamente o acúmulo que o NEG-2 resolveu.</summary>
    [Fact]
    public async Task COM_PRAZO_ZERO_A_NEGOCIACAO_JA_NASCE_CONCLUIDA()
    {
        var (db, tx, amb) = await PrepararAsync("prazo-zero");
        using var _1 = db; using var _2 = tx;

        await db.Empresas.Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.DiasParaConcluirVenda, 0));
        db.ChangeTracker.Clear();

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 120m, null, default);
        db.ChangeTracker.Clear();

        var negociacao = await db.Negociacoes.SingleAsync();
        Assert.Equal(StatusNegociacao.Concluida, negociacao.Status);
        Assert.NotNull(negociacao.GanhaEm);
        Assert.NotNull(negociacao.ConcluidaEm);
    }

    // ==================================================================== perder
    [Fact]
    public async Task PERDER_MARCA_A_NEGOCIACAO_E_PRESERVA_A_ETAPA()
    {
        var (db, tx, amb) = await PrepararAsync("perder");
        using var _1 = db; using var _2 = tx;

        var etapaAntes = amb.Cenario.Contato.EtapaId;

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "achou caro", default);
        db.ChangeTracker.Clear();

        var negociacao = await db.Negociacoes.SingleAsync();
        Assert.Equal(StatusNegociacao.Perdida, negociacao.Status);
        Assert.Equal("achou caro", negociacao.MotivoPerda);

        // A etapa registra ONDE o negócio morreu — é o mesmo motivo pelo qual `MarcarPerdidoAsync`
        // não mexe em `contatos.etapa_id`.
        Assert.Equal(etapaAntes, negociacao.EtapaId);
    }

    [Fact]
    public async Task REABRIR_UMA_PERDA_DEVOLVE_A_MESMA_NEGOCIACAO_AO_QUADRO()
    {
        var (db, tx, amb) = await PrepararAsync("reabrir-perda");
        using var _1 = db; using var _2 = tx;

        var antes = await db.Negociacoes.SingleAsync();

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "sumiu", default);
        db.ChangeTracker.Clear();
        await amb.Contatos.ReabrirAsync(amb.Cenario.Contato.Id, default);
        db.ChangeTracker.Clear();

        // Desfazer uma perda é desfazer, não recomeçar: a mesma linha volta.
        var negociacao = await db.Negociacoes.SingleAsync();
        Assert.Equal(antes.Id, negociacao.Id);
        Assert.Equal(StatusNegociacao.Aberta, negociacao.Status);
        Assert.Null(negociacao.PerdidaEm);
        Assert.Null(negociacao.MotivoPerda);
    }

    // ==================================================================== reabrir um GANHO
    /// <summary>⚠️ ESTE É O CASO QUE MAIS FÁCIL SE ERRA, E ELE É SOBRE DINHEIRO.
    ///
    /// `ReabrirAsync` NÃO toca em `vendas` de propósito — o comentário lá diz por quê: o que já
    /// foi faturado continua faturado, e limpar `ganho_em` apagando a venda era exatamente o
    /// defeito que a tabela `vendas` nasceu para corrigir.
    ///
    /// Então a negociação ganha também não pode ser rebaixada para aberta. A rodada nova é uma
    /// LINHA NOVA. Rebaixar faria o faturamento de um mês já fechado mudar sozinho.</summary>
    [Fact]
    public async Task REABRIR_UM_GANHO_PRESERVA_A_GANHA_E_ABRE_OUTRA()
    {
        var (db, tx, amb) = await PrepararAsync("reabrir-ganho");
        using var _1 = db; using var _2 = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 500m, null, default);
        db.ChangeTracker.Clear();

        await amb.Contatos.ReabrirAsync(amb.Cenario.Contato.Id, default);
        db.ChangeTracker.Clear();

        var todas = await db.Negociacoes.OrderBy(n => n.Id).ToListAsync();
        Assert.Equal(2, todas.Count);

        Assert.Equal(StatusNegociacao.Ganha, todas[0].Status);
        Assert.Equal(500m, todas[0].Valor);

        Assert.Equal(StatusNegociacao.Aberta, todas[1].Status);
        Assert.Null(todas[1].VendaId);

        // O faturamento não se mexeu, que é o ponto inteiro.
        Assert.Equal(500m, await db.Negociacoes
            .Where(n => n.Status == StatusNegociacao.Ganha || n.Status == StatusNegociacao.Concluida)
            .SumAsync(n => n.Valor ?? 0m));
    }

    // ==================================================================== concluir
    [Fact]
    public async Task CONCLUIR_A_VENDA_CONCLUI_A_NEGOCIACAO()
    {
        var (db, tx, amb) = await PrepararAsync("concluir");
        using var _1 = db; using var _2 = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 300m, null, default);
        db.ChangeTracker.Clear();

        var venda = await db.Vendas.SingleAsync();
        await amb.Vendas.ConcluirAsync([venda.Id], default);
        db.ChangeTracker.Clear();

        var negociacao = await db.Negociacoes.SingleAsync();
        Assert.Equal(StatusNegociacao.Concluida, negociacao.Status);
        Assert.NotNull(negociacao.ConcluidaEm);

        // ⚠️ E o `ganha_em` FICA. Concluir é sobre o pedido, não sobre o negócio: o dinheiro
        // continua contando, e é `ganha_em` que diz em qual mês.
        Assert.NotNull(negociacao.GanhaEm);
    }

    // ==================================================================== cancelar
    /// <summary>⚠️ ESTE É O TESTE QUE JUSTIFICA A COLUNA `venda_id`.
    ///
    /// Cancelar aceita uma venda ANTIGA — cliente que já comprou de novo. Sem o elo explícito, a
    /// única forma de achar a negociação espelho seria casar por (contato, valor, data), e o
    /// próprio `CancelarAsync` registra que essa tentativa já derrubou um teste: duas vendas no
    /// mesmo instante casavam as duas, e cancelar a antiga limpava o carimbo da nova.
    ///
    /// Aqui as duas vendas têm o MESMO valor e o MESMO relógio, que é o caso que quebra qualquer
    /// heurística.</summary>
    [Fact]
    public async Task CANCELAR_A_VENDA_ANTIGA_NAO_ENCOSTA_NA_RECENTE()
    {
        var (db, tx, amb) = await PrepararAsync("cancelar-antiga");
        using var _1 = db; using var _2 = tx;

        // Primeira compra, concluída — vira histórico.
        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 400m, null, default);
        db.ChangeTracker.Clear();
        var antiga = await db.Vendas.SingleAsync();
        await amb.Vendas.ConcluirAsync([antiga.Id], default);
        db.ChangeTracker.Clear();

        // O cliente volta e compra de novo, pelo MESMO valor e no MESMO instante do relógio falso.
        await amb.Contatos.ReabrirAsync(amb.Cenario.Contato.Id, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 400m, null, default);
        db.ChangeTracker.Clear();

        await amb.Vendas.CancelarAsync(antiga.Id, default);
        db.ChangeTracker.Clear();

        var espelhoAntigo = await db.Negociacoes.SingleAsync(n => n.VendaId == antiga.Id);
        Assert.Equal(StatusNegociacao.Cancelada, espelhoAntigo.Status);

        // A compra nova continua valendo. Se o cancelamento tivesse casado por timestamp, as duas
        // teriam sido canceladas e o faturamento cairia a zero.
        var recente = await db.Negociacoes
            .SingleAsync(n => n.VendaId != null && n.VendaId != antiga.Id);
        Assert.Equal(StatusNegociacao.Ganha, recente.Status);

        Assert.Equal(400m, await db.Negociacoes
            .Where(n => n.Status == StatusNegociacao.Ganha || n.Status == StatusNegociacao.Concluida)
            .SumAsync(n => n.Valor ?? 0m));
    }

    // ====================================================================
    private Task<(NexoraDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx,
        ContatosDbTests.Ambiente Amb)> PrepararAsync(string sufixo) =>
        ContatosDbTests.PrepararAsync(banco, $"espelho-{sufixo}");
}
