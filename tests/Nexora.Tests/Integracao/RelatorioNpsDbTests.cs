using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== NPS-1 ETAPA 3b — O RELATORIO DA PESQUISA =====================
///
/// O eixo e `data_envio` (a coorte). Cada teste aqui guarda uma regra que, quebrada, produziria
/// um numero PLAUSIVEL e errado — o tipo de defeito que ninguem percebe olhando a tela.
///
/// Relogio em 07/10: setembro e um mes FECHADO, e o periodo anterior dele e agosto inteiro.
/// ======================================================================================</summary>
[Collection("banco")]
public class RelatorioNpsDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 7, 13, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly SetembroDe = new(2026, 9, 1);
    private static readonly DateOnly SetembroAte = new(2026, 9, 30);

    /// <summary>Meio-dia de um dia de setembro, em UTC — longe das bordas do fuso.</summary>
    private static DateTime Setembro(int dia) => new(2026, 9, dia, 15, 0, 0, DateTimeKind.Utc);
    private static DateTime Agosto(int dia) => new(2026, 8, dia, 15, 0, 0, DateTimeKind.Utc);

    private static FiltroRelatorio Filtro(DateOnly de, DateOnly ate) =>
        new(de, ate, AgrupamentoSerie.Dia);

    // ==================================================================== a conta

    /// <summary>===================== NPS SOBRE AS RESPONDIDAS, TAXA SOBRE AS ENVIADAS =====================
    ///
    /// Sete enviadas: 9 e 10 (promotores), 7 (neutro), 3 (detrator), uma expirada, uma aberta e uma
    /// cancelada depois do envio.
    ///
    ///   NPS  = (2 − 1) / 4 respondidas = 25,0   ⚠️ e NAO (2 − 1) / 7 = 14,3
    ///   taxa = 4 / 7 = 57,1
    ///
    /// Dividir o NPS pelas enviadas daria um numero que CAI quando mais gente e perguntada — e ele
    /// pareceria certo.
    /// ==========================================================================================</summary>
    [Fact]
    public async Task NPS_E_SOBRE_AS_RESPONDIDAS_E_A_TAXA_SOBRE_AS_ENVIADAS()
    {
        var (db, tx, amb) = await PrepararAsync("conta");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9, Setembro(2));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(3));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 7, Setembro(4));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 3, Setembro(5));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Expirada, null, Setembro(6));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Enviada, null, Setembro(7));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Cancelada, null, Setembro(8));

        var r = await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default);
        var t = r.Totais;

        Assert.Equal(7, t.Enviadas);
        Assert.Equal(4, t.Respondidas);
        Assert.Equal(1, t.Expiradas);
        Assert.Equal(1, t.Canceladas);
        Assert.Equal(1, t.AindaAbertas);
        Assert.Equal(2, t.Promotores);
        Assert.Equal(1, t.Neutros);
        Assert.Equal(1, t.Detratores);

        Assert.Equal(25.0, t.Nps);
        Assert.Equal(57.1, t.TaxaDeResposta);
    }

    /// <summary>⚠️ AS BORDAS DAS FAIXAS: 6 e detrator, 7 e neutro, 8 e neutro, 9 e promotor. Errar
    /// um `<` por `<=` move uma nota inteira de faixa, e o NPS muda sem nada parecer errado.</summary>
    [Fact]
    public async Task AS_BORDAS_DAS_FAIXAS_6_7_8_9()
    {
        var (db, tx, amb) = await PrepararAsync("bordas");
        using var _ = db; using var __ = tx;

        foreach (short nota in new short[] { 6, 7, 8, 9 })
            await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, nota, Setembro(10));

        var t = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(1, t.Detratores);
        Assert.Equal(2, t.Neutros);
        Assert.Equal(1, t.Promotores);
    }

    /// <summary>===================== `PossivelNota` E SUSPEITA, NAO RESULTADO =====================
    ///
    /// A nota esta preenchida, e ninguem a confirmou. Ela conta como ENVIADA e como AINDA ABERTA
    /// (o vendedor ainda vai decidir), e em mais nada: um 0 suspeito virando detrator derrubaria o
    /// NPS por uma mensagem que podia ser "0 problemas, obrigado".
    /// ======================================================================================</summary>
    [Fact]
    public async Task POSSIVEL_NOTA_NAO_ENTRA_NO_NPS_NEM_NA_DISTRIBUICAO()
    {
        var (db, tx, amb) = await PrepararAsync("possivel");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.PossivelNota, 0, Setembro(2));

        var r = await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default);

        Assert.Equal(1, r.Totais.Enviadas);
        Assert.Equal(1, r.Totais.AindaAbertas);
        Assert.Equal(0, r.Totais.Respondidas);
        Assert.Equal(0, r.Totais.Detratores);
        Assert.Null(r.Totais.Nps);
        Assert.All(r.Distribuicao, f => Assert.Equal(0, f.Quantas));
    }

    /// <summary>Pesquisa `agendada` ainda nao saiu e nao tem `data_envio` — e a coorte e do que
    /// CHEGOU ao cliente. Contá-la como enviada derrubaria a taxa de resposta com perguntas que
    /// ninguem recebeu.</summary>
    [Fact]
    public async Task PESQUISA_QUE_NAO_SAIU_NAO_ENTRA_EM_NADA()
    {
        var (db, tx, amb) = await PrepararAsync("agendada");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Agendada, null, envio: null);
        await PesquisaAsync(db, amb, StatusPesquisaNps.Cancelada, null, envio: null);

        var t = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(0, t.Enviadas);
        Assert.Equal(0, t.Canceladas);
        Assert.Null(t.TaxaDeResposta);
    }

    /// <summary>Zero e um NPS REAL — tantos promotores quanto detratores. "Sem resposta" e outra
    /// coisa, e a tela escreve outra frase.</summary>
    [Fact]
    public async Task SEM_RESPOSTA_O_NPS_E_NULO_E_NAO_ZERO()
    {
        var (db, tx, amb) = await PrepararAsync("nulo");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Expirada, null, Setembro(2));

        var t = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(1, t.Enviadas);
        Assert.Null(t.Nps);
        Assert.Equal(0.0, t.TaxaDeResposta);
    }

    // ==================================================================== o corte

    /// <summary>===================== O MES COMECA NO FUSO DA EMPRESA =====================
    ///
    /// 01/09 02:00 UTC e 31/08 23:00 em Brasilia — AGOSTO. 01/09 03:00 UTC e meia-noite em
    /// Brasilia — SETEMBRO. Cortar em UTC poria tres horas de agosto em setembro todo mes.
    /// ====================================================================</summary>
    [Fact]
    public async Task O_CORTE_E_NA_MEIA_NOITE_DA_EMPRESA_E_NAO_DO_SERVIDOR()
    {
        var (db, tx, amb) = await PrepararAsync("fuso");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10,
            new DateTime(2026, 9, 1, 2, 0, 0, DateTimeKind.Utc));   // 31/08 23h local
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 0,
            new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc));   // 01/09 00h local

        var t = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(1, t.Enviadas);
        Assert.Equal(1, t.Detratores);
        Assert.Equal(0, t.Promotores);
    }

    /// <summary>O ultimo dia entra INTEIRO: 30/09 23:59 local e 01/10 02:59 UTC. Corte com `<=` no
    /// fim perderia o dia; corte sem o `+1d` tambem.</summary>
    [Fact]
    public async Task O_ULTIMO_DIA_DO_PERIODO_ENTRA_INTEIRO()
    {
        var (db, tx, amb) = await PrepararAsync("ultimo-dia");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9,
            new DateTime(2026, 10, 1, 2, 59, 0, DateTimeKind.Utc));   // 30/09 23:59 local
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9,
            new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc));    // 01/10 00:00 local

        var t = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(1, t.Enviadas);
    }

    // ==================================================================== a distribuicao

    /// <summary>As ONZE barras, sempre, de 0 a 10 e nesta ordem — inclusive as vazias. Omitir a
    /// nota que ninguem deu faria as barras mudarem de lugar entre dois periodos.</summary>
    [Fact]
    public async Task A_DISTRIBUICAO_TEM_AS_ONZE_NOTAS_INCLUSIVE_AS_VAZIAS()
    {
        var (db, tx, amb) = await PrepararAsync("distribuicao");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(2));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(3));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 4, Setembro(4));

        var d = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Distribuicao;

        Assert.Equal(Enumerable.Range(0, 11).Select(n => (short)n), d.Select(f => f.Nota));
        Assert.Equal(2, d[10].Quantas);
        Assert.Equal(1, d[4].Quantas);
        Assert.Equal(3, d.Sum(f => f.Quantas));
    }

    // ==================================================================== quem ve o que

    /// <summary>===================== O VENDEDOR VE O NPS DAS VENDAS DELE =====================
    ///
    /// ⚠️ E O FILTRO FORJADO E DESCARTADO: o vendedor pede o responsavel da Ana e recebe os dele.
    /// O gestor, com o MESMO pedido sem filtro, ve os dois — senao o teste passaria com uma regra
    /// que nao devolve nada para ninguem.
    /// ==================================================================</summary>
    [Fact]
    public async Task VENDEDOR_SO_VE_AS_PESQUISAS_DAS_VENDAS_DELE_nem_forjando_o_filtro()
    {
        var (db, tx, amb) = await PrepararAsync("vendedor");
        using var _ = db; using var __ = tx;

        var ana = amb.Cenario.Dono;
        var bruno = await VendedorAsync(db, amb, "bruno");

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(2), ana.Id);
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(3), ana.Id);
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 2, Setembro(4), bruno.Id);

        amb.Contexto.UsuarioId = bruno.Id;
        amb.Contexto.Papel = "vendedor";

        var forjado = Filtro(SetembroDe, SetembroAte) with { ResponsavelId = ana.Id };
        var doBruno = (await amb.Servico.LerAsync(forjado, default)).Totais;

        Assert.Equal(1, doBruno.Enviadas);
        Assert.Equal(1, doBruno.Detratores);
        Assert.Equal(0, doBruno.Promotores);

        amb.Contexto.UsuarioId = ana.Id;
        amb.Contexto.Papel = "gestor";
        var doGestor = (await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default)).Totais;

        Assert.Equal(3, doGestor.Enviadas);

        // E o gestor PODE pedir um so.
        var soDoBruno = (await amb.Servico.LerAsync(
            Filtro(SetembroDe, SetembroAte) with { ResponsavelId = bruno.Id }, default)).Totais;
        Assert.Equal(1, soDoBruno.Enviadas);
    }

    /// <summary>⚠️ SQL CRU NAO TEM FILTRO GLOBAL. O `empresa_id = $1` foi escrito a mao, e e so ele
    /// que segura a pesquisa da outra empresa fora deste numero.</summary>
    [Fact]
    public async Task PESQUISA_DE_OUTRA_EMPRESA_NAO_ENTRA()
    {
        var (db, tx, amb) = await PrepararAsync("isolamento");
        using var _ = db; using var __ = tx;

        var outra = await Semeador.TenantAsync(db, "nps-rel-outra");
        await PesquisaDeAsync(db, outra, StatusPesquisaNps.Respondida, 0, Setembro(2), outra.Dono.Id);

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(2));

        var r = await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default);

        Assert.Equal(1, r.Totais.Enviadas);
        Assert.Equal(0, r.Totais.Detratores);
        Assert.Equal(0, r.Distribuicao[0].Quantas);
    }

    // ==================================================================== a comparacao

    /// <summary>Setembro fechado compara com AGOSTO INTEIRO — a regra e do `PeriodoAnterior`, a
    /// mesma do relatorio de vendas. Agosto: 1 promotor e 1 detrator (NPS 0). Setembro: 2
    /// promotores (NPS 100).
    ///
    /// ⚠️ O NPS ZERO DE AGOSTO E UM ZERO DE VERDADE, e a variacao absoluta e 100 pontos.</summary>
    [Fact]
    public async Task COMPARA_COM_O_MES_ANTERIOR_INTEIRO()
    {
        var (db, tx, amb) = await PrepararAsync("comparacao");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Agosto(1));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 0, Agosto(31));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 9, Setembro(10));
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, 10, Setembro(20));

        var r = await amb.Servico.LerAsync(Filtro(SetembroDe, SetembroAte), default);
        var c = Assert.IsType<ComparativoNps>(r.Comparativo);

        Assert.Equal(new DateOnly(2026, 8, 1), c.Nps.AnteriorDe);
        Assert.Equal(new DateOnly(2026, 8, 31), c.Nps.AnteriorAte);
        Assert.False(c.EmAndamento);

        Assert.Equal(100m, c.Nps.Atual);
        Assert.Equal(0m, c.Nps.Anterior);
        Assert.Equal(100m, c.Nps.VariacaoAbsoluta);

        Assert.Equal(2m, c.Respondidas.Atual);
        Assert.Equal(2m, c.Respondidas.Anterior);
        Assert.Equal(2m, c.Promotores.Atual);
        Assert.Equal(1m, c.Promotores.Anterior);

        // A distribuicao e SO do periodo atual: o 0 de agosto nao aparece.
        Assert.Equal(0, r.Distribuicao[0].Quantas);
    }

    [Fact]
    public async Task DATA_FINAL_ANTES_DA_INICIAL_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("invertida");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Servico.LerAsync(Filtro(SetembroAte, SetembroDe), default));
    }

    // ==================================================================== infraestrutura

    private sealed record Ambiente(Cenario Cenario, ContextoMutavel Contexto, ServicoRelatorioNps Servico);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(Agora);
        var db = banco.NovoContexto(ctx, relogio, new Nexora.Core.Auditoria.ColetorAuditoria());
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"nps-rel-{sufixo}");

        // O FUSO EXPLICITO: o teste do corte depende dele, e nao do padrao de quem semeou.
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.FusoHorario, "America/Sao_Paulo"));

        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new Ambiente(cenario, ctx, new ServicoRelatorioNps(db, ctx, relogio)));
    }

    private static Task PesquisaAsync(
        NexoraDbContext db, Ambiente amb, StatusPesquisaNps status, short? nota, DateTime? envio,
        long? responsavelId = null) =>
        PesquisaDeAsync(db, amb.Cenario, status, nota, envio, responsavelId ?? amb.Cenario.Dono.Id);

    /// <summary>Uma pesquisa = uma venda concluida propria (`uq_pesquisas_nps_negociacao`). O
    /// responsavel mora na NEGOCIACAO, que e onde o relatorio o le.</summary>
    private static async Task PesquisaDeAsync(
        NexoraDbContext db, Cenario cenario, StatusPesquisaNps status, short? nota, DateTime? envio,
        long responsavelId)
    {
        var etapa = cenario.Etapas[0];

        var negocio = new Negociacao
        {
            EmpresaId = cenario.Id,
            ContatoId = cenario.Contato.Id,
            PipelineId = etapa.PipelineId,
            EtapaId = etapa.Id,
            Status = StatusNegociacao.Concluida,
            Valor = 1000m,
            ResponsavelId = responsavelId,
            GanhaEm = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
            ConcluidaEm = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc)
        };
        db.Negociacoes.Add(negocio);
        await db.SaveChangesAsync();

        var respondida = status == StatusPesquisaNps.Respondida;

        db.PesquisasNps.Add(new PesquisaNps
        {
            EmpresaId = cenario.Id,
            NegociacaoId = negocio.Id,
            ContatoId = cenario.Contato.Id,
            Status = status,
            Nota = nota,
            DataAgendada = new DateOnly(2026, 7, 28),
            DataLimite = new DateOnly(2026, 12, 31),
            DataEnvio = envio,
            DataResposta = respondida ? envio?.AddHours(2) : null
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
