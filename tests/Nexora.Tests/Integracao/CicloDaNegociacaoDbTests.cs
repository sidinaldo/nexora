using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>O CICLO DE VIDA DE UMA NEGOCIAÇÃO: nascer, mover, ganhar, perder, reabrir, concluir.
///
/// ⚠️ ESTE ARQUIVO SE CHAMAVA `EspelhoNegociacaoDbTests`, e a troca de nome registra o fim de uma
/// fase. No E4b ele era o entregável: nada LIA `negociacoes` ainda, então a suíte ficaria verde
/// mesmo com o espelho completamente errado, e o defeito só apareceria no E4c como card sumido ou
/// faturamento trocado — longe da causa. Estes testes eram a única prova de que a escrita estava
/// certa enquanto ninguém lia.
///
/// Agora todo mundo lê. Não há espelho, não há duas metades para conferir uma contra a outra: os
/// mesmos testes passaram a descrever o comportamento do produto, e é por isso que eles
/// sobreviveram inteiros à morte da classe que lhes deu nome.
/// ====================================================================================</summary>
[Collection("banco")]
public class CicloDaNegociacaoDbTests(BancoTeste banco)
{
    // ==================================================================== abrir (E6)
    /// <summary>⚠️ O LEAD QUE CHEGA PELA CAIXA NAO TEM NEGOCIO, e o gesto de abrir um e o que o
    /// E6 entrega. Estes quatro testes cobrem o que o clique pode encontrar pela frente.</summary>
    [Fact]
    public async Task ABRIR_SEM_ESCOLHER_FUNIL_USA_O_PADRAO()
    {
        var (db, tx, amb) = await PrepararAsync("abrir-padrao");
        using var _1 = db; using var _2 = tx;

        // Um contato SEM negociacao — o estado de quem acabou de chegar pelo WhatsApp.
        var lead = new Contato
        {
            EmpresaId = amb.Cenario.Id, Nome = "Chegou pela caixa", Telefone = "5584977770001"
        };
        db.Contatos.Add(lead);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Contatos.AbrirNegociacaoAsync(lead.Id, null, default);
        db.ChangeTracker.Clear();

        var n = await db.Negociacoes.AsNoTracking().SingleAsync(x => x.ContatoId == lead.Id);
        Assert.Equal(StatusNegociacao.Aberta, n.Status);
        Assert.Equal(amb.Cenario.Pipeline.Id, n.PipelineId);
        Assert.Equal(amb.Cenario.PrimeiraEtapa.Id, n.EtapaId);
        Assert.Null(n.Valor);
    }

    [Fact]
    public async Task ABRIR_ESCOLHENDO_O_FUNIL_NASCE_NELE()
    {
        var (db, tx, amb) = await PrepararAsync("abrir-escolhido");
        using var _1 = db; using var _2 = tx;

        var outra = new Pipeline { EmpresaId = amb.Cenario.Id, Nome = "Pós-venda", Ordem = 2 };
        db.Pipelines.Add(outra);
        await db.SaveChangesAsync();

        var entrada = new EtapaFunil
        {
            EmpresaId = amb.Cenario.Id, PipelineId = outra.Id, Nome = "Recebido", Ordem = 1
        };
        db.EtapasFunil.Add(entrada);

        var lead = new Contato
        {
            EmpresaId = amb.Cenario.Id, Nome = "Vai para o pós-venda", Telefone = "5584977770002"
        };
        db.Contatos.Add(lead);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Contatos.AbrirNegociacaoAsync(lead.Id, outra.Id, default);
        db.ChangeTracker.Clear();

        var n = await db.Negociacoes.AsNoTracking().SingleAsync(x => x.ContatoId == lead.Id);
        Assert.Equal(outra.Id, n.PipelineId);
        Assert.Equal(entrada.Id, n.EtapaId);
    }

    /// <summary>⚠️ O `pipelineId` VEM DO CORPO DA REQUISICAO, e por isso precisa de filtro de
    /// tenant na leitura. Sem ele o id de outra empresa chegaria a `PrimeiraEtapaAsync` e o
    /// negocio nasceria no funil de outro cliente — a FK composta pegaria depois, como erro de
    /// banco, virando 500 numa tela em vez de "funil nao encontrado".</summary>
    [Fact]
    public async Task ABRIR_COM_FUNIL_DE_OUTRA_EMPRESA_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("abrir-alheio");
        using var _1 = db; using var _2 = tx;

        var alheia = await Semeador.TenantAsync(db, "abrir-vizinha");

