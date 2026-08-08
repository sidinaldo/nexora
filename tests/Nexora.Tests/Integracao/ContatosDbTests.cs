using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O ciclo de vida do contato contra Postgres real.
///
/// Antes deste bloco a ÚNICA escrita de contato no sistema era a criação automática pelo webhook.
/// Nada preenchia `ganho_em`, `perdido_em`, `valor` ou `ordem_kanban` — as colunas existiam, os
/// índices existiam, o dashboard lia, e nenhum caminho escrevia.</summary>
[Collection("banco")]
public class ContatosDbTests(BancoTeste banco)
{
    internal static readonly DateTimeOffset Agora = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ==================================================================== cadastro
    [Fact]
    public async Task Criar_canonicaliza_o_telefone_e_entra_na_primeira_etapa()
    {
        var (db, tx, amb) = await PrepararAsync("criar");
        using var _ = db; using var __ = tx;

        // O vendedor digita com máscara e sem DDI; o WhatsApp entrega 5584988887777.
        var id = await amb.Contatos.CriarAsync(
            new NovoContato("Maria Silva", "(84) 98888-1234", Email: "maria@exemplo.com"), default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);

        Assert.Equal("5584988881234", c.Telefone);          // canonicalizado, só dígitos, com DDI
        Assert.Equal(amb.Cenario.PrimeiraEtapa.Id, c.EtapaId);
        Assert.Equal(OrigemLead.Manual, c.Origem);          // cadastro manual, não WhatsApp
        Assert.Null(c.GanhoEm);
        Assert.Null(c.PerdidoEm);
    }

    [Fact]
    public async Task Telefone_invalido_e_recusado_em_vez_de_virar_contato_mudo()
    {
        // Aceitar lixo aqui produz o pior modo de falha do sistema: o contato existe, aparece na
        // tela, e simplesmente nunca recebe nem casa com mensagem nenhuma. Sem erro no log.
        var (db, tx, amb) = await PrepararAsync("tel-ruim");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.CriarAsync(new NovoContato("Fulano", "123"), default));

