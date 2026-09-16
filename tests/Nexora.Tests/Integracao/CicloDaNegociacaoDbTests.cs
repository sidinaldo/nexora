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

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "achou caro", null, default);
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

    // ==================================================================== um aberto POR FUNIL
    /// <summary>⚠️ A MESMA PESSOA PODE ESTAR EM VARIOS FUNIS AO MESMO TEMPO, e nao podia.
    ///
    /// Relatado assim: "por que Ysia esta em 2 negociacao e nao posso incluir ela em mais outro
    /// pipeline?".
    ///
    /// A guarda era `Any(Status == Aberta)` sobre o contato INTEIRO — a regra de quando o contato
    /// ERA o card. O E4 existe justamente para ela poder estar em Vendas, em Pos-venda e num
    /// terceiro funil, e a tela do contato ja lista esses negocios um por linha.</summary>
    [Fact]
    public async Task ABRIR_NOUTRO_FUNIL_E_PERMITIDO_MESMO_COM_UM_ABERTO()
    {
        var (db, tx, amb) = await PrepararAsync("varios-funis");
        using var _1 = db; using var _2 = tx;

        // O contato do cenario ja tem UM aberto, em Vendas.
        var outra = new Pipeline { EmpresaId = amb.Cenario.Id, Nome = "Pós-venda", Ordem = 2 };
        db.Pipelines.Add(outra);
        await db.SaveChangesAsync();

        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = amb.Cenario.Id, PipelineId = outra.Id, Nome = "Recebido", Ordem = 1
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, outra.Id, default);
        db.ChangeTracker.Clear();

        var abertos = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == amb.Cenario.Contato.Id
                     && n.Status == StatusNegociacao.Aberta)
            .Select(n => n.PipelineId).ToListAsync();

        Assert.Equal(2, abertos.Count);
        Assert.Contains(amb.Cenario.Pipeline.Id, abertos);
        Assert.Contains(outra.Id, abertos);
    }

    /// <summary>O que continua proibido e o que de fato confunde: DOIS cards da mesma pessoa NO
    /// MESMO funil. Ali ninguem sabe qual e qual, e mover um deixa o outro para tras.
    ///
    /// ⚠️ A MENSAGEM NOMEIA O FUNIL. Com varios, "ja esta em aberto" parece arbitrario e a pessoa
    /// tenta de novo no mesmo lugar.</summary>
    [Fact]
    public async Task ABRIR_DUAS_VEZES_NO_MESMO_FUNIL_E_RECUSADO_DIZENDO_QUAL()
    {
        var (db, tx, amb) = await PrepararAsync("mesmo-funil-duas");
        using var _1 = db; using var _2 = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.AbrirNegociacaoAsync(
                amb.Cenario.Contato.Id, amb.Cenario.Pipeline.Id, default));

        Assert.True(erro.Conflito);
        Assert.Contains(amb.Cenario.Pipeline.Nome, erro.Message);
    }

    /// <summary>===================== UMA ABERTA POR FUNIL, NOS TRES CAMINHOS =====================
    /// Pedido assim: "o que nao pode e o mesmo contato ter dois cards com status diferente de
    /// close no mesmo funil. Essa regra precisa estar no banco de dados, backend e frontend".
    ///
    /// ⚠️ O ALCANCE SAO OS DOIS ESTADOS DO QUADRO, `aberta` E `ganha`. A primeira versao
    /// cobria so `aberta`, com o argumento de que ganha nao e negociacao e sim PEDIDO a caminho;
    /// na tela isso pos a mesma pessoa em duas etapas do mesmo funil, uma em "Venda" e outra em
    /// "Separado", e a pergunta que sobra e "afinal, onde ela esta?".
    ///
    /// ⚠️ O BANCO E QUEM GARANTE (`uq_negociacoes_card_por_funil`). Sao TRES caminhos que
    /// podem criar o segundo card — abrir, arrastar de outro funil, cancelar uma venda — e "os
    /// tres lembram de checar" e uma promessa que se quebra no quarto. Os servicos checam antes
    /// para a recusa ser uma MENSAGEM em vez de um 500.
    ///
    /// ⚠️ OS DOIS ULTIMOS PASSOS USAM SAVEPOINT, e nao e preciosismo: no Postgres um
    /// `SaveChanges` que estoura ABORTA a transacao inteira, e o passo seguinte morreria com
    /// "current transaction is aborted" em vez de com a violacao que se quer ver.
    /// ==================================================================================</summary>
    [Fact]
    public async Task DOIS_CARDS_NO_MESMO_FUNIL_SAO_RECUSADOS_NOS_TRES_CAMINHOS()
    {
        var (db, tx, amb) = await PrepararAsync("uma-aberta");
        using var _1 = db; using var _2 = tx;

        // O contato do cenario ja tem UMA aberta em Vendas.
        var outra = new Pipeline { EmpresaId = amb.Cenario.Id, Nome = "Pós-venda", Ordem = 2 };
        db.Pipelines.Add(outra);
        await db.SaveChangesAsync();

        var la = new EtapaFunil
        {
            EmpresaId = amb.Cenario.Id, PipelineId = outra.Id, Nome = "Recebido", Ordem = 1
        };
        db.EtapasFunil.Add(la);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // ---------- 1. ABRIR no mesmo funil
        var abrir = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.AbrirNegociacaoAsync(
                amb.Cenario.Contato.Id, amb.Cenario.Pipeline.Id, default));

        Assert.True(abrir.Conflito);
        Assert.Contains(amb.Cenario.Pipeline.Nome, abrir.Message);

        // ---------- 2. ARRASTAR de outro funil para ca
        await amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, outra.Id, default);
        db.ChangeTracker.Clear();

        var noOutro = await db.Negociacoes.AsNoTracking()
            .SingleAsync(n => n.ContatoId == amb.Cenario.Contato.Id && n.PipelineId == outra.Id);

        var mover = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Funil.MoverAsync(
                noOutro.Id, new MoverContato(amb.Cenario.Etapas[1].Id, null), default));

        Assert.True(mover.Conflito);
        Assert.Contains(amb.Cenario.Pipeline.Nome, mover.Message);

        // ---------- 3. E O BANCO, que e quem garante quando alguem esquecer os dois de cima
        await tx.CreateSavepointAsync("antes_do_segundo_card", default);

        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = amb.Cenario.Id, ContatoId = amb.Cenario.Contato.Id,
            PipelineId = amb.Cenario.Pipeline.Id, EtapaId = amb.Cenario.PrimeiraEtapa.Id,
            OrdemKanban = 9000m, Status = StatusNegociacao.Aberta
        });

        var doBanco = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("uq_negociacoes_card_por_funil", doBanco.InnerException!.Message);

        await tx.RollbackToSavepointAsync("antes_do_segundo_card", default);
        db.ChangeTracker.Clear();

        // ---------- 4. E A GANHA TAMBEM OCUPA, que e a metade que a primeira versao deixou
        // passar. Se o filtro do indice voltar a valer so para `aberta`, ESTE passo e o unico que
        // percebe: a linha entra sem reclamar e a pessoa aparece em duas etapas do mesmo funil.
        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = amb.Cenario.Id, ContatoId = amb.Cenario.Contato.Id,
            PipelineId = amb.Cenario.Pipeline.Id,
            EtapaId = amb.Cenario.Etapas.Single(e => e.EGanho).Id,
            OrdemKanban = 9001m, Valor = 700m, Status = StatusNegociacao.Ganha,
            GanhaEm = DateTime.UtcNow
        });

        var comGanha = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("uq_negociacoes_card_por_funil", comGanha.InnerException!.Message);

        await tx.RollbackToSavepointAsync("antes_do_segundo_card", default);
        db.ChangeTracker.Clear();
    }

    /// <summary>⚠️ A GANHA OCUPA O LUGAR ATE SER CONCLUIDA, e este teste esta aqui invertido:
    /// ele afirmava o contrario ("a ganha convive"), e foi essa versao que produziu o relato
    /// "Ysia ficou duas vezes no mesmo funil".
    ///
    /// A recusa so e aceitavel porque a MENSAGEM ensina a saida, e a saida nao custa dinheiro:
    /// concluir o pedido e um clique, e `concluida` continua somando no faturamento. Por isso o
    /// teste nao para na recusa — ele segue ate a abertura dar certo.</summary>
    [Fact]
    public async Task UMA_GANHA_OCUPA_O_FUNIL_ATE_SER_CONCLUIDA()
    {
        var (db, tx, amb) = await PrepararAsync("ganha-ocupa");
        using var _1 = db; using var _2 = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 500m, null, null, default);
        db.ChangeTracker.Clear();

        // ---------- com o pedido a caminho, o funil esta ocupado
        var recusa = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.AbrirNegociacaoAsync(
                amb.Cenario.Contato.Id, amb.Cenario.Pipeline.Id, default));

        Assert.True(recusa.Conflito);
        Assert.Contains(amb.Cenario.Pipeline.Nome, recusa.Message);
        // A mensagem da ganha e diferente da mensagem da aberta: ela DIZ O QUE FAZER.
        Assert.Contains("Conclua o pedido", recusa.Message);

        // ---------- concluir o pedido libera o lugar
        await ContatosDbTests.ConcluirGanhaAsync(db, amb.Vendas, amb.Cenario.Contato.Id);

        await amb.Contatos.AbrirNegociacaoAsync(
            amb.Cenario.Contato.Id, amb.Cenario.Pipeline.Id, default);
        db.ChangeTracker.Clear();

        var estados = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == amb.Cenario.Contato.Id
                     && n.PipelineId == amb.Cenario.Pipeline.Id)
            .Select(n => n.Status).OrderBy(x => x).ToListAsync();

        Assert.Equal([StatusNegociacao.Aberta, StatusNegociacao.Concluida], estados);

        // E o dinheiro nao se mexeu: concluir nao e cancelar.
        Assert.Equal(500m, await db.Negociacoes.AsNoTracking()
            .Where(n => n.Status == StatusNegociacao.Ganha || n.Status == StatusNegociacao.Concluida)
            .SumAsync(n => n.Valor ?? 0m));
    }

    /// <summary>⚠️ "ESCOLHA POR MIM" PULA OS FUNIS OCUPADOS, e nao pular era um beco sem saida.
    ///
    /// Relatado assim: "tenho 3 funis e Ysia esta em 2, mas quando tento incluir ela no terceiro
    /// pela tela de contato nao permite".
    ///
    /// A tela so mostra o seletor de funil quando ha MAIS DE UM livre; com um so, ela manda
    /// `null` — "escolha por mim". A escolha era seca: o funil da ultima compra. Como ele ja
    /// estava ocupado, a resposta era 409 dizendo o nome de um funil que ninguem tinha pedido, e
    /// o unico livre ficava inalcancavel pela tela.
    ///
    /// Este teste percorre o caminho inteiro: o funil lembrado quando ele CABE, o desvio para o
    /// livre quando nao cabe, e a recusa honesta quando nao sobra nenhum.</summary>
    [Fact]
    public async Task ABRIR_SEM_ESCOLHER_PULA_OS_FUNIS_OCUPADOS()
    {
        var (db, tx, amb) = await PrepararAsync("pula-ocupado");
        using var _1 = db; using var _2 = tx;

        var c = amb.Cenario;
        var posVenda = await OutroFunilAsync(db, c, "Pós-venda", 2);
        var teste = await OutroFunilAsync(db, c, "Teste", 3);

        // ---------- a compra acontece no funil do cenario, e o pedido e concluido
        await amb.Contatos.MarcarGanhoAsync(c.Contato.Id, 300m, null, null, default);
        await ContatosDbTests.ConcluirGanhaAsync(db, amb.Vendas, c.Contato.Id);

        // ---------- 1. o funil da ULTIMA COMPRA, porque ele esta livre
        await amb.Contatos.AbrirNegociacaoAsync(c.Contato.Id, null, default);
        db.ChangeTracker.Clear();

        Assert.Equal(c.Pipeline.Id, await FunilDaAbertaAsync(db, c.Contato.Id, c.Pipeline.Id));

        // ---------- 2. o segundo funil, este escolhido a dedo
        await amb.Contatos.AbrirNegociacaoAsync(c.Contato.Id, posVenda.Id, default);
        db.ChangeTracker.Clear();

        // ---------- 3. E AQUI ESTAVA O DEFEITO. Sem escolher: o lembrado (Vendas) esta ocupado,
        // o Pos-venda tambem, e sobra o Teste. Antes isto devolvia 409 apontando "Vendas".
        await amb.Contatos.AbrirNegociacaoAsync(c.Contato.Id, null, default);
        db.ChangeTracker.Clear();

        Assert.Equal(teste.Id, await FunilDaAbertaAsync(db, c.Contato.Id, teste.Id));

        var funis = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == c.Contato.Id && n.Status == StatusNegociacao.Aberta)
            .Select(n => n.PipelineId).OrderBy(x => x).ToListAsync();

        Assert.Equal([c.Pipeline.Id, posVenda.Id, teste.Id], funis.Order().ToList());

        // ---------- 4. e sem funil livre a recusa DIZ ISSO, em vez de apontar um funil ao acaso
        var semSaida = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.AbrirNegociacaoAsync(c.Contato.Id, null, default));

        Assert.True(semSaida.Conflito);
        Assert.Contains("todos os funis", semSaida.Message);
    }

    /// <summary>⚠️ A PERDA SO E REVIVIDA NO PROPRIO FUNIL.
    ///
    /// Reviver e desfazer: a mesma linha volta ao quadro na etapa onde morreu, e essa etapa e a
    /// unica informacao que o gesto existe para preservar. Com o funil dela ocupado, arrastar a
    /// perda para outro lugar apagaria justamente isso — entao ela fica onde esta, como
    /// historico, e o gesto vira uma linha NOVA no funil livre.</summary>
    [Fact]
    public async Task COM_O_FUNIL_DA_PERDA_OCUPADO_ELA_FICA_ONDE_ESTA()
    {
        var (db, tx, amb) = await PrepararAsync("perda-ocupada");
        using var _1 = db; using var _2 = tx;

        var c = amb.Cenario;
        var outro = await OutroFunilAsync(db, c, "Atacado", 2);

        // Perde no funil do cenario, na SEGUNDA etapa — para a etapa da morte ser distinguivel.
        await amb.Funil.MoverAsync(
            (await db.Negociacoes.AsNoTracking().SingleAsync(n => n.ContatoId == c.Contato.Id)).Id,
            new MoverContato(c.Etapas[1].Id, null), default);
        db.ChangeTracker.Clear();

        await amb.Contatos.MarcarPerdidoAsync(c.Contato.Id, "sumiu", null, default);
        db.ChangeTracker.Clear();

        // Alguem abre outro negocio NO FUNIL DA PERDA. Agora ele esta ocupado.
        await amb.Contatos.AbrirNegociacaoAsync(c.Contato.Id, c.Pipeline.Id, default);
        db.ChangeTracker.Clear();

        // E o gesto seguinte, sem escolher, vai para o funil livre — sem tocar na perda.
        await amb.Contatos.AbrirNegociacaoAsync(c.Contato.Id, null, default);
        db.ChangeTracker.Clear();

        var perdida = await db.Negociacoes.AsNoTracking()
            .SingleAsync(n => n.ContatoId == c.Contato.Id && n.Status == StatusNegociacao.Perdida);

        Assert.Equal(c.Etapas[1].Id, perdida.EtapaId);      // a etapa onde ela morreu
        Assert.NotNull(perdida.PerdidaEm);
        Assert.Equal("sumiu", perdida.MotivoPerda);

        Assert.Equal(outro.Id, await FunilDaAbertaAsync(db, c.Contato.Id, outro.Id));
    }

    /// <summary>O funil de uma das abertas do contato — falha o teste se nao houver exatamente
    /// uma ali, que e a propria regra do card por funil.</summary>
    private static async Task<long> FunilDaAbertaAsync(NexoraDbContext db, long contatoId, long funil) =>
        (await db.Negociacoes.AsNoTracking().SingleAsync(
            n => n.ContatoId == contatoId
              && n.PipelineId == funil
              && n.Status == StatusNegociacao.Aberta)).PipelineId;

    /// <summary>Um funil a mais, com uma etapa — o minimo para `AbrirNegociacaoAsync` ter onde
    /// pousar. Dois `SaveChanges`: a etapa cita `PipelineId`, e salvar junto mandaria zero.</summary>
    private static async Task<Pipeline> OutroFunilAsync(
        NexoraDbContext db, Cenario c, string nome, short ordem)
    {
        var funil = new Pipeline { EmpresaId = c.Id, Nome = nome, Ordem = ordem };
        db.Pipelines.Add(funil);
        await db.SaveChangesAsync();

        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = funil.Id, Nome = "Entrada", Ordem = 1
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return funil;
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
        await amb.Contatos.MarcarGanhoAsync(c.Id, 900m, null, null, default);
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

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 900m, null, null, default);
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

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 120m, null, null, default);
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

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "achou caro", null, default);
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

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "sumiu", null, default);
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
    /// LINHA NOVA. Rebaixar faria o faturamento de um mês já fechado mudar sozinho.
    ///
    /// ⚠️ A ANTERIOR APARECE COMO `concluida`, e não como `ganha`: um card por pessoa por funil,
    /// então concluir o pedido é o passo que libera o lugar para a rodada nova. O que o teste
    /// afirma não mudou — a linha velha continua inteira, com o valor dela, e a soma do
    /// faturamento não se mexe.</summary>
    [Fact]
    public async Task REABRIR_UM_GANHO_PRESERVA_A_GANHA_E_ABRE_OUTRA()
    {
        var (db, tx, amb) = await PrepararAsync("reabrir-ganho");
        using var _1 = db; using var _2 = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 500m, null, null, default);
        // A regra e um card por funil: concluir o pedido libera o lugar.
        await ContatosDbTests.ConcluirGanhaAsync(db, amb.Vendas, amb.Cenario.Contato.Id);

        await amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, null, default);
        db.ChangeTracker.Clear();

        var todas = await db.Negociacoes.OrderBy(n => n.Id).ToListAsync();
        Assert.Equal(2, todas.Count);

        // ⚠️ `Concluida`, e NAO `Aberta`: o ponto e que ela nao foi rebaixada nem apagada.
        Assert.Equal(StatusNegociacao.Concluida, todas[0].Status);
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

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 300m, null, null, default);
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
        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 400m, null, null, default);
        db.ChangeTracker.Clear();
        var antiga = await db.Negociacoes.SingleAsync();
        await amb.Vendas.ConcluirAsync([antiga.Id], default);
        db.ChangeTracker.Clear();

        // O cliente volta e compra de novo, pelo MESMO valor e no MESMO instante do relógio falso.
        await amb.Contatos.AbrirNegociacaoAsync(amb.Cenario.Contato.Id, null, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 400m, null, null, default);
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
