using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== LPA-1 · QUEM PAROU DE SER TRABALHADO =====================
///
/// O relógio destes testes está em 06/08/2026. "Velho" aqui é 01/05/2026 (97 dias) e "recente" é
/// 05/08/2026 (ontem) — os dois longe da borda de 30 dias, para o teste não medir a aritmética da
/// data em vez da regra.
///
/// ⚠️ O TESTE QUE MAIS IMPORTA É O DO CONTATO SEM CONVERSA. Ele é o lead frio mais comum — entrou
/// por formulário ou importação e ninguém chamou —, e é o único que a forma óbvia da consulta
/// (`cv.ultima_mensagem_em` sozinho, num LEFT JOIN) deixaria de fora para sempre.
/// ============================================================================================</summary>
[Collection("banco")]
public class LeadsParadosDbTests(BancoTeste banco)
{
    /// <summary>97 dias antes do relógio: parado em qualquer janela.</summary>
    private static readonly DateTime Velho = new(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Ontem: não está parado em janela nenhuma.</summary>
    private static readonly DateTime Recente = new(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc);

    private static FiltroLeadsParados Filtro(int dias = 30, long? responsavel = null) =>
        new(dias, responsavel, 1, 50);

    // ==================================================================== a regra de "parado"

    [Fact]
    public async Task CONVERSA_VELHA_ENTRA_E_CONVERSA_DE_ONTEM_FICA_FORA()
    {
        var (db, tx, amb) = await PrepararAsync("datas");
        using var _ = db; using var __ = tx;

        var frio = await LeadAsync(db, amb, "frio", comConversaEm: Velho);
        await LeadAsync(db, amb, "quente", comConversaEm: Recente);

        var pagina = await Servico(amb).ListarAsync(Filtro(), default);

        var linha = Assert.Single(pagina.Itens);
        Assert.Equal(frio, linha.ContatoId);
        Assert.Equal(1, pagina.Total);
    }

    /// <summary>===================== O CONTATO QUE NUNCA CONVERSOU =====================
    ///
    /// ⚠️ ESTE É O TESTE QUE JUSTIFICA O `UNION`. A consulta óbvia seria um LEFT JOIN com
    /// `COALESCE(cv.ultima_mensagem_em, c.criado_em)`, e ela acerta este caso — mas foi MEDIDA
    /// contra o banco e descarta o índice, deixando a data como `Filter` depois de juntar todo
    /// contato com toda conversa da empresa.
    ///
    /// Em dois ramos o índice volta, e este teste é o que impede o segundo ramo de ser apagado por
    /// quem achar que o primeiro basta.
    /// ==============================================================</summary>
    [Fact]
    public async Task CONTATO_QUE_NUNCA_CONVERSOU_ENTRA_PELA_DATA_DE_CRIACAO()
    {
        var (db, tx, amb) = await PrepararAsync("semconversa");
        using var _ = db; using var __ = tx;

        var nunca = await LeadAsync(
            db, amb, "nunca", criadoEm: Velho, comConversaEm: null, comNegocio: false);
        await LeadAsync(
            db, amb, "novo", criadoEm: Recente, comConversaEm: null, comNegocio: false);

        var pagina = await Servico(amb).ListarAsync(Filtro(), default);

        var linha = Assert.Single(pagina.Itens);
        Assert.Equal(nunca, linha.ContatoId);

        // Sem negócio aberto, a tela mostra travessão — e o dado tem de dizer isso com nulo, nunca
        // com texto vazio, que a tela imprimiria como espaço em branco.
        Assert.Null(linha.NegociacaoId);
        Assert.Null(linha.PipelineNome);
        Assert.Null(linha.EtapaNome);
    }

    /// <summary>A janela é lista fechada, e o corte respeita o dia.</summary>
    [Theory]
    [InlineData(15, 1)]
    [InlineData(90, 1)]
    public async Task A_JANELA_MUDA_O_CORTE(int dias, int esperado)
    {
        var (db, tx, amb) = await PrepararAsync($"janela{dias}");
        using var _ = db; using var __ = tx;

        await LeadAsync(db, amb, "frio", comConversaEm: Velho);

        Assert.Equal(esperado, (await Servico(amb).ListarAsync(Filtro(dias), default)).Total);
    }

    [Fact]
    public async Task JANELA_FORA_DA_LISTA_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("janelaruim");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => Servico(amb).ListarAsync(Filtro(45), default));
    }

    // ==================================================================== quem sai

    /// <summary>⚠️ QUEM COMPROU NÃO É LEAD FRIO. O contato com negociação ganha ou concluída e nada
    /// aberto é cliente — pô-lo nesta lista mandaria o dono "recuperar" quem já comprou.</summary>
    [Theory]
    [InlineData(StatusNegociacao.Ganha)]
    [InlineData(StatusNegociacao.Concluida)]
    [InlineData(StatusNegociacao.Perdida)]
    public async Task NEGOCIO_FECHADO_TIRA_O_CONTATO_DA_LISTA(StatusNegociacao status)
    {
        var (db, tx, amb) = await PrepararAsync($"fechado{status}");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "fechado", comConversaEm: Velho);
        await MudarStatusAsync(db, id, status);

        Assert.Empty((await Servico(amb).ListarAsync(Filtro(), default)).Itens);
    }

    /// <summary>O par do teste acima. Cancelar desfaz o negócio: o contato volta a ser lead, e
    /// voltar para a lista é o comportamento certo.</summary>
    [Fact]
    public async Task NEGOCIO_CANCELADO_DEVOLVE_O_CONTATO_A_LISTA()
    {
        var (db, tx, amb) = await PrepararAsync("cancelado");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "cancelado", comConversaEm: Velho);
        await MudarStatusAsync(db, id, StatusNegociacao.Cancelada);

        var linha = Assert.Single((await Servico(amb).ListarAsync(Filtro(), default)).Itens);
        Assert.Equal(id, linha.ContatoId);
        Assert.Null(linha.NegociacaoId);
    }

    /// <summary>⚠️ O CLIENTE RECORRENTE COM PÓS-VENDA PARADO APARECE, e tem de aparecer: aquele
    /// negócio está parado de verdade. A regra não é "o contato nunca comprou" — é "há um negócio
    /// aberto sem avanço".</summary>
    [Fact]
    public async Task QUEM_JA_COMPROU_MAS_TEM_OUTRO_NEGOCIO_ABERTO_APARECE()
    {
        var (db, tx, amb) = await PrepararAsync("recorrente");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "recorrente", comConversaEm: Velho);
        await MudarStatusAsync(db, id, StatusNegociacao.Concluida);
        await NegocioAbertoAsync(db, amb, id);

        var linha = Assert.Single((await Servico(amb).ListarAsync(Filtro(), default)).Itens);
        Assert.Equal(id, linha.ContatoId);
        Assert.NotNull(linha.NegociacaoId);
    }

    [Fact]
    public async Task CONTATO_ANONIMIZADO_FICA_FORA()
    {
        var (db, tx, amb) = await PrepararAsync("lgpd");
        using var _ = db; using var __ = tx;

        var id = await LeadAsync(db, amb, "apagado", comConversaEm: Velho);
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AnonimizadoEm, Velho));
        db.ChangeTracker.Clear();

        Assert.Empty((await Servico(amb).ListarAsync(Filtro(), default)).Itens);
    }

    // ==================================================================== o recorte e o tenant

    /// <summary>⚠️ O RECORTE É POR `negociacoes.responsavel_id`, nunca pelo do contato. A
    /// `LiberacaoDeCiclo` zera a coluna do contato ao concluir a venda — foi esse detalhe que
    /// tornou instável a conversão do relatório antigo.</summary>
    [Fact]
    public async Task QUEM_NAO_VE_OS_NUMEROS_DA_EQUIPE_RECEBE_SO_OS_SEUS()
    {
        var (db, tx, amb) = await PrepararAsync("recorte");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb, "ana");
        var bruno = await VendedorAsync(db, amb, "bruno");

        await LeadAsync(db, amb, "daana", comConversaEm: Velho, responsavelId: ana.Id);
        await LeadAsync(db, amb, "dobruno", comConversaEm: Velho, responsavelId: bruno.Id);

        Assert.Equal(2, (await Servico(amb).ListarAsync(Filtro(), default)).Total);

        amb.Contexto.UsuarioId = ana.Id;
        amb.Contexto.Papel = "vendedor";

        var daAna = await Servico(amb).ListarAsync(Filtro(), default);
        var so = Assert.Single(daAna.Itens);
        Assert.Equal(ana.Id, so.ResponsavelId);
    }

    /// <summary>===================== A ATRIBUICAO E DA NEGOCIACAO =====================
    ///
    /// ⚠️ ESTE TESTE NASCEU DE UMA SABOTAGEM QUE NAO DERRUBOU NADA. Troquei
    /// `n.responsavel_id` por `c.responsavel_id` na consulta e a suite inteira ficou verde — a
    /// fixture gravava o mesmo responsavel nas DUAS colunas, entao nenhum teste conseguia
    /// distingui-las.
    ///
    /// Aqui as duas DIVERGEM de proposito, reproduzindo o que a `LiberacaoDeCiclo` faz de
    /// verdade: ela ZERA `contatos.responsavel_id` quando a venda e concluida, e nao toca no da
    /// negociacao. Contar pelo contato faria o lead sumir da lista do vendedor que o trabalha.
    /// ==============================================================</summary>
    [Fact]
    public async Task A_ATRIBUICAO_SEGUE_A_NEGOCIACAO_E_NAO_O_CONTATO()
    {
        var (db, tx, amb) = await PrepararAsync("atribui");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb, "ana");

        var id = await LeadAsync(db, amb, "repassado", comConversaEm: Velho, responsavelId: ana.Id);

        // O que a LiberacaoDeCiclo faz: o contato perde o dono, a negociacao mantem o dela.
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, (long?)null));
        db.ChangeTracker.Clear();

        var daAna = await Servico(amb).ListarAsync(Filtro(responsavel: ana.Id), default);

        var linha = Assert.Single(daAna.Itens);
        Assert.Equal(id, linha.ContatoId);
        Assert.Equal(ana.Id, linha.ResponsavelId);
    }

    /// <summary>⚠️ SQL cru passa por fora do filtro global do EF. O `empresa_id = $2` é a única
    /// coisa que separa as empresas aqui, e não há teste mecânico que o exija — então é exigido
    /// à mão.</summary>
    [Fact]
    public async Task A_EMPRESA_VIZINHA_NAO_ENTRA()
    {
        var (db, tx, amb) = await PrepararAsync("tenant");
        using var _ = db; using var __ = tx;

        await LeadAsync(db, amb, "meu", comConversaEm: Velho);

        var vizinha = await Semeador.TenantAsync(db, "lpa-vizinha");
        await ZerarAsync(db, vizinha.Id);
        var outro = new ContextoMutavel
        {
            EmpresaId = vizinha.Id, UsuarioId = vizinha.Dono.Id, Papel = "dono"
        };
        var ambVizinha = amb with { Cenario = vizinha, Contexto = outro };

        for (var i = 0; i < 5; i++)
            await LeadAsync(db, ambVizinha, $"viz{i}", comConversaEm: Velho);

        Assert.Equal(1, (await Servico(amb).ListarAsync(Filtro(), default)).Total);
    }

    // ==================================================================== a paginação

    /// <summary>O total vem da MESMA consulta, por `COUNT(*) OVER ()`. Uma segunda consulta de
    /// contagem pode discordar da primeira quando alguém escreve no meio das duas.</summary>
    [Fact]
    public async Task O_TOTAL_CONTA_TUDO_E_A_PAGINA_TRAZ_SO_A_FATIA()
    {
        var (db, tx, amb) = await PrepararAsync("pagina");
        using var _ = db; using var __ = tx;

        for (var i = 0; i < 7; i++)
            await LeadAsync(db, amb, $"p{i}", comConversaEm: Velho.AddHours(i));

        var primeira = await Servico(amb).ListarAsync(new FiltroLeadsParados(30, null, 1, 3), default);

        Assert.Equal(7, primeira.Total);
        Assert.Equal(3, primeira.Itens.Count);

        var terceira = await Servico(amb).ListarAsync(new FiltroLeadsParados(30, null, 3, 3), default);

        Assert.Equal(7, terceira.Total);
        Assert.Single(terceira.Itens);
    }

    /// <summary>O teto é do SERVIÇO, não do campo da tela: quem manda pela API também passa por
    /// ele. Pedir 500 devolve 50.</summary>
    [Fact]
    public async Task O_TAMANHO_DE_PAGINA_TEM_TETO()
    {
        var (db, tx, amb) = await PrepararAsync("teto");
        using var _ = db; using var __ = tx;

        for (var i = 0; i < 3; i++)
            await LeadAsync(db, amb, $"t{i}", comConversaEm: Velho.AddHours(i));

        var pagina = await Servico(amb).ListarAsync(new FiltroLeadsParados(30, null, 1, 500), default);

        Assert.Equal(3, pagina.Itens.Count);
        Assert.Equal(JanelasDeParada.TamanhoMaximoPagina, 50);
    }

    [Fact]
    public async Task OS_MAIS_PARADOS_VEM_PRIMEIRO()
    {
        var (db, tx, amb) = await PrepararAsync("ordem");
        using var _ = db; using var __ = tx;

        await LeadAsync(db, amb, "meio", comConversaEm: Velho.AddDays(10));
        var maisVelho = await LeadAsync(db, amb, "velho", comConversaEm: Velho);

        var pagina = await Servico(amb).ListarAsync(Filtro(), default);

        Assert.Equal(maisVelho, pagina.Itens[0].ContatoId);
        Assert.True(pagina.Itens[0].DiasParado > pagina.Itens[1].DiasParado);
    }

    // ==================================================================== o andaime

    private static IServicoLeadsParados Servico(Ambiente amb) =>
        new ServicoLeadsParados(amb.Db, amb.Contexto, amb.Relogio);

    private sealed record Ambiente(
        NexoraDbContext Db, Cenario Cenario, ContextoMutavel Contexto, TimeProvider Relogio);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(ContatosDbTests.Agora);
        var trilha = new ColetorAuditoria();

        var db = banco.NovoContexto(ctx, relogio, trilha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"lpa-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        await ZerarAsync(db, cenario.Id);

        return (db, tx, new Ambiente(db, cenario, ctx, relogio));
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

    /// <summary>Contato com negociação aberta e, opcionalmente, uma conversa.
    ///
    /// `criado_em` e `ultima_mensagem_em` são impostos por UPDATE: o `InterceptorAuditoria`
    /// sobrescreve `CriadoEm` em todo INSERT, o que é certo em produção e trabalha contra um teste
    /// que precisa de datas espalhadas pelo calendário.</summary>
    private static async Task<long> LeadAsync(
        NexoraDbContext db, Ambiente amb, string marca,
        DateTime? comConversaEm, DateTime? criadoEm = null, long? responsavelId = null,
        bool comNegocio = true)
    {
        var contato = new Contato
        {
            EmpresaId = amb.Cenario.Id,
            Nome = $"Contato {marca}",
            Telefone = $"5584{Random.Shared.NextInt64(900000000, 999999999)}",
            ResponsavelId = responsavelId
        };
        db.Contatos.Add(contato);

        if (comNegocio)
        {
            var negocio = Semeador.Negocio(contato, amb.Cenario.Etapas[0]);
            negocio.ResponsavelId = responsavelId;
            db.Negociacoes.Add(negocio);
        }

        if (comConversaEm is { } quando)
        {
            db.Conversas.Add(new Conversa
            {
                EmpresaId = amb.Cenario.Id,
                Contato = contato,
                ConexaoId = amb.Cenario.Conexao.Id,
                UltimaMensagemEm = quando
            });
        }

        await db.SaveChangesAsync();

        var nascimento = criadoEm ?? comConversaEm ?? ContatosDbTests.Agora.UtcDateTime;
        await db.Contatos.IgnoreQueryFilters().Where(x => x.Id == contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CriadoEm, nascimento));

        if (comConversaEm is { } c2)
        {
            await db.Conversas.IgnoreQueryFilters().Where(x => x.ContatoId == contato.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UltimaMensagemEm, c2));
        }

        db.ChangeTracker.Clear();
        return contato.Id;
    }

    /// <summary>⚠️ MUDAR O STATUS NAO BASTA, e o banco ensina isso na cara: `ck_negociacoes_valor`
    /// exige `valor > 0` em tudo que nao seja `aberta` ou `perdida`, e `ck_negociacoes_terminal`
    /// proibe `ganha_em` e `perdida_em` preenchidos ao mesmo tempo. Um `ExecuteUpdate` so do
    /// `status` estoura 23514 — foi assim que a primeira versao destes testes caiu.</summary>
    private static async Task MudarStatusAsync(
        NexoraDbContext db, long contatoId, StatusNegociacao status)
    {
        var quando = Velho.AddDays(1);
        var perdida = status == StatusNegociacao.Perdida;

        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == contatoId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, status)
                .SetProperty(n => n.Valor, perdida ? null : 1000m)
                .SetProperty(n => n.GanhaEm, perdida ? null : quando)
                .SetProperty(n => n.PerdidaEm, perdida ? quando : null)
                .SetProperty(n => n.ConcluidaEm,
                    status == StatusNegociacao.Concluida ? quando : null)
                .SetProperty(n => n.CanceladaEm,
                    status == StatusNegociacao.Cancelada ? quando : null));

        db.ChangeTracker.Clear();
    }

    /// <summary>Um segundo negócio ABERTO para o mesmo contato, noutro funil — o caso do cliente
    /// recorrente. `uq_negociacoes_card_por_funil` permite um por funil.</summary>
    private static async Task NegocioAbertoAsync(NexoraDbContext db, Ambiente amb, long contatoId)
    {
        var outro = await db.Pipelines.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.EmpresaId == amb.Cenario.Id && !p.Padrao)
            .FirstOrDefaultAsync();

        var pipelineId = outro?.Id ?? amb.Cenario.Etapas[0].PipelineId;
        var etapaId = outro is null
            ? amb.Cenario.Etapas[0].Id
            : await db.EtapasFunil.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.PipelineId == outro.Id).Select(e => e.Id).FirstAsync();

        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = contatoId,
            PipelineId = pipelineId,
            EtapaId = etapaId,
            Status = StatusNegociacao.Aberta
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

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
}