        // ⚠️ UM LEAD SEM NEGOCIO, e nao o contato do cenario. A recusa por "ja esta em aberto"
        // roda ANTES da validacao do funil, entao usar o contato do cenario faria o teste passar
        // pelo motivo errado — verde sem nunca ter exercitado o filtro de tenant.
        var lead = new Contato
        {
            EmpresaId = amb.Cenario.Id, Nome = "Sem negócio", Telefone = "5584977770003"
        };
        db.Contatos.Add(lead);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.AbrirNegociacaoAsync(lead.Id, alheia.Pipeline.Id, default));

        Assert.Contains("não encontrado", erro.Message);
    }

    /// <summary>Dois cards da mesma pessoa no mesmo funil nao e estado que alguem pediu.</summary>
    [Fact]
    public async Task ABRIR_COM_NEGOCIO_JA_EM_ABERTO_DEVOLVE_CONFLITO()
    {
        var (db, tx, amb) = await PrepararAsync("abrir-duplicado");
        using var _1 = db; using var _2 = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, null, default));

        Assert.True(erro.Conflito);
    }

    /// <summary>⚠️ ESCOLHER FUNIL NAO REVIVE A PERDA, e a diferenca importa: quem escolheu esta
    /// dizendo para onde quer ir, e ressuscitar a negociacao noutro lugar contrariaria a escolha
    /// em silencio — a tela mostraria um funil e o card apareceria noutro.</summary>
    [Fact]
    public async Task ESCOLHER_FUNIL_ABRE_LINHA_NOVA_EM_VEZ_DE_REVIVER_A_PERDA()
    {
        var (db, tx, amb) = await PrepararAsync("abrir-perda-escolhida");
        using var _1 = db; using var _2 = tx;

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "achou caro", default);
        db.ChangeTracker.Clear();

        var outra = new Pipeline { EmpresaId = amb.Cenario.Id, Nome = "Atacado", Ordem = 2 };
        db.Pipelines.Add(outra);
        await db.SaveChangesAsync();
        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = amb.Cenario.Id, PipelineId = outra.Id, Nome = "Sondagem", Ordem = 1
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, outra.Id, default);
        db.ChangeTracker.Clear();

        var todas = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == amb.Cenario.Contato.Id)
            .OrderBy(n => n.Id).ToListAsync();

        Assert.Equal(2, todas.Count);

        // A perda CONTINUA perdida — ela e historico, e o relatorio de motivos conta com ela.
        Assert.Equal(StatusNegociacao.Perdida, todas[0].Status);
        Assert.Equal("achou caro", todas[0].MotivoPerda);

        Assert.Equal(StatusNegociacao.Aberta, todas[1].Status);
        Assert.Equal(outra.Id, todas[1].PipelineId);
    }

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

        // ⚠️ NAO HA MAIS CONTRA QUE COMPARAR (E4e/4). Ate aqui o teste conferia que a negociacao
        // espelhava as colunas do contato; elas nao existem, e o que se afirma agora e o valor
        // ABSOLUTO — a etapa de entrada do funil, que e onde todo lead novo nasce.
        Assert.Equal(StatusNegociacao.Aberta, negociacao.Status);
        Assert.Equal(amb.Cenario.PrimeiraEtapa.Id, negociacao.EtapaId);
        Assert.Equal(250m, negociacao.Valor);
        Assert.Equal(amb.Cenario.Pipeline.Id, negociacao.PipelineId);
    }

    // ==================================================================== editar
    /// <summary>⚠️ EDITAR O CONTATO TEM DE CHEGAR AO CARD, E NÃO CHEGAVA.
    ///
    /// Encontrado numa varredura, não por teste. Desde que o quadro passou a ler `negociacoes`,
    /// `valor` e `responsável` do card saem de lá — e `AtualizarAsync` mudava só `contatos`. O
    /// dono editava o valor na tela do contato, salvava, voltava ao quadro e via o número velho.
    /// Sem erro, sem aviso: só a tela discordando de si mesma.</summary>
    [Fact]
    public async Task EDITAR_O_CONTATO_ATUALIZA_O_CARD()
    {
        var (db, tx, amb) = await PrepararAsync("editar");
        using var _1 = db; using var _2 = tx;

        var c = amb.Cenario.Contato;

        await amb.Contatos.AtualizarAsync(c.Id, new EditarContato(
            c.Nome, c.Telefone, Valor: 4321m, ResponsavelId: null), default);
        db.ChangeTracker.Clear();

        var negociacao = await db.Negociacoes.SingleAsync();
        Assert.Equal(4321m, negociacao.Valor);
        Assert.Null(negociacao.ResponsavelId);

        // E o card mostra o número novo — que é o ponto.
        var card = (await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default))
            .Colunas.SelectMany(x => x.Contatos).Single(x => x.ContatoId == c.Id);
        Assert.Equal(4321m, card.Valor);
    }

    /// <summary>⚠️ A GANHA NÃO ACOMPANHA, e isso é deliberado: ela guarda o valor FECHADO e o
    /// vendedor que fechou. Reescrevê-los a partir de uma tela de cadastro mudaria histórico de
    /// faturamento — o mesmo erro que o NEG-1 corrigiu ao tirar a venda da coluna do contato.</summary>
    [Fact]
    public async Task EDITAR_O_CONTATO_NAO_REESCREVE_O_NEGOCIO_JA_GANHO()
    {
        var (db, tx, amb) = await PrepararAsync("editar-ganho");
        using var _1 = db; using var _2 = tx;

        var c = amb.Cenario.Contato;
        await amb.Contatos.MarcarGanhoAsync(c.Id, 900m, null, default);
        db.ChangeTracker.Clear();

        await amb.Contatos.AtualizarAsync(c.Id, new EditarContato(
            c.Nome, c.Telefone, Valor: 1m, ResponsavelId: null), default);
        db.ChangeTracker.Clear();

        var ganha = await db.Negociacoes.SingleAsync(n => n.Status == StatusNegociacao.Ganha);
        Assert.Equal(900m, ganha.Valor);

        // O faturamento não se mexeu, que é o que está em jogo.
        Assert.Equal(900m, await db.Negociacoes
            .Where(n => n.Status == StatusNegociacao.Ganha || n.Status == StatusNegociacao.Concluida)
            .SumAsync(n => n.Valor ?? 0m));
    }

    // ==================================================================== arrastar
    [Fact]
    public async Task ARRASTAR_O_CARD_MOVE_A_NEGOCIACAO_JUNTO()
    {
        var (db, tx, amb) = await PrepararAsync("arrastar");
        using var _1 = db; using var _2 = tx;

        var destino = amb.Cenario.Etapas[1];
        var ordem = await amb.Funil.MoverAsync(
            amb.Cenario.Negociacao.Id, new MoverContato(destino.Id, null), default);
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
    public async Task REGISTRAR_A_VENDA_TRANSFORMA_A_ABERTA_EM_GANHA()
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

        // ⚠️ NAO HA MAIS ELO A CONFERIR (E4e/2). A linha da venda deixou de existir: o elo
        // `venda_id` ligava duas tabelas, e agora ha uma so. `Concluir` e `Cancelar` recebem o
        // id DESTA negociacao direto.
        //
        // O que sobrou para provar e que ela parou na etapa de ganho, que era o papel do
        // `vendas.etapa_id` — o registro de ONDE o negocio fechou.
        var ganho = amb.Cenario.Etapas.Single(e => e.EGanho);
        Assert.Equal(ganho.Id, negociacao.EtapaId);
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

        var etapaAntes = amb.Cenario.Negociacao.EtapaId;

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
        await amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, null, default);
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

        await amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, null, default);
        db.ChangeTracker.Clear();

        var todas = await db.Negociacoes.OrderBy(n => n.Id).ToListAsync();
        Assert.Equal(2, todas.Count);

        Assert.Equal(StatusNegociacao.Ganha, todas[0].Status);
        Assert.Equal(500m, todas[0].Valor);

        Assert.Equal(StatusNegociacao.Aberta, todas[1].Status);

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

        var venda = await db.Negociacoes.SingleAsync();
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
    /// Cancelar aceita uma venda ANTIGA — cliente que já comprou de novo. O gesto tem que atingir
    /// exatamente aquela, e nenhuma outra.
    ///
    /// ⚠️ As duas compras têm o MESMO valor e o MESMO instante do relógio falso, que é o caso que
    /// quebra qualquer heurística. Antes do E4e isto exigia o elo `venda_id`, porque casar por
    /// (contato, valor, data) cancelava as duas — e o `CancelarAsync` registra que essa tentativa
    /// já derrubou um teste de verdade.
    ///
    /// Agora o id que chega É o da negociação, e o problema deixa de existir: não há duas tabelas
    /// para casar. O teste fica porque a armadilha continua sendo real para quem mexer aqui.</summary>
    [Fact]
    public async Task CANCELAR_A_VENDA_ANTIGA_NAO_ENCOSTA_NA_RECENTE()
    {
        var (db, tx, amb) = await PrepararAsync("cancelar-antiga");
        using var _1 = db; using var _2 = tx;

        // Primeira compra, concluída — vira histórico.
        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 400m, null, default);
        db.ChangeTracker.Clear();
        var antiga = await db.Negociacoes.SingleAsync();
        await amb.Vendas.ConcluirAsync([antiga.Id], default);
        db.ChangeTracker.Clear();

        // O cliente volta e compra de novo, pelo MESMO valor e no MESMO instante do relógio falso.
        await amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, null, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 400m, null, default);
        db.ChangeTracker.Clear();

        await amb.Vendas.CancelarAsync(antiga.Id, default);
        db.ChangeTracker.Clear();

        Assert.Equal(StatusNegociacao.Cancelada,
            (await db.Negociacoes.SingleAsync(n => n.Id == antiga.Id)).Status);

        // A compra nova continua valendo. Se o cancelamento tivesse atingido as duas, o
        // faturamento cairia a zero.
        var recente = await db.Negociacoes
            .SingleAsync(n => n.Status == StatusNegociacao.Ganha);
        Assert.NotEqual(antiga.Id, recente.Id);
        Assert.Equal(400m, recente.Valor);

        Assert.Equal(400m, await db.Negociacoes
            .Where(n => n.Status == StatusNegociacao.Ganha || n.Status == StatusNegociacao.Concluida)
            .SumAsync(n => n.Valor ?? 0m));
    }

    // ====================================================================
    private Task<(NexoraDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx,
        ContatosDbTests.Ambiente Amb)> PrepararAsync(string sufixo) =>
        ContatosDbTests.PrepararAsync(banco, $"espelho-{sufixo}");
}