        Assert.Contains("inválido", erro.Message);
        Assert.False(erro.Conflito);   // entrada errada = 400, não 409
    }

    [Fact]
    public async Task Telefone_repetido_e_recusado_com_conflito()
    {
        var (db, tx, amb) = await PrepararAsync("tel-repetido");
        using var _ = db; using var __ = tx;

        await amb.Contatos.CriarAsync(new NovoContato("Primeiro", "(84) 98888-4321"), default);

        // Mesmo número, digitado de outro jeito — a canonicalização faz os dois colidirem.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.CriarAsync(new NovoContato("Segundo", "5584988884321"), default));

        Assert.True(erro.Conflito);
    }

    [Fact]
    public async Task Editar_altera_os_dados_e_NAO_mexe_na_etapa()
    {
        // A etapa só muda por MoverAsync (que calcula ordem e recusa a etapa de ganho). Se este
        // PUT aceitasse etapa, existiria um segundo caminho sem nenhuma dessas regras.
        var (db, tx, amb) = await PrepararAsync("editar");
        using var _ = db; using var __ = tx;

        var id = await amb.Contatos.CriarAsync(new NovoContato("Nome Antigo", "(84) 98888-5555"), default);
        var etapaOriginal = (await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Id == id)).EtapaId;
        db.ChangeTracker.Clear();

        await amb.Contatos.AtualizarAsync(id, new EditarContato(
            "Nome Novo", "(84) 98888-5555", Email: "novo@exemplo.com",
            ResponsavelId: amb.Cenario.Dono.Id, Valor: 2500m), default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);

        Assert.Equal("Nome Novo", c.Nome);
        Assert.Equal(2500m, c.Valor);
        Assert.Equal(amb.Cenario.Dono.Id, c.ResponsavelId);
        Assert.Equal(etapaOriginal, c.EtapaId);
    }

    // ==================================================================== leitura
    [Fact]
    public async Task Listar_busca_por_nome_e_por_digitos_do_telefone()
    {
        // O vendedor digita "(84) 98888" e a coluna guarda "5584988887777". Sem tirar a máscara
        // da busca, procurar pelo que está na tela não acha nada.
        var (db, tx, amb) = await PrepararAsync("busca");
        using var _ = db; using var __ = tx;

        await amb.Contatos.CriarAsync(new NovoContato("Joana Prado", "(84) 98111-2222"), default);
        await amb.Contatos.CriarAsync(new NovoContato("Ricardo Alves", "(84) 98333-4444"), default);

        var porNome = await amb.Contatos.ListarAsync(FiltroContato.Abertos, "joana", null, null, 1, 30, default);
        Assert.Equal("Joana Prado", Assert.Single(porNome.Itens).Nome);

        var porTelefone = await amb.Contatos.ListarAsync(FiltroContato.Abertos, "(84) 98333", null, null, 1, 30, default);
        Assert.Equal("Ricardo Alves", Assert.Single(porTelefone.Itens).Nome);
    }

    [Fact]
    public async Task Listar_pagina_no_SQL_e_devolve_o_total_do_conjunto_inteiro()
    {
        var (db, tx, amb) = await PrepararAsync("paginar");
        using var _ = db; using var __ = tx;

        for (var i = 0; i < 7; i++)
            await amb.Contatos.CriarAsync(new NovoContato($"Contato {i:D2}", $"(84) 97000-00{i:D2}"), default);

        var p1 = await amb.Contatos.ListarAsync(FiltroContato.Abertos, null, null, null, 1, 3, default);
        var p2 = await amb.Contatos.ListarAsync(FiltroContato.Abertos, null, null, null, 2, 3, default);

        // 7 criados + o do Semeador = 8.
        Assert.Equal(8, p1.Total);
        Assert.Equal(3, p1.Itens.Count);
        Assert.Equal(3, p2.Itens.Count);
        Assert.Empty(p1.Itens.Select(i => i.Id).Intersect(p2.Itens.Select(i => i.Id)));
    }

    [Fact]
    public async Task Detalhe_traz_a_conversa_e_os_lembretes_numa_chamada_so()
    {
        var (db, tx, amb) = await PrepararAsync("detalhe");
        using var _ = db; using var __ = tx;

        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Cenario.Contato.Id,
            Origem = OrigemLembrete.Manual,
            DataAlvo = new DateOnly(2026, 8, 10),
            Titulo = "ligar de volta"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var d = await amb.Contatos.DetalheAsync(amb.Cenario.Contato.Id, default);

        Assert.Equal(amb.Cenario.Contato.Nome, d.Contato.Nome);
        Assert.Equal(amb.Cenario.Conversa.Id, d.Contato.ConversaId);
        Assert.NotNull(d.UltimaMensagemEm);
        Assert.Equal("ligar de volta", Assert.Single(d.Lembretes).Titulo);
    }

    // ==================================================================== estado terminal
    [Fact]
    public async Task Marcar_ganho_sem_valor_e_recusado()
    {
        var (db, tx, amb) = await PrepararAsync("ganho-sem-valor");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 0m, null, default));

        Assert.Contains("valor", erro.Message);
    }

    [Fact]
    public async Task Marcar_ganho_carimba_valor_data_e_MOVE_para_a_etapa_de_venda()
    {
        // A porta única: carimbar e mover na MESMA operação. É isso que permite ao cliente tratar
        // "arrastar para Venda" e "clicar em venda fechada" como a mesma coisa.
        var (db, tx, amb) = await PrepararAsync("ganho");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 3200m, null, default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == amb.Cenario.Contato.Id);

        Assert.Equal(3200m, c.Valor);
        Assert.NotNull(c.GanhoEm);
        Assert.Null(c.PerdidoEm);

        var etapaGanho = amb.Cenario.Etapas.Single(e => e.EGanho);
        Assert.Equal(etapaGanho.Id, c.EtapaId);
    }

    [Fact]
    public async Task Marcar_perdido_sem_motivo_e_recusado()
    {
        var (db, tx, amb) = await PrepararAsync("perda-sem-motivo");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "   ", default));
    }

    [Fact]
    public async Task Marcar_perdido_preserva_a_etapa_onde_a_negociacao_morreu()
    {
        var (db, tx, amb) = await PrepararAsync("perda");
        using var _ = db; using var __ = tx;

        var etapaAntes = amb.Cenario.Contato.EtapaId;
        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "achou caro", default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == amb.Cenario.Contato.Id);

        Assert.NotNull(c.PerdidoEm);
        Assert.Equal("achou caro", c.MotivoPerda);
        Assert.Equal(etapaAntes, c.EtapaId);   // o card sai do quadro pelo índice parcial
    }

    [Fact]
    public async Task Ganho_sobre_perdido_e_recusado_em_vez_de_apagar_a_perda_por_baixo_do_pano()
    {
        // ck_contatos_terminal proíbe os dois juntos. Limpar a perda em silêncio faria o
        // histórico sumir sem ninguém entender por quê.
        var (db, tx, amb) = await PrepararAsync("ganho-sobre-perda");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarPerdidoAsync(amb.Cenario.Contato.Id, "sumiu", default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 1000m, null, default));

        Assert.True(erro.Conflito);
        Assert.Contains("Reabra", erro.Message);
    }

    [Fact]
    public async Task Reabrir_limpa_ganho_perda_e_motivo_mas_PRESERVA_o_valor()
    {
        var (db, tx, amb) = await PrepararAsync("reabrir");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 4800m, null, default);
        db.ChangeTracker.Clear();

        await amb.Contatos.ReabrirAsync(amb.Cenario.Contato.Id, default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == amb.Cenario.Contato.Id);

        Assert.Null(c.GanhoEm);
        Assert.Null(c.PerdidoEm);
        Assert.Null(c.MotivoPerda);
        Assert.Equal(4800m, c.Valor);   // a estimativa fica: reabrir não é apagar o negócio

        // E sai da coluna de venda — senão ficaria lá sem ganho_em, o estado divergente que a
        // porta única existe para impedir.
        Assert.Equal(amb.Cenario.PrimeiraEtapa.Id, c.EtapaId);
    }

    [Fact]
    public async Task Reabrir_contato_que_ja_esta_aberto_devolve_conflito()
    {
        var (db, tx, amb) = await PrepararAsync("reabrir-aberto");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.ReabrirAsync(amb.Cenario.Contato.Id, default));
        Assert.True(erro.Conflito);
    }

    // ==================================================================== LGPD
    [Fact]
    public async Task Anonimizar_zera_a_PII_e_PRESERVA_o_historico()
    {
        var (db, tx, amb) = await PrepararAsync("lgpd");
        using var _ = db; using var __ = tx;

        var alvo = amb.Cenario.Contato.Id;
        await amb.Contatos.MarcarGanhoAsync(alvo, 900m, null, default);
        db.ChangeTracker.Clear();

        await amb.Contatos.AnonimizarAsync(alvo, default);

        db.ChangeTracker.Clear();
        var c = await db.Contatos.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == alvo);

        // PII zerada
        Assert.Equal("Contato anonimizado", c.Nome);
        Assert.Equal($"ANON-{alvo}", c.Telefone);
        Assert.Null(c.Email);
        Assert.Null(c.Observacoes);
        Assert.Null(c.OrigemDetalhe);
        Assert.NotNull(c.AnonimizadoEm);

        // Histórico preservado: nem delete físico, nem soft delete.
        Assert.Equal(900m, c.Valor);
        Assert.NotNull(c.GanhoEm);
        Assert.True(await db.Conversas.IgnoreQueryFilters().AnyAsync(v => v.ContatoId == alvo));
        Assert.True(await db.Mensagens.IgnoreQueryFilters().AnyAsync(m => m.ContatoId == alvo));
    }

    [Fact]
    public async Task Dois_contatos_anonimizados_convivem_sem_colidir_no_telefone()
    {
        // `telefone` é NOT NULL com índice único por empresa. Sem marcador determinístico e
        // único, ou a constraint quebra ou os dois colidem.
        var (db, tx, amb) = await PrepararAsync("lgpd-x2");
        using var _ = db; using var __ = tx;

        var a = await amb.Contatos.CriarAsync(new NovoContato("Pessoa A", "(84) 96000-1111"), default);
        var b = await amb.Contatos.CriarAsync(new NovoContato("Pessoa B", "(84) 96000-2222"), default);
        db.ChangeTracker.Clear();

        await amb.Contatos.AnonimizarAsync(a, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.AnonimizarAsync(b, default);

        db.ChangeTracker.Clear();
        var telefones = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == a || c.Id == b).Select(c => c.Telefone).ToListAsync();

        Assert.Equal(2, telefones.Distinct().Count());
        Assert.Contains($"ANON-{a}", telefones);
        Assert.Contains($"ANON-{b}", telefones);
    }

    [Fact]
    public async Task Anonimizado_some_da_lista_mas_o_telefone_dele_libera_cadastro_novo()
    {
        // O índice único é PARCIAL (`WHERE anonimizado_em IS NULL`): a linha sai do índice ao ser
        // anonimizada, e o mesmo número pode voltar como contato novo. Se a checagem de duplicata
        // não repetisse o predicado, o cadastro seria barrado com uma mensagem mentirosa.
        var (db, tx, amb) = await PrepararAsync("lgpd-libera");
        using var _ = db; using var __ = tx;

        var antigo = await amb.Contatos.CriarAsync(new NovoContato("Antigo", "(84) 95000-7777"), default);
        db.ChangeTracker.Clear();
        await amb.Contatos.AnonimizarAsync(antigo, default);
        db.ChangeTracker.Clear();

        var lista = await amb.Contatos.ListarAsync(FiltroContato.Todos, null, null, null, 1, 50, default);
        Assert.DoesNotContain(lista.Itens, i => i.Id == antigo);

        // E o número volta a ser cadastrável.
        var novo = await amb.Contatos.CriarAsync(new NovoContato("Novo", "(84) 95000-7777"), default);
        Assert.NotEqual(antigo, novo);
    }

    [Fact]
    public async Task Anonimizado_nao_aceita_mais_alteracao()
    {
        var (db, tx, amb) = await PrepararAsync("lgpd-travado");
        using var _ = db; using var __ = tx;

        var alvo = amb.Cenario.Contato.Id;
        await amb.Contatos.AnonimizarAsync(alvo, default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarGanhoAsync(alvo, 100m, null, default));
        Assert.True(erro.Conflito);
    }

    // ==================================================================== multi-tenant
    [Fact]
    public async Task Criar_com_etapa_de_OUTRA_empresa_e_recusado()
    {
        // O query filter protege a LEITURA. Um id de etapa vindo do cliente precisa de checagem
        // explícita — sem ela, o contato entraria no funil de outro tenant.
        var (db, tx, amb, alheia) = await PrepararComVizinhaAsync("etapa-alheia");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.CriarAsync(
                new NovoContato("Invasor", "(84) 94000-1111", EtapaId: alheia.PrimeiraEtapa.Id), default));

        Assert.Contains("não encontrada", erro.Message);
    }

    [Fact]
    public async Task Atribuir_responsavel_de_OUTRA_empresa_e_recusado()
    {
        var (db, tx, amb, alheia) = await PrepararComVizinhaAsync("resp-alheio");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.CriarAsync(
                new NovoContato("Invasor", "(84) 94000-2222", ResponsavelId: alheia.Dono.Id), default));

        Assert.Contains("não encontrado", erro.Message);
    }

    [Fact]
    public async Task Contato_de_outra_empresa_nao_aparece_nem_pode_ser_alterado()
    {
        var (db, tx, amb, alheia) = await PrepararComVizinhaAsync("contato-alheio");
        using var _ = db; using var __ = tx;

        var lista = await amb.Contatos.ListarAsync(FiltroContato.Todos, null, null, null, 1, 50, default);
        Assert.DoesNotContain(lista.Itens, i => i.Id == alheia.Contato.Id);

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.DetalheAsync(alheia.Contato.Id, default));

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Contatos.MarcarGanhoAsync(alheia.Contato.Id, 500m, null, default));
    }

    // ==================================================================== O CRITÉRIO DO BLOCO
    [Fact]
    public async Task O_DASHBOARD_SAI_DO_ZERO_DEPOIS_DE_MARCAR_UM_GANHO()
    {
        // ===================== O TESTE QUE FECHA O BURACO =====================
        // O dashboard do bloco 6 lê `ganho_em` e `valor`. Até este bloco, NADA no produto escrevia
        // essas colunas — "vendas do mês" mostrava zero para sempre, e não por falta de vendas.
        // Este teste amarra as duas pontas: a escrita nova e a leitura que já existia.
        // ======================================================================
        var (db, tx, amb) = await PrepararAsync("dashboard-zero");
        using var _ = db; using var __ = tx;

        var antes = await amb.Dashboard.DashboardAsync(default);
        Assert.Equal(0, antes.VendasDoMes);
        Assert.Equal(0m, antes.FaturamentoDoMes);
        Assert.Equal(0d, antes.TaxaConversao);

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 7500m, null, default);
        db.ChangeTracker.Clear();

        var depois = await amb.Dashboard.DashboardAsync(default);

        Assert.Equal(1, depois.VendasDoMes);
        Assert.Equal(7500m, depois.FaturamentoDoMes);
        Assert.Equal(1d, depois.TaxaConversao);   // 1 ganho, 0 perdas

        // E o funil também reflete: o card foi para a coluna de venda com o valor.
        var etapaGanho = amb.Cenario.Etapas.Single(e => e.EGanho);
        var coluna = depois.Funil.Single(f => f.EtapaId == etapaGanho.Id);
        Assert.Equal(1, coluna.Contatos);
        Assert.Equal(7500m, coluna.Valor);
    }

    [Fact]
    public async Task Perda_entra_na_taxa_de_conversao_mas_nao_no_faturamento()
    {
        var (db, tx, amb) = await PrepararAsync("dashboard-perda");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 1000m, null, default);
        db.ChangeTracker.Clear();

        var perdido = await amb.Contatos.CriarAsync(new NovoContato("Que perdeu", "(84) 93000-1111"), default);
        await amb.Contatos.MarcarPerdidoAsync(perdido, "preço", default);
        db.ChangeTracker.Clear();

        var d = await amb.Dashboard.DashboardAsync(default);

        Assert.Equal(1, d.VendasDoMes);
        Assert.Equal(1000m, d.FaturamentoDoMes);
        Assert.Equal(0.5d, d.TaxaConversao);   // 1 ganho / (1 ganho + 1 perda)
    }

    [Fact]
    public async Task Contato_anonimizado_continua_contando_no_dashboard()
    {
        // A anonimização apaga quem era a pessoa, não que a venda aconteceu.
        var (db, tx, amb) = await PrepararAsync("dashboard-lgpd");
        using var _ = db; using var __ = tx;

        await amb.Contatos.MarcarGanhoAsync(amb.Cenario.Contato.Id, 2000m, null, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.AnonimizarAsync(amb.Cenario.Contato.Id, default);
        db.ChangeTracker.Clear();

        var d = await amb.Dashboard.DashboardAsync(default);

        Assert.Equal(1, d.VendasDoMes);
        Assert.Equal(2000m, d.FaturamentoDoMes);
    }

    // ==================================================================== apoio
    internal sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto,
        IServicoContatos Contatos, IServicoFunil Funil, IServicoDashboard Dashboard,
        IServicoVendas Vendas,
        /// <summary>O MESMO coletor do contexto (AUD-1): quem monta um serviço à mão no teste
        /// precisa passar este, senão a declaração não chega ao interceptor.</summary>
        ColetorAuditoria Trilha,
        /// <summary>O relógio CONGELADO. Sai daqui para quem chama job direto — a conclusão
        /// automática (NEG-2) recebe um `TimeProvider`, e o real faria o prazo depender da hora
        /// em que a suíte roda.</summary>
        TimeProvider Relogio);

    internal static async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        BancoTeste banco, string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(Agora);

        // O relógio falso vai TAMBÉM para o interceptor: sem isso o `criado_em` sai com a data
        // real da máquina, e "leads de hoje" do dashboard conta contra um "hoje" diferente.
        // O MESMO coletor no contexto e nos servicos (AUD-1): e o elo entre a declaracao e a
        // gravacao da trilha.
        var trilha = new ColetorAuditoria();
        var db = banco.NovoContexto(ctx, relogio, trilha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);

        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new Ambiente(
            cenario, ctx,
            new ServicoContatos(db, ctx, PublicadorDeTeste.Novo(db, relogio), trilha, relogio),
            new ServicoFunil(db, PublicadorDeTeste.Novo(db, relogio), trilha),
            new ServicoDashboard(db, relogio),
            new ServicoVendas(db, ctx, trilha, relogio),
            trilha, relogio));
    }

    private Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(string sufixo) =>
        PrepararAsync(banco, sufixo);

    /// <summary>O mesmo, mais um SEGUNDO tenant já semeado — para os testes de isolamento terem
    /// contra o que colidir.</summary>
    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb, Cenario Alheia)>
        PrepararComVizinhaAsync(string sufixo)
    {
        var (db, tx, amb) = await PrepararAsync(sufixo);

        // A vizinha é semeada com o contexto apontando para o tenant do teste; o Semeador atribui
        // empresa_id explicitamente, então as linhas saem corretas de qualquer jeito.
        var alheia = await Semeador.TenantAsync(db, $"{sufixo}-vizinha");
        db.ChangeTracker.Clear();

        return (db, tx, amb, alheia);
    }
}
