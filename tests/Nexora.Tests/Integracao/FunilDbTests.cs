using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O quadro kanban: posicionamento por ponto médio, renormalização e paginação.
///
/// O cálculo de ordem parece trivial e não é: as três bordas (topo, fim, coluna vazia) e o
/// esgotamento de precisão são justamente onde ele quebra — e quebra em silêncio, num card só,
/// meses depois.</summary>
[Collection("banco")]
public class FunilDbTests(BancoTeste banco)
{
    // ==================================================================== ponto médio
    [Fact]
    public async Task Mover_entre_dois_cards_calcula_o_PONTO_MEDIO()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "meio");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.PrimeiraEtapa.Id;
        var a = await CardAsync(db, amb, "A", etapa, 10m);
        var b = await CardAsync(db, amb, "B", etapa, 20m);
        var c = await CardAsync(db, amb, "C", amb.Cenario.Etapas[1].Id, 1m);

        var nova = (await amb.Funil.MoverAsync(c, new MoverContato(etapa, AposNegociacaoId: a), default)).OrdemKanban;

        Assert.Equal(15m, nova);
        Assert.Equal([a, c, b], await OrdemDaColunaAsync(db, etapa, ignorar: amb.Cenario.Negociacao.Id));
    }

    [Fact]
    public async Task Mover_para_o_TOPO_da_coluna()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "topo");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.Etapas[1].Id;
        var a = await CardAsync(db, amb, "A", etapa, 10m);
        await CardAsync(db, amb, "B", etapa, 20m);
        var c = await CardAsync(db, amb, "C", amb.Cenario.PrimeiraEtapa.Id, 1m);

        // AposContatoId null = soltou no topo.
        var nova = (await amb.Funil.MoverAsync(c, new MoverContato(etapa, null), default)).OrdemKanban;

        Assert.Equal(9m, nova);   // primeira - 1
        Assert.Equal(c, (await OrdemDaColunaAsync(db, etapa))[0]);
        Assert.True(nova < 10m);
        Assert.NotEqual(a, (await OrdemDaColunaAsync(db, etapa))[0]);
    }

    [Fact]
    public async Task Mover_para_o_FIM_da_coluna()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "fim");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.Etapas[1].Id;
        await CardAsync(db, amb, "A", etapa, 10m);
        var b = await CardAsync(db, amb, "B", etapa, 20m);
        var c = await CardAsync(db, amb, "C", amb.Cenario.PrimeiraEtapa.Id, 1m);

        var nova = (await amb.Funil.MoverAsync(c, new MoverContato(etapa, AposNegociacaoId: b), default)).OrdemKanban;

        Assert.Equal(21m, nova);   // última + 1
        Assert.Equal(c, (await OrdemDaColunaAsync(db, etapa))[^1]);
    }

    [Fact]
    public async Task Mover_para_COLUNA_VAZIA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "vazia");
        using var _ = db; using var __ = tx;

        var vazia = amb.Cenario.Etapas[1].Id;
        Assert.Empty(await OrdemDaColunaAsync(db, vazia));

        var nova = (await amb.Funil.MoverAsync(
            amb.Cenario.Negociacao.Id, new MoverContato(vazia, null), default)).OrdemKanban;

        Assert.Equal(0m, nova);
        Assert.Equal([amb.Cenario.Negociacao.Id], await OrdemDaColunaAsync(db, vazia));
    }

    [Fact]
    public async Task Reordenar_DENTRO_da_mesma_coluna_nao_considera_a_posicao_antiga_do_proprio_card()
    {
        // Se o card não saísse da conta, o "meio" seria calculado contra ele mesmo e a nova ordem
        // sairia colada na antiga — o card não se moveria de lugar.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "mesma-coluna");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.PrimeiraEtapa.Id;
        var a = await CardAsync(db, amb, "A", etapa, 10m);
        var b = await CardAsync(db, amb, "B", etapa, 20m);
        var c = await CardAsync(db, amb, "C", etapa, 30m);

        // C sobe para entre A e B.
        var nova = (await amb.Funil.MoverAsync(c, new MoverContato(etapa, AposNegociacaoId: a), default)).OrdemKanban;

        Assert.Equal(15m, nova);
        Assert.Equal([a, c, b], await OrdemDaColunaAsync(db, etapa, ignorar: amb.Cenario.Negociacao.Id));
    }

    // ==================================================================== renormalização
    [Fact]
    public async Task RENORMALIZA_quando_a_precisao_se_esgota_e_PRESERVA_a_ordem_relativa()
    {
        // ===================== A ARMADILHA DO PONTO MÉDIO =====================
        // Dividir ao meio entre os mesmos vizinhos encolhe o intervalo exponencialmente. Sem
        // renormalizar, chega um ponto em que o card simplesmente para de aceitar reordenação —
        // sem erro, sem log. Este teste força o intervalo a ficar abaixo do limiar.
        // ======================================================================
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "renormaliza");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.Etapas[1].Id;
        var a = await CardAsync(db, amb, "A", etapa, 1m);
        var b = await CardAsync(db, amb, "B", etapa, 1.000001m);   // colado em A: 1e-6 < 2e-6
        var d = await CardAsync(db, amb, "D", etapa, 8m);
        var c = await CardAsync(db, amb, "C", amb.Cenario.PrimeiraEtapa.Id, 1m);

        var nova = (await amb.Funil.MoverAsync(c, new MoverContato(etapa, AposNegociacaoId: a), default)).OrdemKanban;

        db.ChangeTracker.Clear();
        // Lido de `negociacoes`: desde o E4c/2 é ela quem carrega a posição, e `CardAsync`
        // devolve o id dela.
        var ordens = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.EtapaId == etapa)
            .OrderBy(x => x.OrdemKanban).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.OrdemKanban })
            .ToListAsync();

        // A coluna foi reescrita como 1, 2, 3… e o card entrou no meio do primeiro par.
        Assert.Equal(1m, ordens.Single(o => o.Id == a).OrdemKanban);
        Assert.Equal(1.5m, nova);
        Assert.Equal(2m, ordens.Single(o => o.Id == b).OrdemKanban);
        Assert.Equal(3m, ordens.Single(o => o.Id == d).OrdemKanban);

        // A ORDEM RELATIVA sobreviveu: A antes de B antes de D, com C no meio do primeiro par.
        Assert.Equal([a, c, b, d], ordens.Select(o => o.Id).ToArray());

        // E o intervalo voltou a ser utilizável.
        Assert.True(ordens.Zip(ordens.Skip(1)).All(p => p.Second.OrdemKanban - p.First.OrdemKanban >= 0.5m));
    }

    [Fact]
    public async Task Renormalizar_nao_move_card_perdido_de_volta_para_o_quadro()
    {
        // O card perdido está fora do quadro pelo índice parcial. Se a renormalização o
        // incluísse, ele receberia ordem nova e voltaria a competir por posição com os vivos.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "renorm-perdido");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.Etapas[1].Id;
        var a = await CardAsync(db, amb, "A", etapa, 1m);
        await CardAsync(db, amb, "B", etapa, 1.000001m);
        var morto = await CardAsync(db, amb, "Morto", etapa, 2m);
        await amb.Contatos.MarcarPerdidoAsync(await ContatoDoCardAsync(db, morto), "sumiu", null, default);
        db.ChangeTracker.Clear();

        var c = await CardAsync(db, amb, "C", amb.Cenario.PrimeiraEtapa.Id, 1m);
        await amb.Funil.MoverAsync(c, new MoverContato(etapa, AposNegociacaoId: a), default);

        db.ChangeTracker.Clear();
        var perdido = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == morto);
        Assert.Equal(2m, perdido.OrdemKanban);   // intocado
    }

    // ==================================================================== a porta única do ganho
    [Fact]
    public async Task MOVER_PARA_A_ETAPA_DE_GANHO_E_RECUSADO_com_mensagem_que_orienta()
    {
        // ===================== POR QUE ESTA RECUSA EXISTE =====================
        // Se `mover` aceitasse a etapa de ganho, existiria contato na coluna "Venda" sem
        // `ganho_em` e sem `valor`. O card estaria na tela e a venda NÃO existiria no dashboard,
        // que conta por `ganho_em`. As duas portas do ganho precisam escrever pela mesma rota.
        // ======================================================================
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "recusa-ganho");
        using var _ = db; using var __ = tx;

        var etapaGanho = amb.Cenario.Etapas.Single(e => e.EGanho).Id;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(
                amb.Cenario.Negociacao.Id, new MoverContato(etapaGanho, null), default));

        Assert.True(erro.Conflito);
        Assert.Contains("registre a venda", erro.Message, StringComparison.OrdinalIgnoreCase);

        // E o NEGÓCIO não se moveu.
        db.ChangeTracker.Clear();
        var n = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == amb.Cenario.Negociacao.Id);
        Assert.NotEqual(etapaGanho, n.EtapaId);
        Assert.Equal(StatusNegociacao.Aberta, n.Status);
    }

    [Fact]
    public async Task Contato_perdido_nao_pode_ser_movido_sem_reabrir()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "mover-perdido");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "desistiu", null, default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(
                amb.Cenario.Negociacao.Id, new MoverContato(amb.Cenario.Etapas[1].Id, null), default));

        Assert.True(erro.Conflito);
    }

    // ==================================================================== multi-tenant
    [Fact]
    public async Task Mover_para_etapa_de_OUTRA_empresa_e_recusado()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "mover-alheio");
        using var _ = db; using var __ = tx;

        var alheia = await Semeador.TenantAsync(db, "mover-alheio-vizinha");
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(
                amb.Cenario.Negociacao.Id, new MoverContato(alheia.PrimeiraEtapa.Id, null), default));

        Assert.Contains("não encontrada", erro.Message);

        // O negócio continua onde estava — não saiu do funil da própria empresa.
        db.ChangeTracker.Clear();
        Assert.Equal(amb.Cenario.PrimeiraEtapa.Id, await db.Negociacoes.IgnoreQueryFilters()
            .AsNoTracking().Where(x => x.Id == amb.Cenario.Negociacao.Id)
            .Select(x => x.EtapaId).SingleAsync());
    }

    [Fact]
    public async Task Card_de_referencia_de_outra_coluna_e_recusado()
    {
        // Calcular o "meio" entre vizinhos de colunas diferentes produziria uma ordem sem sentido.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "referencia-errada");
        using var _ = db; using var __ = tx;

        var outraColuna = await CardAsync(db, amb, "Outro", amb.Cenario.Etapas[1].Id, 5m);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(
                amb.Cenario.Negociacao.Id,
                new MoverContato(amb.Cenario.PrimeiraEtapa.Id, AposNegociacaoId: outraColuna),
                default));

        Assert.True(erro.Conflito);
        Assert.Contains("Recarregue", erro.Message);
    }

    // ==================================================================== quadro
    [Fact]
    public async Task Quadro_pagina_por_coluna_e_devolve_a_contagem_do_conjunto_INTEIRO()
    {
        // Uma empresa com 3.000 leads em "Novo Lead" derrubaria a tela se a coluna viesse inteira.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "quadro-pagina");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.PrimeiraEtapa.Id;
        for (var i = 0; i < 5; i++)
            await CardAsync(db, amb, $"Card {i}", etapa, 10m + i, valor: 100m);

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, porColuna: 2, ct: default);
        var primeira = quadro.Colunas.Single(c => c.EtapaId == etapa);

        Assert.Equal(6, primeira.Total);          // 5 criados + o do Semeador
        Assert.Equal(2, primeira.Contatos.Count); // mas só 2 carregados
        Assert.True(primeira.TemMais);
        Assert.Equal(500m, primeira.ValorTotal);  // soma do CONJUNTO, não da página
    }

    [Fact]
    public async Task Coluna_pagina_por_cursor_sem_pular_nem_repetir()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "coluna-cursor");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.Etapas[1].Id;
        for (var i = 0; i < 5; i++)
            await CardAsync(db, amb, $"Card {i}", etapa, 10m + i);

        var p1 = await amb.Funil.ColunaAsync(etapa, null, null, 2, default);
        Assert.Equal(2, p1.Itens.Count);
        Assert.True(p1.TemMais);

        var ultimo = p1.Itens[^1];
        var p2 = await amb.Funil.ColunaAsync(etapa, ultimo.OrdemKanban, ultimo.Id, 2, default);

        Assert.Equal(2, p2.Itens.Count);
        Assert.Empty(p1.Itens.Select(i => i.Id).Intersect(p2.Itens.Select(i => i.Id)));

        var p3 = await amb.Funil.ColunaAsync(etapa, p2.Itens[^1].OrdemKanban, p2.Itens[^1].Id, 2, default);
        Assert.Single(p3.Itens);
        Assert.False(p3.TemMais);
    }

    [Fact]
    public async Task Perdido_e_anonimizado_somem_do_quadro_e_da_contagem()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "quadro-some");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.PrimeiraEtapa.Id;
        var perdido = await CardAsync(db, amb, "Perdido", etapa, 10m);
        var anonimo = await CardAsync(db, amb, "Anonimo", etapa, 11m);

        await amb.Contatos.MarcarPerdidoAsync(await ContatoDoCardAsync(db, perdido), "sumiu", null, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.AnonimizarAsync(await ContatoDoCardAsync(db, anonimo), default);
        db.ChangeTracker.Clear();

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var coluna = quadro.Colunas.Single(c => c.EtapaId == etapa);

        Assert.Equal(1, coluna.Total);   // só o do Semeador
        Assert.DoesNotContain(coluna.Contatos, c => c.Id == perdido || c.Id == anonimo);
    }

    [Fact]
    public async Task Quadro_traz_as_etapas_em_ordem_e_marca_qual_e_a_de_ganho()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "quadro-etapas");
        using var _ = db; using var __ = tx;

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);

        Assert.Equal(3, quadro.Colunas.Count);
        Assert.Equal([1, 2, 3], quadro.Colunas.Select(c => (int)c.Ordem).ToArray());
        Assert.Single(quadro.Colunas.Where(c => c.EGanho));
    }

    [Fact]
    public async Task Quadro_de_outra_empresa_nao_vaza()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "quadro-tenant");
        using var _ = db; using var __ = tx;

        var alheia = await Semeador.TenantAsync(db, "quadro-tenant-vizinha");
        db.ChangeTracker.Clear();

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);

        Assert.DoesNotContain(quadro.Colunas, c => c.EtapaId == alheia.PrimeiraEtapa.Id);
        Assert.DoesNotContain(
            quadro.Colunas.SelectMany(c => c.Contatos), c => c.Id == alheia.Contato.Id);
    }

    // ==================================================================== totais (AUD-1)
    /// <summary>O cabeçalho das colunas depois de um arrasto vem do servidor. A tela tirava 1 da
    /// origem e somava 1 no destino por conta própria, e a coluna de origem nunca era relida.
    ///
    /// ⚠️ O CARD QUE OUTRA PESSOA PÔS NA ORIGEM é o que separa "contado no banco" de "conta da
    /// tela": a conta dela daria o número de antes menos 1, e o banco dá o de antes.</summary>
    [Fact]
    public async Task MOVER_DEVOLVE_OS_NUMEROS_DAS_DUAS_COLUNAS_CONTADOS_NO_BANCO()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "mover-totais");
        using var _ = db; using var __ = tx;

        var origem = amb.Cenario.PrimeiraEtapa.Id;
        var destino = amb.Cenario.Etapas[1].Id;
        var arrastado = await CardAsync(db, amb, "Arrastado", origem, 10m, valor: 250m);
        var vizinho = await CardAsync(db, amb, "Vizinho", destino, 10m, valor: 100m);

        var antes = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var origemAntes = antes.Colunas.Single(c => c.EtapaId == origem);

        // Outra pessoa põe um card na origem depois que a tela carregou.
        await CardAsync(db, amb, "De outra pessoa", origem, 30m, valor: 40m);

        var r = await amb.Funil.MoverAsync(arrastado, new MoverContato(destino, vizinho), default);

        Assert.Equal([origem, destino], r.Colunas.Select(c => c.EtapaId).ToArray());

        var naOrigem = r.Colunas[0];
        Assert.Equal(origemAntes.Total, naOrigem.Total);
        Assert.Equal(origemAntes.ValorTotal - 250m + 40m, naOrigem.ValorTotal);

        var noDestino = r.Colunas[1];
        Assert.Equal(2, noDestino.Total);
        Assert.Equal(350m, noDestino.ValorTotal);

        // E são os MESMOS números que o quadro mostra ao ser aberto de novo.
        var depois = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        foreach (var t in r.Colunas)
        {
            var coluna = depois.Colunas.Single(c => c.EtapaId == t.EtapaId);
            Assert.Equal(coluna.Total, t.Total);
            Assert.Equal(coluna.ValorTotal, t.ValorTotal);
            Assert.Equal(coluna.Concluidas, t.Concluidas);
        }
    }

    [Fact]
    public async Task REORDENAR_NA_MESMA_COLUNA_DEVOLVE_SO_ESSA_COLUNA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "mover-totais-mesma");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.Etapas[1].Id;
        var a = await CardAsync(db, amb, "A", etapa, 10m, valor: 30m);
        var b = await CardAsync(db, amb, "B", etapa, 20m, valor: 70m);

        var r = await amb.Funil.MoverAsync(a, new MoverContato(etapa, b), default);

        var unica = Assert.Single(r.Colunas);
        Assert.Equal(etapa, unica.EtapaId);
        Assert.Equal(2, unica.Total);
        Assert.Equal(100m, unica.ValorTotal);
    }

    /// <summary>A página de uma coluna traz o cabeçalho da coluna INTEIRA — é dela que a tela tira
    /// os números quando relê a coluna depois de um arrasto (AUD-1).</summary>
    [Fact]
    public async Task A_PAGINA_DA_COLUNA_TRAZ_OS_TOTAIS_DA_COLUNA_INTEIRA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "coluna-totais");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.Etapas[1].Id;
        for (var i = 0; i < 5; i++)
        {
            await CardAsync(db, amb, $"Card {i}", etapa, 10m + i, valor: 10m);
        }
        // Sem valor: não soma nada, e conta no total.
        await CardAsync(db, amb, "Sem valor", etapa, 99m);

        var pagina = await amb.Funil.ColunaAsync(etapa, null, null, 2, default);

        Assert.Equal(2, pagina.Itens.Count);
        Assert.True(pagina.TemMais);
        Assert.Equal(6, pagina.Total);
        Assert.Equal(50m, pagina.ValorTotal);
        Assert.Equal(0, pagina.Concluidas);
    }

    /// <summary>A coluna de outra empresa não devolve card nem número — e o teste confere antes que
    /// ela TEM negócio, senão o zero seria de graça.</summary>
    [Fact]
    public async Task A_COLUNA_DE_OUTRA_EMPRESA_DEVOLVE_ZERO_E_NENHUM_CARD()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "coluna-totais-tenant");
        using var _ = db; using var __ = tx;

        var alheia = await Semeador.TenantAsync(db, "coluna-totais-tenant-vizinha");
        db.ChangeTracker.Clear();

        var negociosDela = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(n => n.EtapaId == alheia.PrimeiraEtapa.Id);
        Assert.True(negociosDela > 0, "a etapa da outra empresa deveria ter negócio");

        var pagina = await amb.Funil.ColunaAsync(alheia.PrimeiraEtapa.Id, null, null, 50, default);

        Assert.Empty(pagina.Itens);
        Assert.Equal(0, pagina.Total);
        Assert.Equal(0m, pagina.ValorTotal);
        Assert.Equal(0, pagina.Concluidas);
    }

    /// <summary>===================== O ÚNICO JEITO DE PÔR UM ABERTO NA ETAPA DE GANHO =====================
    ///
    /// A cláusula `!EGanho || Status == Ganha` existe nos dois serviços, e eu a trouxe para o eixo
    /// novo do painel. Sabotei-a e NENHUM teste caiu — nem o de paridade logo abaixo. Fui atrás do
    /// porquê antes de dar a regra por coberta.
    ///
    /// Arrastar para a etapa de ganho é recusado (`RegrasDoQuadro.Recusa`, regra 1: "A etapa de
    /// venda só recebe negócio com valor fechado"), e a mesma porta vale para a criação. Por ali o
    /// estado é inalcançável, e a suíte inteira só conhecia esses caminhos.
    ///
    /// ⚠️ MAS HÁ UMA PORTA QUE NÃO PASSA PELO QUADRO: o dono marcar como ganho uma etapa QUE JÁ
    /// TEM CARDS ABERTOS. `DefinirGanhoAsync` só exige que card VENDIDO não fique para trás
    /// (POS-1); dos abertos que já estão lá ele não trata — e nem deveria recusar, porque "agora
    /// quem fecha é Proposta" é uma decisão legítima. Num clique, todo aberto daquela coluna vira
    /// "aberto na etapa de ganho".
    ///
    /// A partir daí o quadro esconde esses cards e o painel precisa esconder também. Sem a
    /// cláusula, o painel conta um negócio que o quadro não mostra — e o dono vê dois números
    /// diferentes para a mesma pergunta, que é o defeito de 72-contra-69 voltando por outra porta.
    /// =============================================================================================</summary>
    [Fact]
    public async Task PROMOVER_A_GANHO_UMA_ETAPA_COM_ABERTOS_NAO_FAZ_PAINEL_E_QUADRO_DISCORDAREM()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "ganho-promovido");
        using var _ = db; using var __ = tx;

        // O `Semeador` já deixa um aberto na PRIMEIRA etapa, sem valor — ele é o controle: tem de
        // continuar contando dos dois lados depois da promoção.
        var segunda = amb.Cenario.Etapas[1];
        await CardAsync(db, amb, "aberto na futura etapa de ganho", segunda.Id, 1000m, 900m);

        // A promoção, PELO SERVIÇO de verdade. Nenhum card vendido existe, então o guarda do POS-1
        // deixa passar — que é justamente o caso em que esta porta se abre.
        await new ServicoEtapas(db, amb.Contexto).DefinirGanhoAsync(segunda.Id, default);
        db.ChangeTracker.Clear();

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var linha = (await amb.Dashboard.DashboardAsync(default)).Funil
            .Single(f => f.PipelineId == amb.Cenario.Pipeline.Id);

        // ⚠️ OS NÚMEROS ESPERADOS VÊM PRIMEIRO. Só comparar quadro com painel passaria com os dois
        //    igualmente errados — e os dois leem a mesma cláusula, então errar junto é o provável.
        Assert.Equal(1, linha.EmNegociacao);      // só o do Semeador; o de 900 saiu ao virar ganho
        Assert.Equal(0m, linha.ValorEmAberto);

        Assert.Equal(quadro.Colunas.Sum(col => col.Total), linha.EmNegociacao);
        Assert.Equal(quadro.Colunas.Sum(col => col.ValorTotal), linha.ValorEmAberto);
    }

    // ==================================================================== contagem única
    [Fact]
    public async Task A_CONTAGEM_DO_DASHBOARD_BATE_COM_A_DO_QUADRO()
    {
        // ===================== O BUG QUE ISTO IMPEDE DE VOLTAR =====================
        // O predicado estava escrito por extenso nos dois serviços e divergiu: o quadro filtrava
        // perdido E anonimizado; o dashboard, só perdido. O cliente via 72 em "Proposta" no
        // dashboard e contava 69 cards no quadro.
        //
        // Ele não conclui "há um filtro divergente" — conclui que os NÚMEROS DO SISTEMA NÃO SÃO
        // CONFIÁVEIS. Num produto que vende controle de dados, é o pior tipo de bug.
        //
        // A correção foi pôr o predicado numa `Expression` só, e este teste é a garantia de que
        // os dois continuam usando: ele compara as duas leituras REAIS, não o predicado.
        //
        // ⚠️ A FONTE ÚNICA VOLTOU NO E4d, agora do lado da negociação. Entre o E4c e o E4d esta
        // garantia ficou mais fraca por um intervalo: o quadro já lia `negociacoes` e o dashboard
        // ainda lia `contatos` — duas TABELAS respondendo à mesma pergunta, mantidas de acordo só
        // pelo espelho. Hoje os dois usam a MESMA `Expression` (`RegrasNegociacao.NoQuadro`)
        // sobre a MESMA tabela, e este teste voltou a comparar duas leituras da mesma verdade.
        // ==========================================================================
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "contagem-unica");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.Etapas[0].Id;
        var outra = amb.Cenario.Etapas[1].Id;

        // Ativos: entram nas duas contagens.
        await CardAsync(db, amb, "ativo 1", etapa, 1000m, 100m);
        await CardAsync(db, amb, "ativo 2", etapa, 2000m, 250m);
        await CardAsync(db, amb, "ativo 3", outra, 1000m, 900m);

        // ⚠️ PELOS SERVIÇOS, e não com `ExecuteUpdate` nas colunas de `contatos`.
        //
        // Desde o E4c o quadro lê `negociacoes` e o dashboard ainda lê `contatos`. Carimbar
        // `perdido_em` na mão deixava a negociação aberta, e o teste acusava "quadro 4, dashboard
        // 3" — uma divergência que este teste existe para pegar, mas causada pela fixture.
        //
        // Usar os serviços é o que o teste sempre quis medir: as duas leituras REAIS depois de
        // uma operação REAL.

        // Perdido: sai das duas. O negócio acabou.
        var perdido = await CardAsync(db, amb, "perdido", etapa, 3000m, 500m);
        await amb.Contatos.MarcarPerdidoAsync(
            await ContatoDoCardAsync(db, perdido), "Comprou com concorrente", null, default);

        // Anonimizado: sai das duas. Era o lado que o dashboard esquecia.
        var anonimo = await CardAsync(db, amb, "anonimizado", etapa, 4000m, 700m);
        await amb.Contatos.AnonimizarAsync(await ContatoDoCardAsync(db, anonimo), default);

        db.ChangeTracker.Clear();

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var dashboard = await amb.Dashboard.DashboardAsync(default);

        // ===================== A PARIDADE MUDOU DE EIXO (FUN-1) =====================
        // Era coluna a coluna, porque o painel desenhava ETAPAS. Ele passou a desenhar uma linha
        // por FUNIL, então o que se compara agora é o quadro inteiro daquele funil contra a linha
        // dele.
        //
        // ⚠️ É AQUI QUE A REGRA DA ETAPA DE GANHO É GUARDADA NO EIXO NOVO. Por etapa ela era
        // `!e.EGanho || n.Status == Ganha`; somando o funil, passou a ser perguntada A CADA
        // NEGOCIAÇÃO, pela etapa DELA (`!n.Etapa.EGanho || ...`). Perdê-la faz a coluna de ganho
        // voltar a acumular para sempre e os dois números divergirem — e é exatamente o que uma
        // sabotagem dessa cláusula derruba aqui.
        //
        // ⚠️ O QUE SE PERDEU, DITO POR EXTENSO: um total agregado igual pode esconder duas
        // diferenças que se cancelam entre colunas. Essa granularidade saiu do PAINEL, não do
        // produto — a "foto" do relatório continua etapa a etapa, e a paridade coluna a coluna
        // mudou de casa, não sumiu:
        // `RelatoriosDbTests.A_FOTO_DO_RELATORIO_BATE_COM_O_QUADRO_COLUNA_A_COLUNA`.
        // ===========================================================================
        var linha = dashboard.Funil.Single(f => f.PipelineId == amb.Cenario.Pipeline.Id);

        Assert.True(quadro.Colunas.Sum(col => col.Total) == linha.EmNegociacao,
            $"quadro {quadro.Colunas.Sum(col => col.Total)}, painel {linha.EmNegociacao}.");
        Assert.True(quadro.Colunas.Sum(col => col.ValorTotal) == linha.ValorEmAberto,
            $"quadro {quadro.Colunas.Sum(col => col.ValorTotal):C}, painel {linha.ValorEmAberto:C}.");

        // E os números são os ESPERADOS, não apenas iguais: dois serviços igualmente errados
        // passariam na comparação acima.
        //
        // São 3 e não 2 porque o `Semeador` já deixa um contato na primeira etapa — sem valor,
        // por isso a soma continua 100 + 250.
        var primeira = quadro.Colunas.Single(c => c.EtapaId == etapa);
        Assert.Equal(3, primeira.Total);
        Assert.Equal(350m, primeira.ValorTotal);

        // E os cards carregados batem com o total anunciado no cabeçalho da coluna.
        Assert.Equal(primeira.Total, primeira.Contatos.Count);
    }

    // ==================================================================== o quadro le negociacoes
    /// <summary>⚠️ UM CARD POR PESSOA NO FUNIL, DO COMEÇO AO FIM DO CICLO.
    ///
    /// ⚠️ ESTE TESTE AFIRMAVA O CONTRÁRIO, e chamava-se `..._DEIXA_DOIS_CARDS_UM_EM_CADA_COLUNA`.
    /// Ele descrevia o E4c/2 com orgulho: reabrir deixava a ganha esperando conclusão E a nova
    /// aberta, duas linhas vivas que o modelo velho não sabia representar. Na tela o resultado
    /// foi a mesma pessoa em duas etapas do mesmo funil, e o relato foi "Ysia ficou duas vezes no
    /// mesmo funil".
    ///
    /// O que o E4c/2 destravou continua valendo e continua afirmado aqui: cada card carrega a
    /// PRÓPRIA negociação, com id próprio e valor próprio. O que mudou é quantos cards a mesma
    /// pessoa pode ter neste funil ao mesmo tempo — um.
    ///
    /// O ciclo inteiro em três leituras do quadro: ganhou, concluiu, voltou a negociar.</summary>
    [Fact]
    public async Task O_CICLO_INTEIRO_NUNCA_DEIXA_DOIS_CARDS_DA_MESMA_PESSOA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "um-card");
        using var _ = db; using var __ = tx;

        var id = amb.Cenario.Contato.Id;
        var funil = amb.Cenario.Pipeline.Id;

        async Task<List<CardFunil>> CardsAsync()
        {
            var q = await amb.Funil.QuadroAsync(funil, 50, default);
            return q.Colunas.SelectMany(c => c.Contatos).Where(c => c.ContatoId == id).ToList();
        }

        // ---------- 1. ganhou: o card ANDOU para a coluna de ganho, não se duplicou
        var aberta = (await CardsAsync()).Single();

        await amb.Contatos.MarcarGanhoAsync(id, 800m, null, null, default);
        db.ChangeTracker.Clear();

        var quadroGanho = await amb.Funil.QuadroAsync(funil, 50, default);
        var ganho = quadroGanho.Colunas.Single(c => c.EGanho);
        var naGanho = Assert.Single(ganho.Contatos, c => c.ContatoId == id);

        // É A MESMA LINHA que mudou de coluna: o id do card não trocou.
        Assert.Equal(aberta.Id, naGanho.Id);
        Assert.Equal(800m, naGanho.Valor);
        Assert.Equal(800m, ganho.ValorTotal);
        Assert.Single(quadroGanho.Colunas.SelectMany(c => c.Contatos).Where(c => c.ContatoId == id));

        // ---------- 2. concluiu: o pedido SAI do quadro, e com ele o valor da coluna
        await ContatosDbTests.ConcluirGanhaAsync(db, amb.Vendas, id);

        Assert.Empty(await CardsAsync());

        var semPedido = await amb.Funil.QuadroAsync(funil, 50, default);
        Assert.Equal(0m, semPedido.Colunas.Single(c => c.EGanho).ValorTotal);

        // ⚠️ Mas o faturamento NÃO caiu junto — sair do quadro não é sumir do dinheiro. É esta
        // linha que torna a regra aceitável: concluir para liberar o funil não custa nada.
        Assert.Equal(800m, await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == id)
            .Where(n => n.Status == StatusNegociacao.Ganha || n.Status == StatusNegociacao.Concluida)
            .SumAsync(n => n.Valor ?? 0m));

        // ---------- 3. voltou a negociar: UM card, na primeira etapa, com id NOVO
        await amb.Contatos.AbrirNegociacaoAsync(id, null, default);
        db.ChangeTracker.Clear();

        var depois = await amb.Funil.QuadroAsync(funil, 50, default);
        var nova = Assert.Single(depois.Colunas.SelectMany(c => c.Contatos), c => c.ContatoId == id);

        // A rodada nova é uma LINHA NOVA — id diferente do da anterior — e começa do começo.
        Assert.NotEqual(aberta.Id, nova.Id);
        Assert.Equal(amb.Cenario.PrimeiraEtapa.Id,
            depois.Colunas.Single(c => c.Contatos.Any(x => x.ContatoId == id)).EtapaId);
    }

    /// <summary>⚠️ ESTE TESTE SE CHAMAVA `ARRASTAR_O_CARD_JA_GANHO_E_RECUSADO`, E A REGRA MUDOU.
    ///
    /// O nome e a frase que ele afirmava ("já foi fechado") diziam *o card ganho não se move*, e isso
    /// deixou de ser verdade no POS-1: ele avança para as etapas de pós-venda. O que continua
    /// verdade, e é o que este teste guarda agora, é que ele não VOLTA.
    ///
    /// O motivo original segue valendo: arrastar o card da coluna de ganho para uma coluna de
    /// negociação deixava `ganho_em` carimbado com o card fora da etapa de ganho — a "porta única do
    /// ganho" furada pela porta de trás. A posição do negócio vendido é o registro de onde ele
    /// fechou, e para trás ela não anda.
    ///
    /// ⚠️ A FRASE AFIRMADA É "não volta para a negociação", E NÃO "só avança". As duas regras se
    /// aplicam aqui (é antes do ganho E é para trás), e a ordem delas na tabela é o que escolhe qual
    /// frase sai. Esta diz o MOTIVO; a outra, o mecanismo.</summary>
    [Fact]
    public async Task ARRASTAR_O_CARD_GANHO_PARA_TRAS_E_RECUSADO()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "arrastar-ganho");
        using var _ = db; using var __ = tx;

        var id = amb.Cenario.Contato.Id;
        await amb.Contatos.MarcarGanhoAsync(id, 400m, null, null, default);
        db.ChangeTracker.Clear();

        var ganha = await db.Negociacoes.AsNoTracking()
            .Where(n => n.Status == StatusNegociacao.Ganha).Select(n => n.Id).SingleAsync();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(ganha, new MoverContato(amb.Cenario.Etapas[1].Id, null), default));

        Assert.True(erro.Conflito);
        Assert.Contains("não volta para a negociação", erro.Message);

        // E ela continua onde fechou — a afirmação original, intocada.
        db.ChangeTracker.Clear();
        var depois = await db.Negociacoes.AsNoTracking().SingleAsync(n => n.Id == ganha);
        Assert.Equal(amb.Cenario.Etapas.Single(e => e.EGanho).Id, depois.EtapaId);
    }

    // ==================================================================== POS-1 · depois da venda

    /// <summary>===================== O PEDIDO DO DONO, DE PONTA A PONTA =====================
    /// "Vendas é o ganho, mas preciso incluir etapas depois dela — pós-venda, entregue."
    ///
    /// ⚠️ A SEGUNDA METADE DO TESTE É O QUE IMPORTA. Que o `EtapaId` mudou prova que o guarda saiu
    /// do caminho; que o card APARECE no quadro, naquela coluna, com o valor dele, é o que prova que
    /// o recorte das colunas foi feito. Sem essa metade, o teste passaria com o arrasto devolvendo
    /// 200 e o card desaparecendo da tela — que foi exatamente o modo de falha deste bloco.
    /// ==========================================================================</summary>
    [Fact]
    public async Task O_CARD_GANHO_AVANCA_PARA_A_ETAPA_DE_POS_VENDA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pos1-avanca");
        using var _ = db; using var __ = tx;

        var posVenda = await EtapaDepoisDoGanhoAsync(db, amb.Cenario, "Pós-Venda", 4);

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 400m, null, null, default);
        db.ChangeTracker.Clear();

        var ganha = await db.Negociacoes.AsNoTracking()
            .Where(n => n.Status == StatusNegociacao.Ganha).Select(n => n.Id).SingleAsync();

        await amb.Funil.MoverAsync(ganha, new MoverContato(posVenda.Id, null), default);
        db.ChangeTracker.Clear();

        Assert.Equal(posVenda.Id,
            (await db.Negociacoes.AsNoTracking().SingleAsync(n => n.Id == ganha)).EtapaId);

        var coluna = (await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default))
            .Colunas.Single(c => c.EtapaId == posVenda.Id);

        Assert.True(coluna.PosGanho);
        Assert.Equal(1, coluna.Total);
        Assert.Equal(400m, coluna.ValorTotal);
        // E o card sabe que está vendido — é disso que a tela tira "Concluir" em vez de
        // "Registrar venda".
        Assert.True(Assert.Single(coluna.Contatos).Ganha);

        // A coluna de ganho esvaziou: o pedido saiu de lá, não foi duplicado.
        Assert.Equal(0, (await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default))
            .Colunas.Single(c => c.EGanho).Total);
    }

    [Fact]
    public async Task O_CARD_GANHO_NAO_VOLTA_DA_POS_VENDA_PARA_A_ETAPA_DE_VENDA()
    {
        // A etapa de ganho recusa arrasto de QUALQUER status — inclusive de quem já está vendido e
        // só quer voltar uma casa. A porta única do ganho não tem exceção de retorno.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pos1-nao-volta-venda");
        using var _ = db; using var __ = tx;

        var posVenda = await EtapaDepoisDoGanhoAsync(db, amb.Cenario, "Pós-Venda", 4);
        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 400m, null, null, default);
        db.ChangeTracker.Clear();

        var ganha = await db.Negociacoes.AsNoTracking()
            .Where(n => n.Status == StatusNegociacao.Ganha).Select(n => n.Id).SingleAsync();
        await amb.Funil.MoverAsync(ganha, new MoverContato(posVenda.Id, null), default);
        db.ChangeTracker.Clear();

        var etapaGanho = amb.Cenario.Etapas.Single(e => e.EGanho).Id;
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(ganha, new MoverContato(etapaGanho, null), default));

        Assert.Contains("valor fechado", erro.Message);
    }

    [Fact]
    public async Task O_CARD_GANHO_SE_REORDENA_DENTRO_DA_POS_VENDA()
    {
        // ⚠️ `od == oa` TEM de passar. Com vinte entregas pendentes o vendedor vai querer ordenar a
        // fila, e `MoverAsync` é o único caminho que calcula `OrdemKanban`. "Só avança" é sobre
        // ETAPA; trocar `>=` por `>` na regra 9 tornaria a coluna de pós-venda imóvel por dentro.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pos1-reordena");
        using var _ = db; using var __ = tx;

        var posVenda = await EtapaDepoisDoGanhoAsync(db, amb.Cenario, "Pós-Venda", 4);

        var primeiro = amb.Cenario.Contato.Id;
        var segundo = await CriarComNegocioAsync(db, amb.Cenario, "Segundo pedido");

        await amb.Contatos.MarcarGanhoAsync(primeiro, 100m, null, null, default);
        await amb.Contatos.MarcarGanhoAsync(segundo, 200m, null, null, default);
        db.ChangeTracker.Clear();

        var idPrimeiro = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == primeiro && n.Status == StatusNegociacao.Ganha)
            .Select(n => n.Id).SingleAsync();
        var idSegundo = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == segundo && n.Status == StatusNegociacao.Ganha)
            .Select(n => n.Id).SingleAsync();

        await amb.Funil.MoverAsync(idPrimeiro, new MoverContato(posVenda.Id, null), default);
        await amb.Funil.MoverAsync(idSegundo, new MoverContato(posVenda.Id, null), default);
        db.ChangeTracker.Clear();

        // Reordena DENTRO da mesma coluna: põe o primeiro depois do segundo.
        await amb.Funil.MoverAsync(idPrimeiro, new MoverContato(posVenda.Id, idSegundo), default);
        db.ChangeTracker.Clear();

        var ordens = await db.Negociacoes.AsNoTracking()
            .Where(n => n.EtapaId == posVenda.Id)
            .OrderBy(n => n.OrdemKanban).Select(n => n.Id).ToListAsync();

        Assert.Equal([idSegundo, idPrimeiro], ordens);
    }

    [Fact]
    public async Task O_CARD_ABERTO_NAO_ENTRA_NA_POS_VENDA()
    {
        // ===================== O TERCEIRO DEFEITO DO BLOCO =====================
        // Hoje isto PASSA, e a coluna de pós-venda aceita exatamente a coisa errada: o card vendido
        // era recusado e o NÃO vendido entrava — porque a etapa não é de ganho e o único guarda que
        // existia olhava só para isso. Dava para pôr em "Entregue" um negócio que nunca foi vendido.
        // =======================================================================
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pos1-aberto-nao-entra");
        using var _ = db; using var __ = tx;

        var posVenda = await EtapaDepoisDoGanhoAsync(db, amb.Cenario, "Pós-Venda", 4);

        var aberta = await db.Negociacoes.AsNoTracking()
            .Where(n => n.Status == StatusNegociacao.Aberta).Select(n => n.Id).FirstAsync();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(aberta, new MoverContato(posVenda.Id, null), default));

        Assert.Contains("pós-venda", erro.Message);
        Assert.Contains("Registre a venda", erro.Message);
    }

    [Fact]
    public async Task O_CARD_GANHO_EM_ETAPA_ANTERIOR_NAO_ANDA_DENTRO_DA_NEGOCIACAO()
    {
        // ===================== O ESTADO QUE EXISTE E NINGUÉM DESENHOU =====================
        // Negócio `ganha` numa etapa ANTES da de ganho. Acontece em funil sem etapa de ganho (o
        // schema permite, e `MarcarGanhoAsync` ali deixa o card onde está) e em linhas de antes do
        // guarda do E4c/2.
        //
        // ⚠️ É ESTE TESTE QUE TORNA AS REGRAS 7 E 8 NÃO-REDUNDANTES. Ir de "Novo Lead" para
        // "Proposta" é PARA FRENTE — a regra 8 deixa passar. O que recusa é a 7: vendido não circula
        // dentro da negociação, nem para frente.
        // ===============================================================================
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pos1-ganho-atras");
        using var _ = db; using var __ = tx;

        var aberta = await db.Negociacoes
            .Where(n => n.Status == StatusNegociacao.Aberta).FirstAsync();
        aberta.Status = StatusNegociacao.Ganha;
        aberta.Valor = 300m;
        aberta.GanhaEm = ContatosDbTests.Agora.UtcDateTime;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Etapas[0] é "Novo Lead" (ordem 1) e Etapas[1] é "Proposta" (ordem 2): o destino está
        // ADIANTE de onde o card está, e mesmo assim é recusado.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(aberta.Id, new MoverContato(amb.Cenario.Etapas[1].Id, null), default));

        Assert.Contains("não volta para a negociação", erro.Message);
    }

    [Fact]
    public async Task SEM_ETAPA_DE_GANHO_O_CARD_GANHO_APARECE_NO_QUADRO()
    {
        // ⚠️ UM CARD INVISÍVEL QUE EXISTE HOJE EM PRODUÇÃO. Em funil sem etapa de ganho,
        // `MarcarGanhoAsync` deixa o card na etapa pré-venda e só troca o status — e o recorte velho
        // (`!e_ganho && aberta`) não o mostrava. Negócio vendido, fora da tela, com a vaga do funil
        // ocupada e nenhum jeito de concluir pelo quadro.
        //
        // O recorte novo o traz de volta, de graça. Isto não estava no pedido; apareceu lendo o
        // caminho do `FirstOrDefaultAsync` que devolve `null`.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "pos1-sem-ganho-visivel");
        using var _ = db; using var __ = tx;

        await db.EtapasFunil.Where(e => e.EGanho)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.EGanho, false), default);
        db.ChangeTracker.Clear();

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 500m, null, null, default);
        db.ChangeTracker.Clear();

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var card = Assert.Single(
            quadro.Colunas.SelectMany(c => c.Contatos),
            c => c.ContatoId == amb.Cenario.Contato.Id);

        Assert.True(card.Ganha);
        // E nenhuma coluna é de pós-venda: sem etapa de ganho não há fronteira para estar depois de.
        Assert.DoesNotContain(quadro.Colunas, c => c.PosGanho);
    }

    // ==================================================================== a versao do card

    /// <summary>===================== O ARRASTO NORMAL, COM A VERSAO DO QUADRO =====================
    /// O caminho que TODO vendedor usa, e que nao tinha teste nenhum: carrega o quadro, pega o card
    /// como o cliente o recebe, e arrasta mandando a `versao` que veio junto.
    ///
    /// Se este teste falhar com "outra pessoa moveu este negocio", ninguem moveu nada — e o defeito
    /// esta no `xmin` que o quadro entrega ou no que o `MoverAsync` compara.
    /// ==================================================================================</summary>
    [Fact]
    public async Task ARRASTAR_COM_A_VERSAO_QUE_O_QUADRO_ENTREGOU_FUNCIONA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "versao-quadro");
        using var _ = db; using var __ = tx;

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var card = quadro.Colunas.SelectMany(c => c.Contatos).First();

        db.ChangeTracker.Clear();

        await amb.Funil.MoverAsync(
            card.Id, new MoverContato(amb.Cenario.Etapas[1].Id, null, card.Versao), default);

        db.ChangeTracker.Clear();
        Assert.Equal(amb.Cenario.Etapas[1].Id,
            (await db.Negociacoes.AsNoTracking().SingleAsync(n => n.Id == card.Id)).EtapaId);
    }

    /// <summary>O SEGUNDO arrasto seguido, que e onde a versao envelhece.
    ///
    /// ⚠️ Depois de um movimento o `xmin` da linha MUDA. Um cliente que nao recarregue a coluna fica
    /// com a versao velha na mao, e o proximo arrasto do mesmo card e recusado como se outra pessoa
    /// tivesse mexido — com o vendedor sozinho na tela.</summary>
    [Fact]
    public async Task A_VERSAO_ENVELHECE_DEPOIS_DE_UM_ARRASTO()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "versao-envelhece");
        using var _ = db; using var __ = tx;

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var card = quadro.Colunas.SelectMany(c => c.Contatos).First();
        var versaoAntiga = card.Versao;

        db.ChangeTracker.Clear();
        await amb.Funil.MoverAsync(
            card.Id, new MoverContato(amb.Cenario.Etapas[1].Id, null, versaoAntiga), default);
        db.ChangeTracker.Clear();

        // O quadro recarregado traz a versao NOVA...
        var depois = (await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default))
            .Colunas.SelectMany(c => c.Contatos).Single(x => x.Id == card.Id);
        Assert.NotEqual(versaoAntiga, depois.Versao);

        // ...e a velha e recusada, que e o comportamento CERTO.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(
                card.Id, new MoverContato(amb.Cenario.PrimeiraEtapa.Id, null, versaoAntiga), default));

        Assert.Contains("Outra pessoa moveu", erro.Message);
    }

    /// <summary>===================== A RENORMALIZACAO NAO E "OUTRA PESSOA" =====================
    ///
    /// Quando o intervalo entre dois cards acaba, `MoverAsync` RENORMALIZA a coluna inteira — e
    /// renormalizar e um UPDATE em toda linha dela, inclusive na do card que esta sendo arrastado.
    ///
    /// ⚠️ `Versao` e o `xmin`, mapeado com `ValueGeneratedOnAddOrUpdate`: depois do `SaveChanges`
    /// da renormalizacao, o EF RELE o valor, e a entidade em memoria passa a ter a versao NOVA. A
    /// comparacao explicita, que vinha depois, entao comparava a versao do cliente contra uma que a
    /// PROPRIA REQUISICAO acabou de mudar — e acusava "outra pessoa moveu este negocio" com o
    /// vendedor sozinho na tela.
    ///
    /// Achado em producao local: uma coluna com mil cards semeados tinha `ordem_kanban` repetida,
    /// entao a renormalizacao disparava no PRIMEIRO arrasto.
    /// ==============================================================================</summary>
    [Fact]
    public async Task ARRASTAR_NUMA_COLUNA_QUE_PRECISA_RENORMALIZAR_NAO_ACUSA_CONFLITO()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "renormaliza-versao");
        using var _ = db; using var __ = tx;

        var etapa = amb.Cenario.PrimeiraEtapa;

        // Dois vizinhos com a MESMA ordem: e o estado em que o "meio" entre eles nao existe.
        var b = await CriarComNegocioAsync(db, amb.Cenario, "Vizinho B");
        var c = await CriarComNegocioAsync(db, amb.Cenario, "Vizinho C");

        await db.Negociacoes
            .Where(n => n.ContatoId == b || n.ContatoId == c)
            .ExecuteUpdateAsync(x => x.SetProperty(n => n.OrdemKanban, 1000m), default);
        db.ChangeTracker.Clear();

        var idB = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == b).Select(n => n.Id).SingleAsync();

        // O card do cenario, com a versao que o QUADRO entrega — como o cliente a recebe.
        var card = (await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default))
            .Colunas.Single(x => x.EtapaId == etapa.Id)
            .Contatos.Single(x => x.ContatoId == amb.Cenario.Contato.Id);

        db.ChangeTracker.Clear();

        // Soltar DEPOIS do B: o vizinho de baixo e o C, com a mesma ordem. Intervalo zero.
        await amb.Funil.MoverAsync(
            card.Id, new MoverContato(etapa.Id, idB, card.Versao), default);

        db.ChangeTracker.Clear();

        // E a coluna saiu renumerada, que e o ponto da renormalizacao.
        var ordens = await db.Negociacoes.AsNoTracking()
            .Where(n => n.EtapaId == etapa.Id)
            .OrderBy(n => n.OrdemKanban).Select(n => n.OrdemKanban).ToListAsync();

        Assert.Equal(ordens.Count, ordens.Distinct().Count());
    }

    // ---------------------------------------------------------------- auxiliares do POS-1

    private static async Task<EtapaFunil> EtapaDepoisDoGanhoAsync(
        NexoraDbContext db, Cenario c, string nome, short ordem)
    {
        var etapa = new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = c.Pipeline.Id, Nome = nome, Ordem = ordem
        };
        db.EtapasFunil.Add(etapa);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return etapa;
    }

    /// <summary>Contato com negociação aberta, que é o que `MarcarGanhoAsync` exige.</summary>
    private static async Task<long> CriarComNegocioAsync(NexoraDbContext db, Cenario c, string nome)
    {
        var contato = new Contato
        {
            EmpresaId = c.Id, Nome = nome,
            Telefone = $"5584 9{Random.Shared.Next(1000, 9999)}{Random.Shared.Next(1000, 9999)}"
        };
        db.Contatos.Add(contato);
        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = c.Id, Contato = contato,
            PipelineId = c.Pipeline.Id, EtapaId = c.PrimeiraEtapa.Id,
            Status = StatusNegociacao.Aberta
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return contato.Id;
    }

    /// <summary>O valor do card sai da NEGOCIAÇÃO, e é o que o E4e vai precisar quando
    /// `contatos.valor` deixar de existir.</summary>
    [Fact]
    public async Task O_CARD_MOSTRA_O_VALOR_DA_NEGOCIACAO()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "valor-da-negociacao");
        using var _ = db; using var __ = tx;

        var id = amb.Cenario.Contato.Id;

        // ⚠️ O CONTRASTE SUMIU COM A COLUNA (E4e/4). Este teste nasceu comparando os dois
        // valores — 10 em `contatos`, 777 em `negociacoes` — para provar que o quadro lia o
        // segundo. Nao ha mais primeiro: `contatos.valor` nao existe, e o que sobra e afirmar que
        // o quadro mostra o valor do NEGOCIO.
        await db.Negociacoes.Where(n => n.ContatoId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.Valor, 777m));
        db.ChangeTracker.Clear();

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var card = quadro.Colunas.SelectMany(c => c.Contatos).Single(c => c.ContatoId == id);

        Assert.Equal(777m, card.Valor);

        // E o cabeçalho soma o mesmo número que os cards mostram.
        var coluna = quadro.Colunas.Single(c => c.Contatos.Any(x => x.ContatoId == id));
        Assert.Equal(777m, coluna.ValorTotal);
    }

    /// <summary>A coluna de ganho mostra o que fechou e ainda não concluiu (NEG-2). Concluir tira
    /// o card e mantém o número em `concluidas` — a mesma regra de antes, agora sobre o status da
    /// negociação em vez do status da venda.</summary>
    [Fact]
    public async Task CONCLUIR_TIRA_O_CARD_DA_COLUNA_DE_GANHO_E_CONTA_EM_CONCLUIDAS()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "ganho-concluido");
        using var _ = db; using var __ = tx;

        var id = amb.Cenario.Contato.Id;
        await amb.Contatos.MarcarGanhoAsync(id, 640m, null, null, default);
        db.ChangeTracker.Clear();

        var antes = (await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default))
            .Colunas.Single(c => c.EGanho);
        Assert.Equal(1, antes.Total);
        Assert.Equal(640m, antes.ValorTotal);
        Assert.Equal(0, antes.Concluidas);

        var venda = await db.Negociacoes.AsNoTracking().SingleAsync();
        await amb.Vendas.ConcluirAsync([venda.Id], default);
        db.ChangeTracker.Clear();

        var depois = (await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default))
            .Colunas.Single(c => c.EGanho);
        Assert.Equal(0, depois.Total);
        Assert.Empty(depois.Contatos);
        Assert.Equal(1, depois.Concluidas);
    }

    // ==================================================================== apoio
    /// <summary>Cria um contato direto no banco, com a ordem que o teste precisa.
    ///
    /// Não passa pelo ServicoContatos de propósito: o serviço sempre põe no FIM da coluna, e
    /// estes testes precisam montar arranjos específicos de ordem para exercitar as bordas.
    ///
    /// ⚠️ E POR ISSO ELE TAMBÉM CRIA A NEGOCIAÇÃO. Desde o E4c o quadro lê `negociacoes`; um
    /// contato sem ela simplesmente não tem card, e o teste falharia dizendo "esperado 6, veio 1"
    /// sem nenhuma pista de que o problema é a fixture.
    ///
    /// ⚠️ DEVOLVE O ID DA NEGOCIAÇÃO, e não o do contato — desde o E4c/2 é ele o card, e é ele
    /// que o arrasto usa. Quem precisar da pessoa resolve pelo `ContatoId` da negociação.</summary>
    private static async Task<long> CardAsync(
        NexoraDbContext db, ContatosDbTests.Ambiente amb, string nome, long etapaId,
        decimal ordem, decimal? valor = null)
    {
        var contato = new Contato
        {
            EmpresaId = amb.Cenario.Id,
            Nome = nome,
            // ⚠️ `Semeador.Semente` e NAO `GetHashCode()`: o hash de string do .NET e semeado
            // por PROCESSO, entao ele gera telefone diferente a cada rodada. Este projeto ja
            // levou um CI vermelho por isso — ver o comentario em `Semeador.Semente`.
            Telefone = $"5584{Semeador.Semente(amb.Cenario.Empresa.Nome + nome) % 1000000000:D9}"
        };
        db.Contatos.Add(contato);

        var negociacao = new Negociacao
        {
            EmpresaId = amb.Cenario.Id,
            Contato = contato,
            PipelineId = amb.Cenario.Pipeline.Id,
            EtapaId = etapaId,
            OrdemKanban = ordem,
            Valor = valor,
            Status = StatusNegociacao.Aberta
        };
        db.Negociacoes.Add(negociacao);

        await db.SaveChangesAsync();
        var id = negociacao.Id;
        db.ChangeTracker.Clear();
        return id;
    }

    /// <summary>A PESSOA por trás de um card. Desde o E4c/2 `CardAsync` devolve o id da
    /// negociação, e os serviços de contato continuam recebendo o id do contato — os dois são
    /// `long`, então trocar um pelo outro compila e só reprova em execução.</summary>
    private static Task<long> ContatoDoCardAsync(NexoraDbContext db, long cardId) =>
        db.Negociacoes.AsNoTracking().IgnoreQueryFilters()
            .Where(n => n.Id == cardId).Select(n => n.ContatoId).SingleAsync();

    /// <summary>Os ids da coluna, na ordem em que o quadro os mostraria.</summary>
    private static async Task<long[]> OrdemDaColunaAsync(
        NexoraDbContext db, long etapaId, long? ignorar = null)
    {
        db.ChangeTracker.Clear();
        return await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.EtapaId == etapaId
                     && n.Status == StatusNegociacao.Aberta
                     && n.Contato.AnonimizadoEm == null
                     && (ignorar == null || n.Id != ignorar))
            .OrderBy(n => n.OrdemKanban).ThenBy(n => n.Id)
            .Select(c => c.Id)
            .ToArrayAsync();
    }
    // ==================================================================== etiquetas do card
    /// <summary>⚠️ O RELATO, NO LUGAR ONDE ELE FOI VISTO: o CARD.
    ///
    /// "incluí o contato Ysia em Vendas e Pós-venda e ela ficou com a mesma etiqueta em pipeline
    /// diferente."
    ///
    /// O quadro projetava `n.Contato.Etiquetas`, entao os dois cards da mesma pessoa saiam
    /// identicos. Os testes de `ServicoEtiquetas` provam que as duas tabelas guardam coisas
    /// diferentes — mas NAO provam qual delas o quadro le, e foi exatamente ai que o defeito
    /// morava. Verificado devolvendo `n.Contato.Etiquetas`: os outros testes seguem verdes e so
    /// este reprova.</summary>
    [Fact]
    public async Task O_CARD_MOSTRA_A_ETIQUETA_DO_NEGOCIO_E_NAO_A_DA_PESSOA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "etiqueta-do-card");
        using var _ = db; using var __ = tx;

        var etiquetas = new ServicoEtiquetas(db, amb.Contexto);
        var doNegocio = await etiquetas.CriarAsync(new NovaEtiqueta("Urgente", null), default);
        var daPessoa = await etiquetas.CriarAsync(new NovaEtiqueta("VIP", null), default);

        await etiquetas.AplicarNaNegociacaoAsync(amb.Cenario.Negociacao.Id, [doNegocio], default);
        await etiquetas.AplicarAsync(amb.Cenario.Contato.Id, [daPessoa], default);
        db.ChangeTracker.Clear();

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var card = quadro.Colunas.SelectMany(c => c.Contatos)
            .Single(c => c.ContatoId == amb.Cenario.Contato.Id);

        Assert.Equal(["Urgente"], card.Etiquetas.Select(e => e.Nome));
    }
}
