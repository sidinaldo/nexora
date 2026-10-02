using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>Configuração do funil.
///
/// Estes testes existem por causa de três coisas que o banco impõe e uma que ele não impõe:
/// `uq_etapas_ordem` (índice único NÃO adiável), `uq_etapas_ganho` (parcial), o
/// `ON DELETE RESTRICT` da FK dos contatos — e a que só a aplicação garante: precisa sobrar
/// ao menos uma etapa que não seja a de ganho.</summary>
[Collection("banco")]
public class EtapasDbTests(BancoTeste banco)
{
    // ==================================================================== ordem
    [Fact]
    public async Task REORDENAR_TROCA_POSICOES_SEM_VIOLAR_O_INDICE_UNICO()
    {
        // ===================== O CASO QUE QUEBRA A IMPLEMENTAÇÃO INGÊNUA =====================
        // `uq_etapas_ordem` é um ÍNDICE único, e índice no Postgres não é adiável. Inverter o
        // funil inteiro faz cada linha querer a posição de outra que ainda não se moveu. Sem a
        // passada intermediária isto estoura com "duplicate key value violates unique
        // constraint" — e é o teste mais importante deste arquivo.
        // =====================================================================================
        var (db, tx, s, cenario, _) = await PrepararAsync("reordenar");
        using var _1 = db; using var _2 = tx;

        var antes = await s.ListarAsync(cenario.Pipeline.Id, default);
        // Sem número fixo: quantas etapas o semeador cria é assunto dele, e amarrar o teste a
        // isso faria a próxima mudança lá reprovar um teste que não tem nada a ver com ordem.
        Assert.True(antes.Count >= 3, "o cenário precisa de ao menos 3 etapas para inverter");

        var invertido = antes.Select(e => e.Id).Reverse().ToList();
        await s.ReordenarAsync(cenario.Pipeline.Id, invertido, default);

        db.ChangeTracker.Clear();
        var depois = await s.ListarAsync(cenario.Pipeline.Id, default);

        Assert.Equal(invertido, depois.Select(e => e.Id).ToList());
        // Contígua e começando em 1: é o que faz a posição na tela bater com o número.
        Assert.Equal(Contigua(antes.Count), depois.Select(e => e.Ordem).ToArray());
    }

    [Fact]
    public async Task Reordenar_e_idempotente()
    {
        // A tela manda a ordem inteira. Repetir a mesma requisição — duplo clique, retry de rede
        // — não pode andar com as colunas.
        var (db, tx, s, cenario, _) = await PrepararAsync("idempotente");
        using var _1 = db; using var _2 = tx;

        var ordem = (await s.ListarAsync(cenario.Pipeline.Id, default)).Select(e => e.Id).Reverse().ToList();

        await s.ReordenarAsync(cenario.Pipeline.Id, ordem, default);
        db.ChangeTracker.Clear();
        await s.ReordenarAsync(cenario.Pipeline.Id, ordem, default);
        db.ChangeTracker.Clear();

        Assert.Equal(ordem, (await s.ListarAsync(cenario.Pipeline.Id, default)).Select(e => e.Id).ToList());
    }

    [Fact]
    public async Task LISTA_PARCIAL_DE_ORDEM_E_RECUSADA()
    {
        // Permutação parcial deixaria posição repetida ou buraco, e o erro chegaria ao dono como
        // violação de índice — ilegível para quem só arrastou uma coluna.
        var (db, tx, s, cenario, _) = await PrepararAsync("ordem-parcial");
        using var _1 = db; using var _2 = tx;

        var ids = (await s.ListarAsync(cenario.Pipeline.Id, default)).Select(e => e.Id).ToList();

        // Falta uma.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.ReordenarAsync(cenario.Pipeline.Id, ids.Take(ids.Count - 1).ToList(), default));

        // Id repetido, com a contagem CERTA — é o caso que passaria por uma checagem que só
        // olha o tamanho da lista.
        var comRepetido = ids.ToList();
        comRepetido[^1] = comRepetido[0];
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.ReordenarAsync(cenario.Pipeline.Id, comRepetido, default));

