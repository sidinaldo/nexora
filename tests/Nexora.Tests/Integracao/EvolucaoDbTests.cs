using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== EVO-1 · A CONVERSÃO QUE NÃO MUDA O PASSADO =====================
///
/// O relógio destes testes está em 06/08/2026, então o mês CORRENTE é agosto e uma janela de 6
/// meses vai de março a agosto. Maio é usado em quase todo teste: fica no meio da janela, longe das
/// duas bordas.
///
/// ⚠️ OS DOIS PRIMEIROS TESTES SÃO A RAZÃO DE ESTE SERVIÇO EXISTIR. Eles afirmam que um fato
/// registrado HOJE não reescreve um mês ANTIGO — e o relatório de vendedores (`SqlDesempenho`) não
/// passaria em nenhum dos dois. Não é zelo: sem isso, a tela mostra uma linha do tempo que muda de
/// forma sozinha entre dois carregamentos, e quem olhar vai concluir que o número é inventado.
/// ============================================================================================</summary>
[Collection("banco")]
public class EvolucaoDbTests(BancoTeste banco)
{
    /// <summary>Maio de 2026, no meio da janela de 6 meses a partir de agosto.</summary>
    private static readonly DateTime Maio = new(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Abril = new(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc);

    // ==================================================================== a estabilidade

    /// <summary>===================== CONCLUIR NÃO REESCREVE O MÊS =====================
    ///
    /// ⚠️ ESTE É O TESTE QUE O RELATÓRIO ANTIGO NÃO PASSA. `LiberacaoDeCiclo` ZERA
    /// `contatos.responsavel_id` quando a última negociação ganha é concluída — e o denominador de
    /// `SqlDesempenho` conta justamente por essa coluna. O lead sai do denominador do vendedor
    /// sozinho, meses depois, inclusive pela rodada diária que ninguém disparou à mão: a conversão
    /// de maio SOBE sem nada ter acontecido em maio.
    ///
    /// Aqui as duas pontas contam por `negociacoes.responsavel_id`, que a conclusão não toca.
    /// ==============================================================</summary>
    [Fact]
    public async Task CONCLUIR_UMA_VENDA_NAO_MEXE_NA_CONVERSAO_DO_MES_DELA()
    {
        var (db, tx, amb) = await PrepararAsync("estavel");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "ana");

        // Um ganho e uma perda em maio, do mesmo vendedor: 1 de 2 = 50%.
        var ganho = await GanhoAsync(db, amb, "g1", Maio, 1000m, vendedor.Id);
        await PerdidoAsync(db, amb, "p1", Maio, vendedor.Id);

        // A conversa atribuida ao mesmo vendedor: e a linha sem a qual a liberacao nao acontece.
        await ConversaAsync(db, amb, ganho.ContatoId, vendedor.Id);

        var antes = await MesDeAsync(amb, vendedor.Id, mes: 5);
        Assert.Equal(50m, antes.ConversaoPercentual);
        Assert.Equal(2, antes.Decididos);

        // O gatilho: concluir o pedido. Ele zerava `contatos.responsavel_id`; desde o BUG-XX (decisão
        // do dono: concluir não mexe em conversa nem em carteira) não zera mais — e a conversão
        // tem de continuar a mesma dos dois jeitos.
        await ContatosDbTests.ConcluirGanhaAsync(db, amb.Vendas, ganho.ContatoId);

        var concluida = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Id == ganho.NegociacaoId).Select(n => n.Status).FirstAsync();
        Assert.Equal(StatusNegociacao.Concluida, concluida);

