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

        var nova = await amb.Funil.MoverAsync(c, new MoverContato(etapa, AposContatoId: a), default);

        Assert.Equal(15m, nova);
        Assert.Equal([a, c, b], await OrdemDaColunaAsync(db, etapa, ignorar: amb.Cenario.Contato.Id));
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
        var nova = await amb.Funil.MoverAsync(c, new MoverContato(etapa, null), default);

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

        var nova = await amb.Funil.MoverAsync(c, new MoverContato(etapa, AposContatoId: b), default);

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

        var nova = await amb.Funil.MoverAsync(
            amb.Cenario.Contato.Id, new MoverContato(vazia, null), default);

        Assert.Equal(0m, nova);
        Assert.Equal([amb.Cenario.Contato.Id], await OrdemDaColunaAsync(db, vazia));
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
        var nova = await amb.Funil.MoverAsync(c, new MoverContato(etapa, AposContatoId: a), default);

        Assert.Equal(15m, nova);
        Assert.Equal([a, c, b], await OrdemDaColunaAsync(db, etapa, ignorar: amb.Cenario.Contato.Id));
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

        var nova = await amb.Funil.MoverAsync(c, new MoverContato(etapa, AposContatoId: a), default);

        db.ChangeTracker.Clear();
        var ordens = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
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
        await amb.Contatos.MarcarPerdidoAsync(morto, "sumiu", default);
        db.ChangeTracker.Clear();

        var c = await CardAsync(db, amb, "C", amb.Cenario.PrimeiraEtapa.Id, 1m);
        await amb.Funil.MoverAsync(c, new MoverContato(etapa, AposContatoId: a), default);

        db.ChangeTracker.Clear();
        var perdido = await db.Contatos.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == morto);
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
                amb.Cenario.Contato.Id, new MoverContato(etapaGanho, null), default));

        Assert.True(erro.Conflito);
        Assert.Contains("registre a venda", erro.Message, StringComparison.OrdinalIgnoreCase);

        // E o contato NÃO se moveu.
        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == amb.Cenario.Contato.Id);
        Assert.NotEqual(etapaGanho, c.EtapaId);
        Assert.Null(c.GanhoEm);
    }

    [Fact]
    public async Task Contato_perdido_nao_pode_ser_movido_sem_reabrir()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "mover-perdido");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "desistiu", default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(
                amb.Cenario.Contato.Id, new MoverContato(amb.Cenario.Etapas[1].Id, null), default));

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
                amb.Cenario.Contato.Id, new MoverContato(alheia.PrimeiraEtapa.Id, null), default));

        Assert.Contains("não encontrada", erro.Message);

        // O contato continua onde estava — não saiu do funil da própria empresa.
        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == amb.Cenario.Contato.Id);
        Assert.Equal(amb.Cenario.PrimeiraEtapa.Id, c.EtapaId);
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
                amb.Cenario.Contato.Id,
                new MoverContato(amb.Cenario.PrimeiraEtapa.Id, AposContatoId: outraColuna),
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

        await amb.Contatos.MarcarPerdidoAsync(perdido, "sumiu", default);
        db.ChangeTracker.Clear();
        await amb.Contatos.AnonimizarAsync(anonimo, default);
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
        // Hoje os dois usam `RegrasContato.NoQuadro`. Este teste é a garantia de que continuam
        // usando: ele compara as duas leituras REAIS, não o predicado.
        //
        // ⚠️ ENTRE O E4c E O E4d ESTA GARANTIA É MAIS FRACA DO QUE ERA, e vale registrar: o
        // quadro já lê `negociacoes` e o dashboard ainda lê `contatos`. Não são mais duas cópias
        // do mesmo predicado sobre a mesma tabela — são duas tabelas, e o que as mantém de
        // acordo é `EspelhoNegociacao`. O E4d devolve a fonte única, agora do lado da negociação.
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
        await amb.Contatos.MarcarPerdidoAsync(perdido, "Comprou com concorrente", default);

        // Anonimizado: sai das duas. Era o lado que o dashboard esquecia.
        var anonimo = await CardAsync(db, amb, "anonimizado", etapa, 4000m, 700m);
        await amb.Contatos.AnonimizarAsync(anonimo, default);

        db.ChangeTracker.Clear();

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var dashboard = await amb.Dashboard.DashboardAsync(default);

        // Etapa por etapa: um total agregado igual poderia esconder duas diferenças que se
        // cancelam entre colunas.
        foreach (var coluna in quadro.Colunas)
        {
            var noDashboard = dashboard.Funil.Single(f => f.EtapaId == coluna.EtapaId);

            Assert.True(coluna.Total == noDashboard.Contatos,
                $"'{coluna.Nome}': quadro {coluna.Total}, dashboard {noDashboard.Contatos}.");
            Assert.True(coluna.ValorTotal == noDashboard.Valor,
                $"'{coluna.Nome}': quadro {coluna.ValorTotal:C}, dashboard {noDashboard.Valor:C}.");
        }

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
    /// <summary>⚠️ O TESTE QUE TORNA ESTE BLOCO SEGURO.
    ///
    /// Depois do E4b, reabrir um contato que já ganhou deixa DUAS negociações vivas: a ganha, que
    /// continua esperando conclusão, e a nova aberta. Mostrar as duas é o ganho do E4 — mas o
    /// card ainda é identificado por `contatoId`, e o arrasto ainda manda `contatoId`. Com as
    /// duas na tela, arrastar o card da coluna de ganho moveria a negociação ABERTA: o card
    /// errado se mexeria, sem erro nenhum.
    ///
    /// `RegrasNegociacao.CardVigente` segura isso até o contrato virar. Verificado tirando-a: o
    /// quadro passa a devolver 2 cards para o mesmo contato.</summary>
    [Fact]
    public async Task REABRIR_UM_GANHO_NAO_DUPLICA_O_CARD_NO_QUADRO()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "vigente");
        using var _ = db; using var __ = tx;

        var id = amb.Cenario.Contato.Id;

        await amb.Contatos.MarcarGanhoAsync(id, 800m, null, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.ReabrirAsync(id, default);
        db.ChangeTracker.Clear();

        // As duas existem no banco — o teste não passa por não haver o que duplicar.
        Assert.Equal(2, await db.Negociacoes.CountAsync(n => n.ContatoId == id));

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var cards = quadro.Colunas.SelectMany(c => c.Contatos).Where(c => c.Id == id).ToList();

        Assert.Single(cards);

        // E ele está onde o contato está: reabrir devolve o card ao início do funil.
        var contato = await db.Contatos.AsNoTracking().SingleAsync(c => c.Id == id);
        var coluna = quadro.Colunas.Single(c => c.Contatos.Any(x => x.Id == id));
        Assert.Equal(contato.EtapaId, coluna.EtapaId);

        // A coluna de ganho não conta a negociação ganha que ficou para trás — senão o total do
        // cabeçalho diria 1 com zero cards embaixo.
        var ganho = quadro.Colunas.Single(c => c.EGanho);
        Assert.Equal(0, ganho.Total);
    }

    /// <summary>⚠️ O CASO QUE O BANCO DE DESENVOLVIMENTO PEGOU E O TESTE NÃO PEGAVA.
    ///
    /// `CardVigente` era "a de maior id". Comparando as duas leituras contra os 1015 contatos
    /// reais apareceu um contato com `aberta` id 245 e `ganha` id 1074 — e o card pulava da
    /// coluna 30 para a 32.
    ///
    /// O motivo é que a migração do E4b apagou e reinseriu as negociações vindas de venda, para
    /// gravar o elo `venda_id`: os ids delas ficaram maiores que os das abertas, e id deixou de
    /// ser relógio. Nenhum teste criava essa ordem invertida, porque pelos serviços a aberta é
    /// sempre a mais nova.
    ///
    /// Aqui a ordem é invertida À MÃO, que é o que a migração fez.</summary>
    [Fact]
    public async Task A_GANHA_MAIS_NOVA_NAO_ROUBA_O_CARD_DA_ABERTA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "id-nao-e-relogio");
        using var _ = db; using var __ = tx;

        var contato = amb.Cenario.Contato;
        var ganho = amb.Cenario.Etapas.Single(e => e.EGanho);

        // A ganha entra DEPOIS da aberta, então com id maior — exatamente como ficou no banco
        // depois da migração do E4b.
        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = contato.Id,
            PipelineId = amb.Cenario.Pipeline.Id,
            EtapaId = ganho.Id,
            Valor = 300m,
            Status = StatusNegociacao.Ganha,
            GanhaEm = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var abertaId = await db.Negociacoes.Where(n => n.Status == StatusNegociacao.Aberta)
            .Select(n => n.Id).SingleAsync();
        var ganhaId = await db.Negociacoes.Where(n => n.Status == StatusNegociacao.Ganha)
            .Select(n => n.Id).SingleAsync();
        Assert.True(ganhaId > abertaId, "a fixture precisa da ordem invertida para valer de algo");

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);

        // O card continua onde o contato está: a aberta é a viva, a ganha é resto de rodada.
        var coluna = quadro.Colunas.Single(c => c.Contatos.Any(x => x.Id == contato.Id));
        Assert.Equal(contato.EtapaId, coluna.EtapaId);
        Assert.Equal(0, quadro.Colunas.Single(c => c.EGanho).Total);
    }

    /// <summary>O valor do card sai da NEGOCIAÇÃO, e é o que o E4e vai precisar quando
    /// `contatos.valor` deixar de existir.</summary>
    [Fact]
    public async Task O_CARD_MOSTRA_O_VALOR_DA_NEGOCIACAO()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "valor-da-negociacao");
        using var _ = db; using var __ = tx;

        var id = amb.Cenario.Contato.Id;

        // Os dois divergem de propósito: se o quadro ainda lesse `contatos`, viria 10.
        await db.Contatos.Where(c => c.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Valor, 10m));
        await db.Negociacoes.Where(n => n.ContatoId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.Valor, 777m));
        db.ChangeTracker.Clear();

        var quadro = await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default);
        var card = quadro.Colunas.SelectMany(c => c.Contatos).Single(c => c.Id == id);

        Assert.Equal(777m, card.Valor);

        // E o cabeçalho soma o mesmo número que os cards mostram.
        var coluna = quadro.Colunas.Single(c => c.Contatos.Any(x => x.Id == id));
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
        await amb.Contatos.MarcarGanhoAsync(id, 640m, null, default);
        db.ChangeTracker.Clear();

        var antes = (await amb.Funil.QuadroAsync(amb.Cenario.Pipeline.Id, 50, default))
            .Colunas.Single(c => c.EGanho);
        Assert.Equal(1, antes.Total);
        Assert.Equal(640m, antes.ValorTotal);
        Assert.Equal(0, antes.Concluidas);

        var venda = await db.Vendas.AsNoTracking().SingleAsync();
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
    /// sem nenhuma pista de que o problema é a fixture.</summary>
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
            Telefone = $"5584{Semeador.Semente(amb.Cenario.Empresa.Nome + nome) % 1000000000:D9}",
            EtapaId = etapaId,
            OrdemKanban = ordem,
            Valor = valor
        };
        db.Contatos.Add(contato);

        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = amb.Cenario.Id,
            Contato = contato,
            PipelineId = amb.Cenario.Pipeline.Id,
            EtapaId = etapaId,
            OrdemKanban = ordem,
            Valor = valor,
            Status = StatusNegociacao.Aberta
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return contato.Id;
    }

    /// <summary>Os ids da coluna, na ordem em que o quadro os mostraria.</summary>
    private static async Task<long[]> OrdemDaColunaAsync(
        NexoraDbContext db, long etapaId, long? ignorar = null)
    {
        db.ChangeTracker.Clear();
        return await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EtapaId == etapaId && c.PerdidoEm == null && c.AnonimizadoEm == null
                     && (ignorar == null || c.Id != ignorar))
            .OrderBy(c => c.OrdemKanban).ThenBy(c => c.Id)
            .Select(c => c.Id)
            .ToArrayAsync();
    }
}