        // Id de fora, também com a contagem certa.
        var comIntruso = ids.ToList();
        comIntruso[^1] = 999_999;
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.ReordenarAsync(cenario.Pipeline.Id, comIntruso, default));
    }

    // ==================================================================== ganho
    [Fact]
    public async Task MOVER_O_GANHO_NAO_DEIXA_DUAS_ETAPAS_MARCADAS()
    {
        // `uq_etapas_ganho` é parcial e único por empresa: marcar a nova antes de desmarcar a
        // antiga viola. É a mesma armadilha do reordenar, em escala menor.
        var (db, tx, s, cenario, _) = await PrepararAsync("ganho");
        using var _1 = db; using var _2 = tx;

        var etapas = await s.ListarAsync(cenario.Pipeline.Id, default);
        var antiga = etapas.Single(e => e.EGanho);
        var nova = etapas.First(e => !e.EGanho);

        await s.DefinirGanhoAsync(nova.Id, default);

        db.ChangeTracker.Clear();
        var depois = await s.ListarAsync(cenario.Pipeline.Id, default);

        Assert.Single(depois.Where(e => e.EGanho));
        Assert.True(depois.Single(e => e.Id == nova.Id).EGanho);
        Assert.False(depois.Single(e => e.Id == antiga.Id).EGanho);
    }

    [Fact]
    public async Task Marcar_como_ganho_a_etapa_que_ja_e_ganho_nao_faz_nada()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("ganho-mesma");
        using var _1 = db; using var _2 = tx;

        var ganho = (await s.ListarAsync(cenario.Pipeline.Id, default)).Single(e => e.EGanho);
        await s.DefinirGanhoAsync(ganho.Id, default);

        db.ChangeTracker.Clear();
        Assert.Single((await s.ListarAsync(cenario.Pipeline.Id, default)).Where(e => e.EGanho));
    }

    [Fact]
    public async Task A_ETAPA_DE_GANHO_NAO_PODE_SER_APAGADA()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("apagar-ganho");
        using var _1 = db; using var _2 = tx;

        var ganho = (await s.ListarAsync(cenario.Pipeline.Id, default)).Single(e => e.EGanho);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(ganho.Id, null, default));
        Assert.Contains("ganho", erro.Message);
    }

    [Fact]
    public async Task O_FUNIL_NAO_PODE_FICAR_SO_COM_A_ETAPA_DE_GANHO()
    {
        // ===================== A INVARIANTE QUE O BANCO NÃO GARANTE =====================
        // O lead novo entra na etapa de MENOR ordem. Se a única etapa restante for a de ganho,
        // todo contato criado já nasce ganho — a "porta única do ganho" (`MoverAsync` recusa a
        // etapa `e_ganho`) cairia por dentro, sem nenhum erro em lugar nenhum.
        // ===============================================================================
        var (db, tx, s, cenario, _) = await PrepararAsync("so-ganho");
        using var _1 = db; using var _2 = tx;

        var etapas = (await s.ListarAsync(cenario.Pipeline.Id, default)).ToList();
        var ganho = etapas.Single(e => e.EGanho);
        var abertas = etapas.Where(e => !e.EGanho).ToList();

        // Tira os negócios do caminho: o que se testa aqui é o teto de etapas, não a FK.
        await db.Negociacoes.IgnoreQueryFilters()
            .Where(n => n.EmpresaId == cenario.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(n => n.EtapaId, ganho.Id));
        db.ChangeTracker.Clear();

        // Apaga todas menos a última aberta.
        for (var i = 0; i < abertas.Count - 1; i++)
        {
            await s.RemoverAsync(abertas[i].Id, null, default);
            db.ChangeTracker.Clear();
        }

        var ultima = abertas[^1];
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(ultima.Id, null, default));
        Assert.Contains("ao menos uma etapa", erro.Message);

        db.ChangeTracker.Clear();
        Assert.Equal(2, (await s.ListarAsync(cenario.Pipeline.Id, default)).Count);
    }

    // ==================================================================== remover com contatos
    [Fact]
    public async Task APAGAR_ETAPA_COM_CONTATOS_EXIGE_DESTINO_E_MOVE_TODOS()
    {
        // `fk_contatos_etapa` é ON DELETE RESTRICT: o banco recusaria de qualquer forma, mas
        // viraria 500 numa tela de configuração. A pergunta é feita ANTES.
        var (db, tx, s, cenario, _) = await PrepararAsync("destino");
        using var _1 = db; using var _2 = tx;

        var etapas = (await s.ListarAsync(cenario.Pipeline.Id, default)).ToList();
        var comContatos = etapas.First(e => !e.EGanho && e.Contatos > 0);
        var destino = etapas.First(e => !e.EGanho && e.Id != comContatos.Id);

        var quantos = comContatos.Contatos;
        Assert.True(quantos > 0);

        // Sem destino: recusa, e diz quantos são.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(comContatos.Id, null, default));
        Assert.Contains(quantos.ToString(), erro.Message);

        db.ChangeTracker.Clear();
        var noDestinoAntes = (await s.ListarAsync(cenario.Pipeline.Id, default)).Single(e => e.Id == destino.Id).Contatos;

        await s.RemoverAsync(comContatos.Id, destino.Id, default);
        db.ChangeTracker.Clear();

        var depois = await s.ListarAsync(cenario.Pipeline.Id, default);
        Assert.DoesNotContain(depois, e => e.Id == comContatos.Id);
        // NENHUM contato se perdeu — todos foram para o destino.
        Assert.Equal(noDestinoAntes + quantos, depois.Single(e => e.Id == destino.Id).Contatos);
        Assert.Equal(0, await db.Negociacoes.IgnoreQueryFilters()
            .CountAsync(n => n.EtapaId == comContatos.Id));
    }

    [Fact]
    public async Task Destino_invalido_e_recusado()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("destino-ruim");
        using var _1 = db; using var _2 = tx;

        var comContatos = (await s.ListarAsync(cenario.Pipeline.Id, default)).First(e => !e.EGanho && e.Contatos > 0);

        // A própria etapa.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(comContatos.Id, comContatos.Id, default));

        // Uma que não existe.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(comContatos.Id, 999_999, default));
    }

    [Fact]
    public async Task A_CONTAGEM_INCLUI_PERDIDO_PORQUE_E_ELE_QUE_TRAVA_A_FK()
    {
        // ===================== POR QUE NÃO USAR `RegrasNegociacao.NoQuadro` AQUI =====================
        // O quadro esconde perdido e anonimizado, mas as duas linhas continuam com `etapa_id`
        // apontando para a etapa — e é isso que a FK enxerga. Contar como o kanban conta mostraria
        // "0 contatos" numa etapa que o banco recusa apagar, e o dono levaria o erro DEPOIS do
        // clique, na forma de um 500.
        // =========================================================================================
        var (db, tx, s, cenario, _) = await PrepararAsync("perdidos");
        using var _1 = db; using var _2 = tx;

        var alvo = (await s.ListarAsync(cenario.Pipeline.Id, default)).First(e => !e.EGanho && e.Contatos > 0);

        // Marca TODOS os negócios da etapa como perdidos: o quadro passaria a mostrar zero.
        var afetados = await db.Negociacoes.IgnoreQueryFilters()
            .Where(n => n.EmpresaId == cenario.Id && n.EtapaId == alvo.Id)
            .ExecuteUpdateAsync(x => x
                .SetProperty(n => n.Status, StatusNegociacao.Perdida)
                .SetProperty(n => n.PerdidaEm, DateTime.UtcNow));
        Assert.True(afetados > 0);
        db.ChangeTracker.Clear();

        // A contagem NÃO caiu para zero.
        Assert.Equal(alvo.Contatos, (await s.ListarAsync(cenario.Pipeline.Id, default)).Single(e => e.Id == alvo.Id).Contatos);

        // E apagar sem destino continua recusado — que é o comportamento correto, porque a FK
        // recusaria.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(alvo.Id, null, default));
    }

    [Fact]
    public async Task Apagar_renumera_a_ordem_sem_deixar_buraco()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("renumerar");
        using var _1 = db; using var _2 = tx;

        // Uma etapa a mais no fim, para que a apagada fique de fato NO MEIO — apagar a última
        // não deixaria buraco nenhum e o teste passaria sem provar nada.
        await s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa("Extra do fim", null), default);
        db.ChangeTracker.Clear();

        var etapas = (await s.ListarAsync(cenario.Pipeline.Id, default)).ToList();
        var doMeio = etapas.First(e => !e.EGanho && e.Ordem > 1);
        var destino = etapas.First(e => !e.EGanho && e.Id != doMeio.Id);
        Assert.True(doMeio.Ordem < etapas[^1].Ordem, "a etapa apagada precisa ter alguma depois");

        await s.RemoverAsync(doMeio.Id, destino.Id, default);
        db.ChangeTracker.Clear();

        var depois = await s.ListarAsync(cenario.Pipeline.Id, default);
        Assert.Equal(Contigua(etapas.Count - 1), depois.Select(e => e.Ordem).ToArray());
    }

    // ==================================================================== criar e editar
    [Fact]
    public async Task ETAPA_NOVA_ENTRA_NO_FIM_E_NUNCA_COMO_GANHO()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("criar");
        using var _1 = db; using var _2 = tx;

        var antes = (await s.ListarAsync(cenario.Pipeline.Id, default)).Count;

        var id = await s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa("Pós-venda", "#7FA88B"), default);
        db.ChangeTracker.Clear();

        var lista = await s.ListarAsync(cenario.Pipeline.Id, default);
        var nova = lista.Single(e => e.Id == id);

        Assert.Equal(antes + 1, lista.Count);
        Assert.Equal((short)(antes + 1), nova.Ordem);
        Assert.False(nova.EGanho);
        Assert.Equal(0, nova.Contatos);
        // A de ganho continua sendo uma só, e a mesma.
        Assert.Single(lista.Where(e => e.EGanho));
    }

    [Fact]
    public async Task Nome_repetido_e_recusado_na_criacao_e_na_edicao()
    {
        // Duas colunas "Proposta" tornam o funil inútil para responder onde o negócio está.
        var (db, tx, s, cenario, _) = await PrepararAsync("nome-repetido");
        using var _1 = db; using var _2 = tx;

        var etapas = (await s.ListarAsync(cenario.Pipeline.Id, default)).ToList();

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa(etapas[0].Nome, null), default));

        // Caixa diferente também colide.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa(etapas[0].Nome.ToUpperInvariant(), null), default));

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.AtualizarAsync(etapas[1].Id, new EditarEtapa(etapas[0].Nome, null), default));

        // Mas manter o PRÓPRIO nome ao editar a cor tem que passar.
        await s.AtualizarAsync(etapas[1].Id, new EditarEtapa(etapas[1].Nome, "#123ABC"), default);
    }

    [Fact]
    public async Task A_ETAPA_DE_GANHO_PODE_SER_RENOMEADA()
    {
        // A flag `e_ganho` existe justamente para a conversão não depender do nome — é o que
        // deixa a empresa chamar "Venda" de "Contrato assinado".
        var (db, tx, s, cenario, _) = await PrepararAsync("renomear-ganho");
        using var _1 = db; using var _2 = tx;

        var ganho = (await s.ListarAsync(cenario.Pipeline.Id, default)).Single(e => e.EGanho);
        await s.AtualizarAsync(ganho.Id, new EditarEtapa("Contrato assinado", null), default);

        db.ChangeTracker.Clear();
        var depois = (await s.ListarAsync(cenario.Pipeline.Id, default)).Single(e => e.Id == ganho.Id);
        Assert.Equal("Contrato assinado", depois.Nome);
        Assert.True(depois.EGanho);
    }

    [Fact]
    public async Task COR_INVALIDA_E_RECUSADA()
    {
        // A cor vai direto para o `style` do cabeçalho da coluna. Texto livre aqui seria deixar
        // o dono escrever CSS na tela de todo mundo da empresa.
        var (db, tx, s, cenario, _) = await PrepararAsync("cor");
        using var _1 = db; using var _2 = tx;

        foreach (var ruim in new[] { "vermelho", "#GGG", "#12345", "red; background:url(x)" })
            await Assert.ThrowsAsync<RegraDeNegocioException>(
                () => s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa($"Etapa {ruim.Length}", ruim), default));

        // Vazio cai no padrão, sem erro.
        var id = await s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa("Sem cor", null), default);
        db.ChangeTracker.Clear();
        Assert.Equal("#2F5D3A", (await s.ListarAsync(cenario.Pipeline.Id, default)).Single(e => e.Id == id).Cor);
    }

    [Fact]
    public async Task O_TETO_DE_ETAPAS_E_RESPEITADO()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("teto");
        using var _1 = db; using var _2 = tx;

        for (var i = (await s.ListarAsync(cenario.Pipeline.Id, default)).Count; i < ServicoEtapas.MaximoEtapas; i++)
        {
            await s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa($"Extra {i}", null), default);
            db.ChangeTracker.Clear();
        }

        Assert.Equal(ServicoEtapas.MaximoEtapas, (await s.ListarAsync(cenario.Pipeline.Id, default)).Count);
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa("Uma a mais", null), default));
    }

    // ==================================================================== tenant
    [Fact]
    public async Task ETAPA_DE_OUTRA_EMPRESA_NAO_E_ALCANCAVEL()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("tenant");
        using var _1 = db; using var _2 = tx;

        var outra = await Semeador.TenantAsync(db, "etapas-outro-tenant");
        var etapaDaOutra = await db.EtapasFunil.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.EmpresaId == outra.Id).OrderBy(e => e.Ordem).FirstAsync();

        // "Não encontrada" e não "sem permissão": distinguir contaria que a etapa existe noutra
        // empresa.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.AtualizarAsync(etapaDaOutra.Id, new EditarEtapa("Invadida", null), default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(etapaDaOutra.Id, null, default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.DefinirGanhoAsync(etapaDaOutra.Id, default));

        db.ChangeTracker.Clear();
        Assert.Equal("Novo Lead", (await db.EtapasFunil.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == etapaDaOutra.Id)).Nome);

        // E a lista continua vendo só as próprias.
        Assert.All(await s.ListarAsync(cenario.Pipeline.Id, default),
            e => Assert.DoesNotContain(e.Id, new[] { etapaDaOutra.Id }));
    }

    // ==================================================================== apoio

    // ==================================================================== dois funis
    /// <summary>⚠️ CADA FUNIL TEM AS SUAS ETAPAS, E NENHUM TESTE PROVAVA ISSO.
    ///
    /// Todos os testes deste arquivo usavam UMA pipeline — a do cenário. A isolação entre funis
    /// era só consequência do código estar certo, sem nada exigindo que continuasse.
    ///
    /// E o buraco custou caro: a tela de configuração passou a existir sem ler o `:pipeline` da
    /// rota, e quem abria "Pós-venda" editava as etapas de "Vendas". O defeito era do cliente,
    /// mas a ausência DESTES testes é o que deixou a invariante sem dono.
    ///
    /// ⚠️ O TESTE DÁ AOS DOIS FUNIS UMA ETAPA DE MESMO NOME ANTES DE RENOMEAR, e isso é o que
    /// o torna capaz de pegar alguma coisa. A primeira versão que escrevi não fazia isso — o
    /// cenário nasce com "Novo Lead/Proposta/Venda" e a pipeline nova com "Entrada/Fechado", sem
    /// nome em comum. Um vazamento por nome não teria o que atingir, e o teste passava mesmo com
    /// a isolação quebrada de propósito.</summary>
    [Fact]
    public async Task RENOMEAR_ETAPA_DE_UM_FUNIL_NAO_TOCA_NO_OUTRO()
    {
        var (db, tx, s, cenario, ctx) = await PrepararAsync("dois-funis-renomear");
        using var _1 = db; using var _2 = tx;

        var outroId = await new ServicoPipelines(db, ctx)
            .CriarAsync(new NovaPipeline("Pós-venda", null), default);
        db.ChangeTracker.Clear();

        var vendas = (await s.ListarAsync(cenario.Pipeline.Id, default)).ToList();
        var alvo = vendas[0];

        // A ARMADILHA: a mesma etapa, pelo nome, existindo nos dois funis.
        await s.CriarAsync(outroId, new NovaEtapa(alvo.Nome, null), default);
        db.ChangeTracker.Clear();

        var posVenda = (await s.ListarAsync(outroId, default)).ToList();
        Assert.Contains(posVenda, e => e.Nome == alvo.Nome);

        // Os dois conjuntos são DISJUNTOS: nenhuma linha é compartilhada.
        Assert.Empty(vendas.Select(e => e.Id).Intersect(posVenda.Select(e => e.Id)));

        var antesNoOutro = posVenda.Select(e => (e.Id, e.Nome)).ToList();

        await s.AtualizarAsync(alvo.Id, new EditarEtapa("Separado", null), default);
        db.ChangeTracker.Clear();

        // O funil editado mudou…
        var vendasDepois = (await s.ListarAsync(cenario.Pipeline.Id, default)).ToList();
        Assert.Equal("Separado", vendasDepois.Single(e => e.Id == alvo.Id).Nome);

        // …e o outro está exatamente como estava, linha por linha.
        var outroDepois = (await s.ListarAsync(outroId, default)).Select(e => (e.Id, e.Nome)).ToList();
        Assert.Equal(antesNoOutro, outroDepois);
    }

    /// <summary>O MESMO NOME PODE EXISTIR NOS DOIS FUNIS, e isso não é descuido.
    ///
    /// "Entrada" em Vendas e "Entrada" em Pós-venda são etapas diferentes de processos
    /// diferentes. A unicidade é POR FUNIL — `ExigirNomeLivre` só olha as irmãs de pipeline.
    /// Exigir nome único por empresa obrigaria o dono a inventar sufixos.</summary>
    [Fact]
    public async Task O_MESMO_NOME_DE_ETAPA_VALE_EM_FUNIS_DIFERENTES()
    {
        var (db, tx, s, cenario, ctx) = await PrepararAsync("dois-funis-nome");
        using var _1 = db; using var _2 = tx;

        var outroId = await new ServicoPipelines(db, ctx)
            .CriarAsync(new NovaPipeline("Pós-venda", null), default);
        db.ChangeTracker.Clear();

        var nome = (await s.ListarAsync(cenario.Pipeline.Id, default)).First().Nome;

        // Mesmo nome, outro funil: aceito.
        var novaId = await s.CriarAsync(outroId, new NovaEtapa(nome, null), default);
        db.ChangeTracker.Clear();

        Assert.Equal(nome, (await s.ListarAsync(outroId, default)).Single(e => e.Id == novaId).Nome);

        // E DENTRO do mesmo funil continua recusado.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(outroId, new NovaEtapa(nome, null), default));
    }

    /// <summary>Reordenar um funil não mexe na ordem do outro. `ReordenarAsync` recebe a pipeline
    /// e exige a lista COMPLETA dela — passar ids do funil vizinho é recusado, e não aplicado
    /// pela metade.</summary>
    [Fact]
    public async Task REORDENAR_UM_FUNIL_NAO_MEXE_NA_ORDEM_DO_OUTRO()
    {
        var (db, tx, s, cenario, ctx) = await PrepararAsync("dois-funis-ordem");
        using var _1 = db; using var _2 = tx;

        var outroId = await new ServicoPipelines(db, ctx)
            .CriarAsync(new NovaPipeline("Pós-venda", null), default);
        db.ChangeTracker.Clear();

        var antesNoOutro = (await s.ListarAsync(outroId, default))
            .Select(e => (e.Id, e.Ordem)).ToList();

        var invertido = (await s.ListarAsync(cenario.Pipeline.Id, default))
            .Select(e => e.Id).Reverse().ToList();
        await s.ReordenarAsync(cenario.Pipeline.Id, invertido, default);
        db.ChangeTracker.Clear();

        Assert.Equal(antesNoOutro,
            (await s.ListarAsync(outroId, default)).Select(e => (e.Id, e.Ordem)).ToList());

        // E a ordem de um funil não serve para o outro: a lista não é dele.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.ReordenarAsync(outroId, invertido, default));
    }

    /// <summary>A etapa de ganho é por FUNIL. Marcar outra em Vendas não pode desmarcar a de
    /// Pós-venda — cada funil precisa da sua porta de saída, ou o quadro dele fica sem coluna de
    /// ganho e `MarcarGanhoAsync` não acha destino.</summary>
    [Fact]
    public async Task DEFINIR_GANHO_NUM_FUNIL_NAO_DESMARCA_O_DO_OUTRO()
    {
        var (db, tx, s, cenario, ctx) = await PrepararAsync("dois-funis-ganho");
        using var _1 = db; using var _2 = tx;

        var outroId = await new ServicoPipelines(db, ctx)
            .CriarAsync(new NovaPipeline("Pós-venda", null), default);
        db.ChangeTracker.Clear();

        var ganhoDoOutro = (await s.ListarAsync(outroId, default)).Single(e => e.EGanho);

        // Em Vendas, promove outra etapa a ganho.
        var candidata = (await s.ListarAsync(cenario.Pipeline.Id, default)).First(e => !e.EGanho);
        await s.DefinirGanhoAsync(candidata.Id, default);
        db.ChangeTracker.Clear();

        // Vendas trocou…
        var vendas = (await s.ListarAsync(cenario.Pipeline.Id, default)).ToList();
        Assert.Equal(candidata.Id, vendas.Single(e => e.EGanho).Id);

        // …e Pós-venda continua com a dele.
        Assert.Equal(ganhoDoOutro.Id, (await s.ListarAsync(outroId, default)).Single(e => e.EGanho).Id);
    }

    // ============================================== POS-1 · mexer nas etapas nao desfaz a venda

    /// <summary>===================== O RISCO MAIS GRAVE DESTE BLOCO =====================
    ///
    /// Com etapas depois da venda, mexer na ORDEM delas pode desfazer a venda de todo mundo — e sem
    /// ninguém arrastar card nenhum.
    ///
    /// Jogue a etapa de ganho para o fim e todo card vendido passa a estar, retroativamente, numa
    /// coluna "pré-venda". Eles continuam VISÍVEIS (o recorte largo cuida disso), e é justamente por
    /// isso que o estrago não aparece na hora: o `NOT EXISTS` da `ConclusaoAutomatica` passa a ler
    /// "não há etapa de ganho antes de mim" e **conclui todos na rodada da noite**.
    ///
    /// Um arrasto na tela de Configurações, nada na tela, e de manhã a coluna de pós-venda sumiu.
    /// ========================================================================</summary>
    [Fact]
    public async Task REORDENAR_NAO_JOGA_A_ETAPA_DE_GANHO_PARA_DEPOIS_DE_UM_CARD_VENDIDO()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("pos1-reordenar");
        using var _1 = db; using var _2 = tx;

        var posVenda = await PosVendaComPedidoAsync(db, s, cenario);

        var etapas = (await s.ListarAsync(cenario.Pipeline.Id, default)).ToList();
        var ganho = etapas.Single(e => e.EGanho);

        // A permutação que machuca: a etapa de ganho vai para o fim, depois da pós-venda.
        var novaOrdem = etapas.Where(e => e.Id != ganho.Id).Select(e => e.Id)
            .Concat([ganho.Id]).ToList();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.ReordenarAsync(cenario.Pipeline.Id, novaOrdem, default));

        Assert.Contains("atrás da etapa de venda", erro.Message);
        // ⚠️ A FRASE DIZ O EFEITO, não a regra. Quem lê precisa saber o que aconteceria, não que
        // uma invariante foi violada.
        Assert.Contains("conclusão automática", erro.Message);

        // E nada foi escrito: o guarda roda antes da primeira passada.
        db.ChangeTracker.Clear();
        Assert.Equal(ganho.Ordem,
            (await db.EtapasFunil.AsNoTracking().SingleAsync(e => e.Id == ganho.Id)).Ordem);
        _ = posVenda;
    }

    [Fact]
    public async Task REORDENAR_SEM_CARD_VENDIDO_CONTINUA_LIVRE()
    {
        // O guarda não pode virar um pedágio na tela de etapas. Sem pedido vendido no funil — que é
        // o caso de quase toda reordenação — ele nem chega a perguntar as ordens.
        var (db, tx, s, cenario, _) = await PrepararAsync("pos1-reordenar-livre");
        using var _1 = db; using var _2 = tx;

        var etapas = (await s.ListarAsync(cenario.Pipeline.Id, default)).ToList();
        var ganho = etapas.Single(e => e.EGanho);
        var novaOrdem = etapas.Where(e => e.Id != ganho.Id).Select(e => e.Id)
            .Concat([ganho.Id]).ToList();

        await s.ReordenarAsync(cenario.Pipeline.Id, novaOrdem, default);

        db.ChangeTracker.Clear();
        Assert.Equal((short)3,
            (await db.EtapasFunil.AsNoTracking().SingleAsync(e => e.Id == ganho.Id)).Ordem);
    }

    /// <summary>⚠️ A PORTA MAIS PROVÁVEL DAS TRÊS. "Agora quem fecha é Entregue" é um clique
    /// natural na tela de etapas — e empurrar a marca de ganho para frente deixa atrás dela todo
    /// pedido que já estava vendido.</summary>
    [Fact]
    public async Task MUDAR_A_ETAPA_DE_GANHO_PARA_A_FRENTE_E_RECUSADO_COM_PEDIDO_VENDIDO()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("pos1-definir-ganho");
        using var _1 = db; using var _2 = tx;

        var posVenda = await PosVendaComPedidoAsync(db, s, cenario);

        // ⚠️ PRECISA DE UMA SEGUNDA ETAPA DEPOIS DO PEDIDO. Marcar como ganho a etapa ONDE o pedido
        // está não o deixa atrás de nada — ele passa a estar NA etapa de ganho, e concluir pelo
        // prazo volta a ser o certo. Descobri isto escrevendo este teste com um cenário mais curto,
        // e o guarda estava certo: era o cenário que não reproduzia o perigo.
        var entregue = await s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa("Entregue", null), default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.DefinirGanhoAsync(entregue, default));

        Assert.Contains("atrás da etapa de venda", erro.Message);
        _ = posVenda;

        // A marca não se mexeu.
        db.ChangeTracker.Clear();
        Assert.Equal(cenario.Etapas.Single(e => e.EGanho).Id,
            (await db.EtapasFunil.AsNoTracking()
                .SingleAsync(e => e.EGanho && e.PipelineId == cenario.Pipeline.Id)).Id);
    }

    [Fact]
    public async Task REORDENAR_PASSA_MESMO_COM_PEDIDO_JA_ATRAS_DA_VENDA()
    {
        // ===================== O GUARDA SO RECUSA O QUE A MUDANCA PIORA =====================
        // Negocio `ganha` numa etapa ANTES da de ganho ja existe: funil sem etapa de ganho, e linhas
        // de antes do guarda do E4c/2. Se o guarda recusasse por causa do estado atual em vez da
        // PIORA, essa empresa nunca mais conseguiria reordenar as etapas dela — barrada por um
        // estado que ela nao criou e que nao tem como consertar daquela tela.
        //
        // A pergunta e "estava do lado certo e passa para o errado?", e este teste e o unico lugar
        // onde a primeira metade dessa pergunta e exercitada.
        // ================================================================================
        var (db, tx, s, cenario, _) = await PrepararAsync("pos1-ja-atras");
        using var _1 = db; using var _2 = tx;

        // O estado legado: vendido, parado na Proposta.
        var negociacao = await db.Negociacoes.FirstAsync(n => n.Status == StatusNegociacao.Aberta);
        negociacao.Status = StatusNegociacao.Ganha;
        negociacao.Valor = 150m;
        negociacao.GanhaEm = DateTime.UtcNow;
        negociacao.EtapaId = cenario.Etapas.Single(e => e.Ordem == 2).Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // ⚠️ A PERMUTACAO IMPORTA, e a primeira que eu escrevi nao servia: inverter a lista levava a
        // etapa de ganho para a FRENTE, e o pedido legado passava a estar depois dela — o estado
        // MELHORAVA, e um guarda errado deixaria passar de qualquer jeito.
        //
        // Esta troca mantem a etapa de ganho no fim e so inverte as duas de negociacao: o pedido
        // continua atras da venda antes e depois, que e exatamente o caso a tolerar.
        var etapas = (await s.ListarAsync(cenario.Pipeline.Id, default)).ToList();
        var ganho = etapas.Single(e => e.EGanho);
        var trocada = new List<long> { etapas[1].Id, etapas[0].Id, ganho.Id };

        await s.ReordenarAsync(cenario.Pipeline.Id, trocada, default);

        var eraASegunda = etapas[1].Id;

        db.ChangeTracker.Clear();
        Assert.Equal((short)1, (await db.EtapasFunil.AsNoTracking()
            .SingleAsync(e => e.Id == eraASegunda)).Ordem);
    }

    [Fact]
    public async Task MARCAR_COMO_GANHO_A_ETAPA_ONDE_O_PEDIDO_ESTA_PASSA()
    {
        // O outro lado da regra, e e o que prova que o guarda pergunta a coisa certa: a empresa que
        // decide "na verdade quem fecha e Pos-Venda" nao e barrada. O pedido nao fica ATRAS da
        // venda — ele fica NELA.
        var (db, tx, s, cenario, _) = await PrepararAsync("pos1-ganho-mesma-etapa");
        using var _1 = db; using var _2 = tx;

        var posVenda = await PosVendaComPedidoAsync(db, s, cenario);

        await s.DefinirGanhoAsync(posVenda, default);

        db.ChangeTracker.Clear();
        Assert.Equal(posVenda, (await db.EtapasFunil.AsNoTracking()
            .SingleAsync(e => e.EGanho && e.PipelineId == cenario.Pipeline.Id)).Id);
    }

    [Fact]
    public async Task APAGAR_A_POS_VENDA_NAO_MANDA_O_PEDIDO_PARA_UMA_ETAPA_DE_NEGOCIACAO()
    {
        // ⚠️ APAGAR TAMBÉM MOVE CARD, e por um `ExecuteUpdateAsync` que não passa por `MoverAsync` —
        // então `RegrasDoQuadro` nunca o veria. O destino é escolha do dono, e "Proposta" está na
        // lista de destinos possíveis.
        var (db, tx, s, cenario, _) = await PrepararAsync("pos1-apagar");
        using var _1 = db; using var _2 = tx;

        var posVenda = await PosVendaComPedidoAsync(db, s, cenario);
        var proposta = cenario.Etapas.Single(e => e.Ordem == 2).Id;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(posVenda, proposta, default));

        Assert.Contains("atrás da etapa de venda", erro.Message);

        db.ChangeTracker.Clear();
        Assert.True(await db.EtapasFunil.AnyAsync(e => e.Id == posVenda));
    }

    [Fact]
    public async Task APAGAR_A_POS_VENDA_MANDANDO_PARA_OUTRA_POS_VENDA_PASSA()
    {
        // O lado que tem de continuar funcionando: consolidar duas etapas de pós-venda numa é
        // operação legítima, e o guarda não pode impedi-la.
        var (db, tx, s, cenario, _) = await PrepararAsync("pos1-apagar-ok");
        using var _1 = db; using var _2 = tx;

        var posVenda = await PosVendaComPedidoAsync(db, s, cenario);
        var entregue = await s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa("Entregue", null), default);

        await s.RemoverAsync(posVenda, entregue, default);

        db.ChangeTracker.Clear();
        Assert.False(await db.EtapasFunil.AnyAsync(e => e.Id == posVenda));
        Assert.Equal(entregue, (await db.Negociacoes.AsNoTracking()
            .SingleAsync(n => n.Status == StatusNegociacao.Ganha)).EtapaId);
    }

    /// <summary>Cria a etapa "Pós-Venda" depois da de ganho e põe nela um pedido VENDIDO. Devolve o
    /// id da etapa.
    ///
    /// A negociação do Semeador é promovida direto no banco: aqui o objeto de teste é a tela de
    /// etapas, e passar por `MarcarGanhoAsync` + `MoverAsync` só acrescentaria duas regras entre o
    /// cenário e o que se mede.</summary>
    private static async Task<long> PosVendaComPedidoAsync(
        NexoraDbContext db, IServicoEtapas s, Cenario cenario)
    {
        var posVenda = await s.CriarAsync(cenario.Pipeline.Id, new NovaEtapa("Pós-Venda", null), default);

        var negociacao = await db.Negociacoes.FirstAsync(n => n.Status == StatusNegociacao.Aberta);
        negociacao.Status = StatusNegociacao.Ganha;
        negociacao.Valor = 350m;
        negociacao.GanhaEm = DateTime.UtcNow;
        negociacao.EtapaId = posVenda;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return posVenda;
    }

    /// <summary>1, 2, 3… n. A ordem tem que ser contígua e começar em 1 — é o que faz a posição
    /// na tela bater com o número guardado.</summary>
    private static short[] Contigua(int n) =>
        Enumerable.Range(1, n).Select(i => (short)i).ToArray();

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, IServicoEtapas Servico,
        Cenario Cenario, ContextoMutavel Contexto)> PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"etapas-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new ServicoEtapas(db, ctx), cenario, ctx);
    }
}