        var depois = await MesDeAsync(amb, vendedor.Id, mes: 5);
        Assert.Equal(50m, depois.ConversaoPercentual);
        Assert.Equal(2, depois.Decididos);
        Assert.Equal(1, depois.Ganhos);
    }

    /// <summary>===================== A PERDA É DATADA PELA PRÓPRIA PERDA =====================
    ///
    /// ⚠️ `SqlDesempenho` NÃO TEM RECORTE DE DATA NO "PERDIDOS": o `EXISTS` dele só pergunta
    /// `status = 'perdida'`. Registrar uma perda hoje muda a conversão de maio, retroativamente.
    ///
    /// Aqui a perda entra pelo mês de `perdida_em`. Uma perda de agosto não tem como alterar maio.
    /// ==============================================================</summary>
    [Fact]
    public async Task UMA_PERDA_DE_AGOSTO_NAO_MUDA_A_CONVERSAO_DE_MAIO()
    {
        var (db, tx, amb) = await PrepararAsync("datada");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "bia");

        await GanhoAsync(db, amb, "g1", Maio, 1000m, vendedor.Id);
        await PerdidoAsync(db, amb, "p1", Maio, vendedor.Id);

        Assert.Equal(50m, (await MesDeAsync(amb, vendedor.Id, 5)).ConversaoPercentual);

        // A perda de HOJE (agosto), com o relógio congelado em 06/08.
        await PerdidoAsync(db, amb, "p2", ContatosDbTests.Agora.UtcDateTime, vendedor.Id);

        var maio = await MesDeAsync(amb, vendedor.Id, 5);
        Assert.Equal(50m, maio.ConversaoPercentual);
        Assert.Equal(2, maio.Decididos);

        // E ela aparece onde deve: em agosto.
        var agosto = await MesDeAsync(amb, vendedor.Id, 8);
        Assert.Equal(1, agosto.Decididos);
        Assert.Equal(0m, agosto.ConversaoPercentual);
    }

    // ==================================================================== a atribuição

    /// <summary>⚠️ AS DUAS PONTAS PELA MESMA COLUNA. O contato é de uma pessoa e a negociação é de
    /// OUTRA — acontece de verdade quando um lead é repassado. A linha tem de ser de quem fechou o
    /// negócio, e um denominador que lesse `contatos.responsavel_id` poria o ganho numa pessoa e a
    /// perda noutra, com as duas conversões erradas e a soma certa.</summary>
    [Fact]
    public async Task A_LINHA_E_DE_QUEM_FECHOU_A_NEGOCIACAO_E_NAO_DE_QUEM_TEM_O_CONTATO()
    {
        var (db, tx, amb) = await PrepararAsync("atribui");
        using var _ = db; using var __ = tx;

        var doContato = await VendedorAsync(db, amb, "carla");
        var daNegociacao = await VendedorAsync(db, amb, "diego");

        var ganho = await GanhoAsync(db, amb, "g1", Maio, 1000m, daNegociacao.Id);

        // O contato fica com outra pessoa, como num repasse.
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == ganho.ContatoId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, doContato.Id));
        db.ChangeTracker.Clear();

        var dados = await Servico(amb).ObterAsync(6, default);

        var deDiego = dados.Pessoas.Single(p => p.UsuarioId == daNegociacao.Id);
        var deCarla = dados.Pessoas.Single(p => p.UsuarioId == doContato.Id);

        Assert.Equal(1, deDiego.Decididos);
        Assert.Equal(0, deCarla.Decididos);
    }

    /// <summary>⚠️ CANCELADA NÃO É GANHA, e a negociação cancelada MANTÉM o `ganha_em` que tinha.
    /// Um filtro por `ganha_em IS NOT NULL` contaria o cancelamento como venda — e o cancelamento
    /// deixaria de ter efeito nenhum aqui, o oposto do que ele significa.</summary>
    [Fact]
    public async Task NEGOCIACAO_CANCELADA_SAI_DOS_GANHOS()
    {
        var (db, tx, amb) = await PrepararAsync("cancela");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "eva");

        var ganho = await GanhoAsync(db, amb, "g1", Maio, 1000m, vendedor.Id);
        await PerdidoAsync(db, amb, "p1", Maio, vendedor.Id);

        Assert.Equal(50m, (await MesDeAsync(amb, vendedor.Id, 5)).ConversaoPercentual);

        // Cancela SEM apagar `ganha_em` — é exatamente o estado que o modelo do NEG-2 produz.
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.Id == ganho.NegociacaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, StatusNegociacao.Cancelada));
        db.ChangeTracker.Clear();

        var maio = await MesDeAsync(amb, vendedor.Id, 5);
        Assert.Equal(1, maio.Decididos);
        Assert.Equal(0, maio.Ganhos);
        Assert.Equal(0m, maio.ConversaoPercentual);
    }

    // ==================================================================== o tenant

    /// <summary>⚠️ SQL CRU PASSA POR FORA DO FILTRO GLOBAL DO EF. O `WHERE empresa_id = $4` é a
    /// única coisa que separa as empresas nesta consulta, e não existe teste mecânico no
    /// repositório que exija a cláusula — então ela é exigida aqui, à mão.</summary>
    [Fact]
    public async Task A_EMPRESA_VIZINHA_NAO_ENTRA_NA_CONTA()
    {
        var (db, tx, amb) = await PrepararAsync("tenant");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "fabio");
        await GanhoAsync(db, amb, "g1", Maio, 1000m, vendedor.Id);

        var vizinha = await Semeador.TenantAsync(db, "evo-vizinha");
        await ZerarAsync(db, vizinha.Id);

        // Quatro ganhos e quatro perdas na vizinha — volume que apareceria sem a cláusula.
        var outro = new ContextoMutavel
        {
            EmpresaId = vizinha.Id, UsuarioId = vizinha.Dono.Id, Papel = "dono"
        };
        var ambVizinha = amb with { Cenario = vizinha, Contexto = outro };

        for (var i = 0; i < 4; i++)
        {
            await GanhoAsync(db, ambVizinha, $"vg{i}", Maio, 500m, vizinha.Dono.Id);
            await PerdidoAsync(db, ambVizinha, $"vp{i}", Maio, vizinha.Dono.Id);
        }

        var dados = await Servico(amb).ObterAsync(6, default);

        Assert.Equal(1, dados.Equipe!.Decididos);
        Assert.DoesNotContain(dados.Pessoas, p => p.Nome.Contains("evo-vizinha"));
    }

    // ==================================================================== a permissão

    /// <summary>===================== A RÉGUA É NÚMERO DA EQUIPE =====================
    ///
    /// ⚠️ NÃO BASTA ESCONDER AS OUTRAS LINHAS. A média da equipe é, ela mesma, um número da equipe:
    /// devolvê-la a quem não tem o gesto entregaria pela porta dos fundos exatamente o que a
    /// permissão fecha pela frente — e com três vendedores o dono se deduz por subtração.
    ///
    /// O par é o teste: o dono recebe régua E as três linhas; o vendedor recebe UMA linha e
    /// `Equipe` nulo. Afirmar só o recorte das linhas passaria numa versão que vazasse a média.
    /// ==============================================================</summary>
    [Fact]
    public async Task QUEM_NAO_VE_OS_NUMEROS_DA_EQUIPE_NAO_RECEBE_REGUA_NEM_OS_COLEGAS()
    {
        var (db, tx, amb) = await PrepararAsync("recorte");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb, "ana");
        var bruno = await VendedorAsync(db, amb, "bruno");

        await GanhoAsync(db, amb, "a1", Maio, 1000m, ana.Id);
        await PerdidoAsync(db, amb, "a2", Maio, ana.Id);
        await GanhoAsync(db, amb, "b1", Maio, 1000m, bruno.Id);

        // O dono: régua e todo mundo.
        var doDono = await Servico(amb).ObterAsync(6, default);
        Assert.NotNull(doDono.Equipe);
        Assert.Equal(3, doDono.Equipe!.Decididos);
        Assert.Contains(doDono.Pessoas, p => p.UsuarioId == ana.Id);
        Assert.Contains(doDono.Pessoas, p => p.UsuarioId == bruno.Id);

        // A Ana, sem o gesto: uma linha, a dela, e nenhuma régua.
        amb.Contexto.UsuarioId = ana.Id;
        amb.Contexto.Papel = "vendedor";

        var daAna = await Servico(amb).ObterAsync(6, default);

        Assert.Null(daAna.Equipe);
        var so = Assert.Single(daAna.Pessoas);
        Assert.Equal(ana.Id, so.UsuarioId);
        Assert.Equal(2, so.Decididos);
    }

    // ==================================================================== a régua

    /// <summary>⚠️ A EQUIPE SOMA, NÃO TIRA MÉDIA DE PERCENTUAIS. A Ana decidiu 1 e ganhou 1 (100%);
    /// o Bruno decidiu 9 e ganhou 0 (0%). A média dos percentuais daria 50% — e diria que a equipe
    /// fecha metade do que pega, quando ela fechou 1 de 10. Média de percentuais dá a quem decidiu
    /// um negócio o mesmo peso de quem decidiu nove.</summary>
    [Fact]
    public async Task A_REGUA_DA_EQUIPE_E_A_CONTA_SOBRE_TODO_MUNDO_JUNTO()
    {
        var (db, tx, amb) = await PrepararAsync("regua");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb, "ana");
        var bruno = await VendedorAsync(db, amb, "bruno");

        await GanhoAsync(db, amb, "a1", Maio, 1000m, ana.Id);
        for (var i = 0; i < 9; i++)
            await PerdidoAsync(db, amb, $"b{i}", Maio, bruno.Id);

        var dados = await Servico(amb).ObterAsync(6, default);

        Assert.Equal(10, dados.Equipe!.Decididos);
        Assert.Equal(1, dados.Equipe!.Ganhos);
        Assert.Equal(10m, dados.Equipe!.ConversaoPercentual);
    }

    // ==================================================================== a janela

    /// <summary>O mês corrente vem MARCADO, e é a marca que o tira da tendência. Sem ela, agosto no
    /// dia 6 — seis dias de dados — seria comparado com meses inteiros.</summary>
    [Fact]
    public async Task O_MES_CORRENTE_VEM_MARCADO_COMO_PARCIAL_E_OS_FECHADOS_NAO()
    {
        var (db, tx, amb) = await PrepararAsync("parcial");
        using var _ = db; using var __ = tx;

        var dados = await Servico(amb).ObterAsync(6, default);
        var meses = dados.Equipe!.Meses;

        // Março a agosto, seis meses, com o relógio em 06/08/2026.
        Assert.Equal(6, meses.Count);
        Assert.Equal(new DateOnly(2026, 3, 1), dados.De);
        Assert.Equal(new DateOnly(2026, 8, 31), dados.Ate);

        Assert.True(meses[^1].Parcial);
        Assert.Equal(8, meses[^1].Mes);
        Assert.DoesNotContain(meses.Take(5), m => m.Parcial);
    }

    /// <summary>⚠️ A JANELA É LISTA FECHADA, e a recusa é do SERVIÇO. `meses` entra numa conta de
    /// calendário: um `meses=600` pedido na mão varreria a tabela inteira para desenhar um gráfico
    /// de 600 pontos num eixo de mil pixels.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(600)]
    [InlineData(0)]
    [InlineData(-6)]
    public async Task JANELA_FORA_DA_LISTA_E_RECUSADA(int meses)
    {
        var (db, tx, amb) = await PrepararAsync($"janela{meses}");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).ObterAsync(meses, default));
    }

    /// <summary>Um mês fora da janela não entra. Abril está dentro da de 6; na de 3 (junho a
    /// agosto) ele fica fora, e o corte é o `>= $1` do SQL.</summary>
    [Fact]
    public async Task O_QUE_ESTA_ANTES_DA_JANELA_FICA_FORA()
    {
        var (db, tx, amb) = await PrepararAsync("corte");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "gil");
        await GanhoAsync(db, amb, "g1", Abril, 1000m, vendedor.Id);

        Assert.Equal(1, (await Servico(amb).ObterAsync(6, default)).Equipe!.Decididos);
        Assert.Equal(0, (await Servico(amb).ObterAsync(3, default)).Equipe!.Decididos);
    }

    /// <summary>Quem não fechou nada aparece com a linha zerada e conversão NULA — e não 0%. A
    /// pessoa tem de estar na tela: ausência de linha pareceria pessoa inexistente, e "0%"
    /// pareceria pessoa que tentou e não fechou.</summary>
    [Fact]
    public async Task QUEM_NAO_FECHOU_NADA_APARECE_COM_CONVERSAO_NULA()
    {
        var (db, tx, amb) = await PrepararAsync("zerado");
        using var _ = db; using var __ = tx;

        var parado = await VendedorAsync(db, amb, "hugo");

        var linha = (await Servico(amb).ObterAsync(6, default))
            .Pessoas.Single(p => p.UsuarioId == parado.Id);

        Assert.Equal(0, linha.Decididos);
        Assert.Null(linha.ConversaoPercentual);
        Assert.Equal("sem_dados", linha.Tendencia);
        Assert.Null(linha.VariacaoPontos);
    }

    /// <summary>A negociação sem responsável vira a linha "Sem dono" — e ela só nasce quando tem o
    /// que mostrar. Uma linha de zeros em toda empresa que atribui tudo seria ruído permanente.</summary>
    [Fact]
    public async Task SEM_DONO_SO_APARECE_QUANDO_TEM_O_QUE_MOSTRAR()
    {
        var (db, tx, amb) = await PrepararAsync("semdono");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "ivo");
        await GanhoAsync(db, amb, "g1", Maio, 1000m, vendedor.Id);

        Assert.DoesNotContain(
            (await Servico(amb).ObterAsync(6, default)).Pessoas, p => p.Nome == "Sem dono");

        await GanhoAsync(db, amb, "g2", Maio, 500m, responsavelId: null);

        var semDono = (await Servico(amb).ObterAsync(6, default))
            .Pessoas.Single(p => p.Nome == "Sem dono");

        Assert.Null(semDono.UsuarioId);
        Assert.Equal(1, semDono.Decididos);
    }

    /// <summary>===================== A LINHA "SEM DONO" OBEDECE A JANELA =====================
    /// Uma perda sem responsavel em ABRIL nao faz a linha "Sem dono" nascer numa janela de 3 meses
    /// (junho a agosto) — ela nasceria vazia, com conversao nula e seis celulas em branco.
    ///
    /// ⚠️ ESTE TESTE NASCEU DE UMA SABOTAGEM QUE NAO DERRUBOU NADA. A existencia da linha era
    /// decidida varrendo as chaves do agregado, que incluem mes fora da janela — e so acertava
    /// porque o SQL recortava por data. A correcao da tela dependia de um filtro que esta ali pelo
    /// indice. Agora a pergunta e sobre a linha montada, e este teste e quem guarda.
    /// ==============================================================</summary>
    [Fact]
    public async Task SEM_DONO_FORA_DA_JANELA_NAO_CRIA_A_LINHA()
    {
        var (db, tx, amb) = await PrepararAsync("semdonofora");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "joana");
        await GanhoAsync(db, amb, "g1", Maio, 1000m, vendedor.Id);

        // Abril, sem responsavel: dentro da janela de 6, fora da de 3.
        await PerdidoAsync(db, amb, "sd", Abril, responsavelId: null);

        Assert.Contains(
            (await Servico(amb).ObterAsync(6, default)).Pessoas, p => p.Nome == "Sem dono");

        Assert.DoesNotContain(
            (await Servico(amb).ObterAsync(3, default)).Pessoas, p => p.Nome == "Sem dono");
    }

    /// <summary>Inativo e convidado só aparecem com número na janela (AUD-XX, B12). O ativo zerado
    /// continua na lista; o inativo que vendeu em maio também — o histórico de quem saiu conta.</summary>
    [Fact]
    public async Task INATIVO_E_CONVIDADO_SO_APARECEM_COM_NUMERO_NA_JANELA()
    {
        var (db, tx, amb) = await PrepararAsync("b12");
        using var _ = db; using var __ = tx;

        var ativoZerado = await VendedorAsync(db, amb, "ativo-zerado");
        var inativoZerado = await VendedorAsync(db, amb, "inativo-zerado");
        var inativoQueVendeu = await VendedorAsync(db, amb, "inativo-vendeu");
        var convidado = await VendedorAsync(db, amb, "convidado");

        await GanhoAsync(db, amb, "g-inativo", Maio, 500m, inativoQueVendeu.Id);

        await db.Usuarios.IgnoreQueryFilters()
            .Where(u => u.Id == inativoZerado.Id || u.Id == inativoQueVendeu.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, StatusUsuario.Inativo));
        await db.Usuarios.IgnoreQueryFilters().Where(u => u.Id == convidado.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, StatusUsuario.Convidado));
        db.ChangeTracker.Clear();

        var ids = (await Servico(amb).ObterAsync(6, default)).Pessoas.Select(p => p.UsuarioId).ToList();

        Assert.Contains(ativoZerado.Id, ids);
        Assert.Contains(inativoQueVendeu.Id, ids);
        Assert.DoesNotContain(inativoZerado.Id, ids);
        Assert.DoesNotContain(convidado.Id, ids);
    }

    /// <summary>O tempo de casa vem do servidor em meses de CALENDÁRIO completos (AUD-XX, #28): de
    /// 14/03 a 06/08 são 4 meses. A tela usava meses de 30,44 dias.</summary>
    [Fact]
    public async Task OS_MESES_DE_CASA_SAO_DE_CALENDARIO_E_VEM_DO_SERVIDOR()
    {
        var (db, tx, amb) = await PrepararAsync("meses-casa");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "casa");
        var entrada = new DateTime(2026, 3, 14, 15, 0, 0, DateTimeKind.Utc);
        await db.Usuarios.IgnoreQueryFilters().Where(u => u.Id == vendedor.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.CriadoEm, entrada));
        db.ChangeTracker.Clear();

        var dados = await Servico(amb).ObterAsync(6, default);

        Assert.Equal(4, dados.Pessoas.Single(p => p.UsuarioId == vendedor.Id).MesesNoNexora);
        Assert.Null(dados.Equipe!.MesesNoNexora);
    }

    // ==================================================================== o andaime

    private static IServicoEvolucao Servico(Ambiente amb) =>
        new ServicoEvolucao(amb.Db, amb.Contexto, amb.Relogio);

    /// <summary>O mês de uma pessoa, pelo número do mês. Atalho porque quase todo teste aqui
    /// pergunta exatamente isso.</summary>
    private static async Task<MesDaConversao> MesDeAsync(Ambiente amb, long? usuarioId, int mes)
    {
        var dados = await Servico(amb).ObterAsync(6, default);

        return dados.Pessoas.Single(p => p.UsuarioId == usuarioId)
            .Meses.Single(m => m.Mes == mes);
    }

    private sealed record Ambiente(
        NexoraDbContext Db, Cenario Cenario, ContextoMutavel Contexto,
        TimeProvider Relogio, IServicoVendas Vendas);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(ContatosDbTests.Agora);
        var trilha = new ColetorAuditoria();

        var db = banco.NovoContexto(ctx, relogio, trilha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"evo-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        // O Semeador deixa um contato com negociação ABERTA. Negociação aberta não é decidida e
        // não entra em nenhuma conta daqui — mas zerar mantém este arquivo lendo igual aos
        // vizinhos, e protege contra o dia em que a fixture passar a semear algo fechado.
        await ZerarAsync(db, cenario.Id);

        return (db, tx, new Ambiente(
            db, cenario, ctx, relogio, new ServicoVendas(db, ctx, trilha, relogio)));
    }

    private static async Task ZerarAsync(NexoraDbContext db, long empresaId)
    {
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Lembretes.IgnoreQueryFilters().Where(l => l.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Auditoria.IgnoreQueryFilters().Where(a => a.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Um vendedor a mais. O `Cenario` do semeador traz so o dono, e todo teste daqui
    /// precisa de alguem com linha propria.</summary>
    private static async Task<Usuario> VendedorAsync(NexoraDbContext db, Ambiente amb, string marca)
    {
        var u = new Usuario
        {
            EmpresaId = amb.Cenario.Id,
            Nome = $"Vendedor {marca}",
            Email = $"{marca}-{Guid.NewGuid():N}@exemplo.com",
            SenhaHash = Nexora.Core.Seguranca.HashSenha.Gerar("senha-de-teste-123"),
            Papel = PapelUsuario.Vendedor,
            Status = StatusUsuario.Ativo
        };
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return u;
    }

    /// <summary>Contato novo com uma negociação aberta. `criado_em` é imposto por UPDATE porque o
    /// `InterceptorAuditoria` sobrescreve a coluna em todo INSERT — o que é certo em produção e
    /// trabalha contra um teste que precisa de datas espalhadas pelo calendário.</summary>
    private static async Task<Contato> LeadAsync(
        NexoraDbContext db, Ambiente amb, string marca, DateTime criadoEm, long? responsavelId)
    {
        var contato = new Contato
        {
            EmpresaId = amb.Cenario.Id,
            Nome = $"Contato {marca}",
            Telefone = $"5584{Random.Shared.NextInt64(900000000, 999999999)}",
            ResponsavelId = responsavelId
        };
        db.Contatos.Add(contato);
        db.Negociacoes.Add(Semeador.Negocio(contato, amb.Cenario.Etapas[0]));
        await db.SaveChangesAsync();

        await db.Contatos.IgnoreQueryFilters().Where(x => x.Id == contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CriadoEm, criadoEm));

        db.ChangeTracker.Clear();
        return contato;
    }

    /// <summary>===================== O QUE A LIBERACAO DE CICLO EXIGE =====================
    /// `LiberacaoDeCiclo` zera `contatos.responsavel_id` com um UPDATE que faz JOIN em `conversas`
    /// e compara `ct.responsavel_id = c.responsavel_id`.
    ///
    /// ⚠️ CONTATO SEM CONVERSA NAO E LIBERADO — nao ha linha para o join achar. A primeira versao
    /// do teste de estabilidade nao criava conversa, o gatilho nao disparava, e o teste ficava
    /// VERDE sem exercitar nada. Foi a assercao-guarda que pegou.
    /// ==============================================================</summary>
    private static async Task ConversaAsync(
        NexoraDbContext db, Ambiente amb, long contatoId, long? responsavelId)
    {
        db.Conversas.Add(new Conversa
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = contatoId,
            ConexaoId = amb.Cenario.Conexao.Id,
            ResponsavelId = responsavelId,
            AtribuidoEm = ContatosDbTests.Agora.UtcDateTime,
            UltimaMensagemEm = ContatosDbTests.Agora.UtcDateTime
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Uma negociação GANHA, com `ganha_em` e responsável fixados por UPDATE — o serviço
    /// de ganho usa o relógio, que está congelado num instante só.</summary>
    private static async Task<(long ContatoId, long NegociacaoId)> GanhoAsync(
        NexoraDbContext db, Ambiente amb, string marca, DateTime ganhaEm, decimal valor,
        long? responsavelId)
    {
        var contato = await LeadAsync(db, amb, marca, ganhaEm, responsavelId);
        // ⚠️ `IgnoreQueryFilters` COM `empresa_id` A MAO. O filtro global do EF usa o contexto com
        // que o `db` foi criado — a PRIMEIRA empresa —, e no teste de tenant esta fixture semeia a
        // vizinha. Sem isto a etapa vem da empresa errada e a FK composta recusa o INSERT.
        var etapaGanhoId = await db.EtapasFunil.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.EGanho && e.EmpresaId == amb.Cenario.Id)
            .Select(e => e.Id).FirstAsync();

        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contato.Id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(n => n.Status, StatusNegociacao.Ganha)
                .SetProperty(n => n.GanhaEm, ganhaEm)
                .SetProperty(n => n.Valor, valor)
                .SetProperty(n => n.EtapaId, etapaGanhoId)
                .SetProperty(n => n.ResponsavelId, responsavelId));

        var id = await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.ContatoId == contato.Id).Select(n => n.Id).FirstAsync();

        db.ChangeTracker.Clear();
        return (contato.Id, id);
    }

    private static async Task PerdidoAsync(
        NexoraDbContext db, Ambiente amb, string marca, DateTime perdidaEm, long? responsavelId)
    {
        var contato = await LeadAsync(db, amb, marca, perdidaEm, responsavelId);

        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contato.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, StatusNegociacao.Perdida)
                .SetProperty(n => n.PerdidaEm, perdidaEm)
                .SetProperty(n => n.MotivoPerda, "preco")
                .SetProperty(n => n.ResponsavelId, responsavelId));

        db.ChangeTracker.Clear();
    }
}
