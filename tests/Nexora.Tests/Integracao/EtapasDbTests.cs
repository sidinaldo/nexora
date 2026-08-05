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
        var (db, tx, s, _) = await PrepararAsync("reordenar");
        using var _1 = db; using var _2 = tx;

        var antes = await s.ListarAsync(default);
        // Sem número fixo: quantas etapas o semeador cria é assunto dele, e amarrar o teste a
        // isso faria a próxima mudança lá reprovar um teste que não tem nada a ver com ordem.
        Assert.True(antes.Count >= 3, "o cenário precisa de ao menos 3 etapas para inverter");

        var invertido = antes.Select(e => e.Id).Reverse().ToList();
        await s.ReordenarAsync(invertido, default);

        db.ChangeTracker.Clear();
        var depois = await s.ListarAsync(default);

        Assert.Equal(invertido, depois.Select(e => e.Id).ToList());
        // Contígua e começando em 1: é o que faz a posição na tela bater com o número.
        Assert.Equal(Contigua(antes.Count), depois.Select(e => e.Ordem).ToArray());
    }

    [Fact]
    public async Task Reordenar_e_idempotente()
    {
        // A tela manda a ordem inteira. Repetir a mesma requisição — duplo clique, retry de rede
        // — não pode andar com as colunas.
        var (db, tx, s, _) = await PrepararAsync("idempotente");
        using var _1 = db; using var _2 = tx;

        var ordem = (await s.ListarAsync(default)).Select(e => e.Id).Reverse().ToList();

        await s.ReordenarAsync(ordem, default);
        db.ChangeTracker.Clear();
        await s.ReordenarAsync(ordem, default);
        db.ChangeTracker.Clear();

        Assert.Equal(ordem, (await s.ListarAsync(default)).Select(e => e.Id).ToList());
    }

    [Fact]
    public async Task LISTA_PARCIAL_DE_ORDEM_E_RECUSADA()
    {
        // Permutação parcial deixaria posição repetida ou buraco, e o erro chegaria ao dono como
        // violação de índice — ilegível para quem só arrastou uma coluna.
        var (db, tx, s, _) = await PrepararAsync("ordem-parcial");
        using var _1 = db; using var _2 = tx;

        var ids = (await s.ListarAsync(default)).Select(e => e.Id).ToList();

        // Falta uma.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.ReordenarAsync(ids.Take(ids.Count - 1).ToList(), default));

        // Id repetido, com a contagem CERTA — é o caso que passaria por uma checagem que só
        // olha o tamanho da lista.
        var comRepetido = ids.ToList();
        comRepetido[^1] = comRepetido[0];
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.ReordenarAsync(comRepetido, default));

        // Id de fora, também com a contagem certa.
        var comIntruso = ids.ToList();
        comIntruso[^1] = 999_999;
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.ReordenarAsync(comIntruso, default));
    }

    // ==================================================================== ganho
    [Fact]
    public async Task MOVER_O_GANHO_NAO_DEIXA_DUAS_ETAPAS_MARCADAS()
    {
        // `uq_etapas_ganho` é parcial e único por empresa: marcar a nova antes de desmarcar a
        // antiga viola. É a mesma armadilha do reordenar, em escala menor.
        var (db, tx, s, _) = await PrepararAsync("ganho");
        using var _1 = db; using var _2 = tx;

        var etapas = await s.ListarAsync(default);
        var antiga = etapas.Single(e => e.EGanho);
        var nova = etapas.First(e => !e.EGanho);

        await s.DefinirGanhoAsync(nova.Id, default);

        db.ChangeTracker.Clear();
        var depois = await s.ListarAsync(default);

        Assert.Single(depois.Where(e => e.EGanho));
        Assert.True(depois.Single(e => e.Id == nova.Id).EGanho);
        Assert.False(depois.Single(e => e.Id == antiga.Id).EGanho);
    }

    [Fact]
    public async Task Marcar_como_ganho_a_etapa_que_ja_e_ganho_nao_faz_nada()
    {
        var (db, tx, s, _) = await PrepararAsync("ganho-mesma");
        using var _1 = db; using var _2 = tx;

        var ganho = (await s.ListarAsync(default)).Single(e => e.EGanho);
        await s.DefinirGanhoAsync(ganho.Id, default);

        db.ChangeTracker.Clear();
        Assert.Single((await s.ListarAsync(default)).Where(e => e.EGanho));
    }

    [Fact]
    public async Task A_ETAPA_DE_GANHO_NAO_PODE_SER_APAGADA()
    {
        var (db, tx, s, _) = await PrepararAsync("apagar-ganho");
        using var _1 = db; using var _2 = tx;

        var ganho = (await s.ListarAsync(default)).Single(e => e.EGanho);

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
        var (db, tx, s, cenario) = await PrepararAsync("so-ganho");
        using var _1 = db; using var _2 = tx;

        var etapas = (await s.ListarAsync(default)).ToList();
        var ganho = etapas.Single(e => e.EGanho);
        var abertas = etapas.Where(e => !e.EGanho).ToList();

        // Tira os contatos do caminho: o que se testa aqui é o teto de etapas, não a FK.
        await db.Contatos.IgnoreQueryFilters()
            .Where(c => c.EmpresaId == cenario.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.EtapaId, ganho.Id));
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
        Assert.Equal(2, (await s.ListarAsync(default)).Count);
    }

    // ==================================================================== remover com contatos
    [Fact]
    public async Task APAGAR_ETAPA_COM_CONTATOS_EXIGE_DESTINO_E_MOVE_TODOS()
    {
        // `fk_contatos_etapa` é ON DELETE RESTRICT: o banco recusaria de qualquer forma, mas
        // viraria 500 numa tela de configuração. A pergunta é feita ANTES.
        var (db, tx, s, cenario) = await PrepararAsync("destino");
        using var _1 = db; using var _2 = tx;

        var etapas = (await s.ListarAsync(default)).ToList();
        var comContatos = etapas.First(e => !e.EGanho && e.Contatos > 0);
        var destino = etapas.First(e => !e.EGanho && e.Id != comContatos.Id);

        var quantos = comContatos.Contatos;
        Assert.True(quantos > 0);

        // Sem destino: recusa, e diz quantos são.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(comContatos.Id, null, default));
        Assert.Contains(quantos.ToString(), erro.Message);

        db.ChangeTracker.Clear();
        var noDestinoAntes = (await s.ListarAsync(default)).Single(e => e.Id == destino.Id).Contatos;

        await s.RemoverAsync(comContatos.Id, destino.Id, default);
        db.ChangeTracker.Clear();

        var depois = await s.ListarAsync(default);
        Assert.DoesNotContain(depois, e => e.Id == comContatos.Id);
        // NENHUM contato se perdeu — todos foram para o destino.
        Assert.Equal(noDestinoAntes + quantos, depois.Single(e => e.Id == destino.Id).Contatos);
        Assert.Equal(0, await db.Contatos.IgnoreQueryFilters()
            .CountAsync(c => c.EtapaId == comContatos.Id));
    }

    [Fact]
    public async Task Destino_invalido_e_recusado()
    {
        var (db, tx, s, _) = await PrepararAsync("destino-ruim");
        using var _1 = db; using var _2 = tx;

        var comContatos = (await s.ListarAsync(default)).First(e => !e.EGanho && e.Contatos > 0);

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
        // ===================== POR QUE NÃO USAR `RegrasContato.NoQuadro` AQUI =====================
        // O quadro esconde perdido e anonimizado, mas as duas linhas continuam com `etapa_id`
        // apontando para a etapa — e é isso que a FK enxerga. Contar como o kanban conta mostraria
        // "0 contatos" numa etapa que o banco recusa apagar, e o dono levaria o erro DEPOIS do
        // clique, na forma de um 500.
        // =========================================================================================
        var (db, tx, s, cenario) = await PrepararAsync("perdidos");
        using var _1 = db; using var _2 = tx;

        var alvo = (await s.ListarAsync(default)).First(e => !e.EGanho && e.Contatos > 0);

        // Marca TODOS os contatos da etapa como perdidos: o quadro passaria a mostrar zero.
        var afetados = await db.Contatos.IgnoreQueryFilters()
            .Where(c => c.EmpresaId == cenario.Id && c.EtapaId == alvo.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.PerdidoEm, DateTime.UtcNow));
        Assert.True(afetados > 0);
        db.ChangeTracker.Clear();

        // A contagem NÃO caiu para zero.
        Assert.Equal(alvo.Contatos, (await s.ListarAsync(default)).Single(e => e.Id == alvo.Id).Contatos);

        // E apagar sem destino continua recusado — que é o comportamento correto, porque a FK
        // recusaria.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(alvo.Id, null, default));
    }

    [Fact]
    public async Task Apagar_renumera_a_ordem_sem_deixar_buraco()
    {
        var (db, tx, s, cenario) = await PrepararAsync("renumerar");
        using var _1 = db; using var _2 = tx;

        // Uma etapa a mais no fim, para que a apagada fique de fato NO MEIO — apagar a última
        // não deixaria buraco nenhum e o teste passaria sem provar nada.
        await s.CriarAsync(new NovaEtapa("Extra do fim", null), default);
        db.ChangeTracker.Clear();

        var etapas = (await s.ListarAsync(default)).ToList();
        var doMeio = etapas.First(e => !e.EGanho && e.Ordem > 1);
        var destino = etapas.First(e => !e.EGanho && e.Id != doMeio.Id);
        Assert.True(doMeio.Ordem < etapas[^1].Ordem, "a etapa apagada precisa ter alguma depois");

        await s.RemoverAsync(doMeio.Id, destino.Id, default);
        db.ChangeTracker.Clear();

        var depois = await s.ListarAsync(default);
        Assert.Equal(Contigua(etapas.Count - 1), depois.Select(e => e.Ordem).ToArray());
    }

    // ==================================================================== criar e editar
    [Fact]
    public async Task ETAPA_NOVA_ENTRA_NO_FIM_E_NUNCA_COMO_GANHO()
    {
        var (db, tx, s, _) = await PrepararAsync("criar");
        using var _1 = db; using var _2 = tx;

        var antes = (await s.ListarAsync(default)).Count;

        var id = await s.CriarAsync(new NovaEtapa("Pós-venda", "#7FA88B"), default);
        db.ChangeTracker.Clear();

        var lista = await s.ListarAsync(default);
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
        var (db, tx, s, _) = await PrepararAsync("nome-repetido");
        using var _1 = db; using var _2 = tx;

        var etapas = (await s.ListarAsync(default)).ToList();

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtapa(etapas[0].Nome, null), default));

        // Caixa diferente também colide.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtapa(etapas[0].Nome.ToUpperInvariant(), null), default));

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
        var (db, tx, s, _) = await PrepararAsync("renomear-ganho");
        using var _1 = db; using var _2 = tx;

        var ganho = (await s.ListarAsync(default)).Single(e => e.EGanho);
        await s.AtualizarAsync(ganho.Id, new EditarEtapa("Contrato assinado", null), default);

        db.ChangeTracker.Clear();
        var depois = (await s.ListarAsync(default)).Single(e => e.Id == ganho.Id);
        Assert.Equal("Contrato assinado", depois.Nome);
        Assert.True(depois.EGanho);
    }

    [Fact]
    public async Task COR_INVALIDA_E_RECUSADA()
    {
        // A cor vai direto para o `style` do cabeçalho da coluna. Texto livre aqui seria deixar
        // o dono escrever CSS na tela de todo mundo da empresa.
        var (db, tx, s, _) = await PrepararAsync("cor");
        using var _1 = db; using var _2 = tx;

        foreach (var ruim in new[] { "vermelho", "#GGG", "#12345", "red; background:url(x)" })
            await Assert.ThrowsAsync<RegraDeNegocioException>(
                () => s.CriarAsync(new NovaEtapa($"Etapa {ruim.Length}", ruim), default));

        // Vazio cai no padrão, sem erro.
        var id = await s.CriarAsync(new NovaEtapa("Sem cor", null), default);
        db.ChangeTracker.Clear();
        Assert.Equal("#2F5D3A", (await s.ListarAsync(default)).Single(e => e.Id == id).Cor);
    }

    [Fact]
    public async Task O_TETO_DE_ETAPAS_E_RESPEITADO()
    {
        var (db, tx, s, _) = await PrepararAsync("teto");
        using var _1 = db; using var _2 = tx;

        for (var i = (await s.ListarAsync(default)).Count; i < ServicoEtapas.MaximoEtapas; i++)
        {
            await s.CriarAsync(new NovaEtapa($"Extra {i}", null), default);
            db.ChangeTracker.Clear();
        }

        Assert.Equal(ServicoEtapas.MaximoEtapas, (await s.ListarAsync(default)).Count);
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtapa("Uma a mais", null), default));
    }

    // ==================================================================== tenant
    [Fact]
    public async Task ETAPA_DE_OUTRA_EMPRESA_NAO_E_ALCANCAVEL()
    {
        var (db, tx, s, cenario) = await PrepararAsync("tenant");
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
        Assert.All(await s.ListarAsync(default),
            e => Assert.DoesNotContain(e.Id, new[] { etapaDaOutra.Id }));
    }

    // ==================================================================== apoio

    /// <summary>1, 2, 3… n. A ordem tem que ser contígua e começar em 1 — é o que faz a posição
    /// na tela bater com o número guardado.</summary>
    private static short[] Contigua(int n) =>
        Enumerable.Range(1, n).Select(i => (short)i).ToArray();

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, IServicoEtapas Servico, Cenario Cenario)>
        PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"etapas-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new ServicoEtapas(db, ctx), cenario);
    }
}
