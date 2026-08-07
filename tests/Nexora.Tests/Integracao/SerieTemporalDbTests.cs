using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>A série temporal e o feed de atividades, contra Postgres real.
///
/// O fuso de negócio é UTC-3, então TODA data de teste é montada em hora local e convertida na
/// hora de escrever. Um lead criado às 22h de Brasília é 01h UTC do dia SEGUINTE: se o teste
/// gravasse hora UTC direto, metade dos pontos cairia no dia errado e o erro pareceria do
/// serviço.</summary>
[Collection("banco")]
public class SerieTemporalDbTests(BancoTeste banco, Xunit.Abstractions.ITestOutputHelper saida)
{
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    /// <summary>06/08/2026 é quinta. A série de teste usa a semana dela.</summary>
    private static readonly DateOnly Quinta = new(2026, 8, 6);

    private static readonly TimeSpan OffsetBrasil = TimeSpan.FromHours(-3);

    /// <summary>Instante UTC a partir de uma hora de PAREDE de Brasília.</summary>
    private static DateTime Local(DateOnly dia, int hora, int minuto = 0) =>
        new DateTimeOffset(dia.ToDateTime(new TimeOnly(hora, minuto)), OffsetBrasil).UtcDateTime;

    // ==================================================================== série
    [Fact]
    public async Task SERIE_BATE_COM_A_CONTAGEM_MANUAL()
    {
        var (db, tx, amb) = await PrepararAsync("manual");
        using var _ = db; using var __ = tx;

        // Quinta: 3 leads, 2 deles ganhos (R$ 100 + R$ 250).
        await LeadAsync(db, amb.Cenario, "a", Local(Quinta, 9), ganhoEm: Local(Quinta, 17), valor: 100m);
        await LeadAsync(db, amb.Cenario, "b", Local(Quinta, 10), ganhoEm: Local(Quinta, 18), valor: 250m);
        await LeadAsync(db, amb.Cenario, "c", Local(Quinta, 11));

        // Sexta: 1 lead, nenhum ganho.
        await LeadAsync(db, amb.Cenario, "d", Local(Quinta.AddDays(1), 9));

        var serie = await amb.Serie.ObterAsync(Quinta, Quinta.AddDays(1), AgrupamentoSerie.Dia, default);

        Assert.Equal(2, serie.Pontos.Count);

        var quinta = serie.Pontos[0];
        Assert.Equal(Quinta, quinta.Data);
        Assert.Equal(3, quinta.Leads);
        Assert.Equal(2, quinta.Vendas);
        Assert.Equal(350m, quinta.Faturamento);

        var sexta = serie.Pontos[1];
        Assert.Equal(1, sexta.Leads);
        Assert.Equal(0, sexta.Vendas);
        Assert.Equal(0m, sexta.Faturamento);
    }

    [Fact]
    public async Task DIA_SEM_DADO_VOLTA_COM_ZERO_NAO_AUSENTE()
    {
        // ===================== POR QUE ISTO IMPORTA =====================
        // Gráfico com buraco mente sobre a TENDÊNCIA, e mente para melhor: sem o ponto, a linha
        // liga o dia anterior no seguinte e desenha uma subida contínua onde houve dois dias
        // parados. `generate_series` + LEFT JOIN é o que garante o ponto zerado.
        // ===============================================================
        var (db, tx, amb) = await PrepararAsync("buraco");
        using var _ = db; using var __ = tx;

        // Uma única venda no primeiro dia; os quatro seguintes vazios.
        await LeadAsync(db, amb.Cenario, "só", Local(Quinta, 9), ganhoEm: Local(Quinta, 15), valor: 500m);

        var serie = await amb.Serie.ObterAsync(Quinta, Quinta.AddDays(4), AgrupamentoSerie.Dia, default);

        Assert.Equal(5, serie.Pontos.Count);
        for (var i = 0; i < 5; i++)
            Assert.Equal(Quinta.AddDays(i), serie.Pontos[i].Data);

        Assert.Equal(500m, serie.Pontos[0].Faturamento);
        foreach (var vazio in serie.Pontos.Skip(1))
        {
            Assert.Equal(0, vazio.Leads);
            Assert.Equal(0, vazio.Vendas);
            Assert.Equal(0m, vazio.Faturamento);
            // A MÉDIA é a exceção: null, não zero. Zero minuto diria "respondeu na hora" num dia
            // em que ninguém escreveu — a métrica mostraria seu melhor número no dia mais parado.
            Assert.Null(vazio.TempoRespostaMinutos);
        }
    }

    [Fact]
    public async Task TEMPO_MEDIO_DESCONTA_AS_HORAS_FORA_DA_JANELA()
    {
        // ===================== O CASO CONCRETO =====================
        // Mensagem às 22h, respondida às 8h05 do dia seguinte. Cronômetro de parede: 10 horas.
        // Tempo ÚTIL: 5 minutos — a janela é 8h-20h e ninguém podia responder à noite.
        //
        // Sem o desconto, a métrica puniria a equipe pelo horário em que o cliente escreve, e o
        // número seria pior justamente para quem atende cliente noturno.
        // ===========================================================
        var (db, tx, amb) = await PrepararAsync("janela");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb.Cenario, "noturno", Local(Quinta, 9));
        var conversa = await ConversaAsync(db, amb.Cenario, contato);

        await MensagemAsync(db, amb.Cenario, conversa, contato, DirecaoMensagem.Entrada, Local(Quinta, 22));
        await MensagemAsync(db, amb.Cenario, conversa, contato, DirecaoMensagem.Saida,
            Local(Quinta.AddDays(1), 8, 5));

        var serie = await amb.Serie.ObterAsync(Quinta, Quinta.AddDays(1), AgrupamentoSerie.Dia, default);

        // O ponto é o da CHEGADA (quinta), não o da resposta.
        Assert.Equal(5m, serie.Pontos[0].TempoRespostaMinutos);
        Assert.Null(serie.Pontos[1].TempoRespostaMinutos);
    }

    [Fact]
    public async Task So_a_PRIMEIRA_mensagem_da_rajada_conta_como_espera()
    {
        // Mesma regra do `aguardando_desde ??=` do webhook: se o cliente manda três mensagens
        // seguidas, ele espera desde a primeira. Contando as três, a média cairia artificialmente
        // — as duas últimas teriam "esperado" quase nada.
        var (db, tx, amb) = await PrepararAsync("rajada");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb.Cenario, "falante", Local(Quinta, 8));
        var conversa = await ConversaAsync(db, amb.Cenario, contato);

        await MensagemAsync(db, amb.Cenario, conversa, contato, DirecaoMensagem.Entrada, Local(Quinta, 9, 0));
        await MensagemAsync(db, amb.Cenario, conversa, contato, DirecaoMensagem.Entrada, Local(Quinta, 9, 20));
        await MensagemAsync(db, amb.Cenario, conversa, contato, DirecaoMensagem.Entrada, Local(Quinta, 9, 25));
        await MensagemAsync(db, amb.Cenario, conversa, contato, DirecaoMensagem.Saida, Local(Quinta, 9, 30));

        var serie = await amb.Serie.ObterAsync(Quinta, Quinta, AgrupamentoSerie.Dia, default);

        // 30 minutos desde a PRIMEIRA. Se as três contassem, a média seria (30+10+5)/3 = 15.
        Assert.Equal(30m, serie.Pontos[0].TempoRespostaMinutos);
    }

    [Fact]
    public async Task Conversa_sem_resposta_nao_entra_na_media()
    {
        // Entrar como zero premiaria quem não respondeu; entrar como "infinito" envenenaria a
        // média. Fica de fora, e o período sem nenhuma resposta medida devolve NULL.
        var (db, tx, amb) = await PrepararAsync("sem-resposta");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb.Cenario, "mudo", Local(Quinta, 8));
        var conversa = await ConversaAsync(db, amb.Cenario, contato);
        await MensagemAsync(db, amb.Cenario, conversa, contato, DirecaoMensagem.Entrada, Local(Quinta, 9));

        var serie = await amb.Serie.ObterAsync(Quinta, Quinta, AgrupamentoSerie.Dia, default);

        Assert.Null(serie.Pontos[0].TempoRespostaMinutos);
    }

    [Fact]
    public async Task Agrupamento_por_semana_e_por_mes_junta_os_pontos()
    {
        var (db, tx, amb) = await PrepararAsync("agrupa");
        using var _ = db; using var __ = tx;

        // Três leads na mesma semana (quinta, sexta, sábado).
        await LeadAsync(db, amb.Cenario, "s1", Local(Quinta, 9));
        await LeadAsync(db, amb.Cenario, "s2", Local(Quinta.AddDays(1), 9));
        await LeadAsync(db, amb.Cenario, "s3", Local(Quinta.AddDays(2), 9));

        var semana = await amb.Serie.ObterAsync(Quinta, Quinta.AddDays(2), AgrupamentoSerie.Semana, default);
        Assert.Single(semana.Pontos);
        Assert.Equal(3, semana.Pontos[0].Leads);
        // date_trunc('week') cai na SEGUNDA — o ponto é o início do período, não a data pedida.
        Assert.Equal(new DateOnly(2026, 8, 3), semana.Pontos[0].Data);

        var mes = await amb.Serie.ObterAsync(Quinta, Quinta.AddDays(2), AgrupamentoSerie.Mes, default);
        Assert.Single(mes.Pontos);
        Assert.Equal(3, mes.Pontos[0].Leads);
        Assert.Equal(new DateOnly(2026, 8, 1), mes.Pontos[0].Data);
    }

    [Fact]
    public async Task A_serie_nao_atravessa_a_fronteira_do_tenant()
    {
        var (db, tx, amb) = await PrepararAsync("meu");
        using var _ = db; using var __ = tx;

        var outro = await Semeador.TenantAsync(db, "serie-outro");
        await LeadAsync(db, outro, "invasor", Local(Quinta, 9), ganhoEm: Local(Quinta, 17), valor: 9_999m);

        var serie = await amb.Serie.ObterAsync(Quinta, Quinta, AgrupamentoSerie.Dia, default);

        Assert.Equal(0, serie.Pontos[0].Vendas);
        Assert.Equal(0m, serie.Pontos[0].Faturamento);
    }

    // ==================================================================== atividades
    [Fact]
    public async Task VENDEDOR_NAO_VE_ATIVIDADE_DE_OUTRO_VENDEDOR_NEM_PELA_API_DIRETA()
    {
        // ===================== O TESTE QUE IMPORTA =====================
        // O recorte é da API. Se fosse da tela, a resposta HTTP já teria trazido o dado do outro
        // vendedor — bastaria abrir a aba de rede. Por isso o teste chama o SERVIÇO e ainda
        // tenta forçar o `responsavelId` do colega, que é o que um cliente hostil faria.
        // ==============================================================
        var (db, tx, amb) = await PrepararAsync("papeis");
        using var _ = db; using var __ = tx;

        var ana = await VendedorAsync(db, amb.Cenario, "ana");
        var bruno = await VendedorAsync(db, amb.Cenario, "bruno");

        await LeadAsync(db, amb.Cenario, "da-ana", Local(Quinta, 9), responsavelId: ana);
        await LeadAsync(db, amb.Cenario, "do-bruno", Local(Quinta, 10), responsavelId: bruno);
        await LeadAsync(db, amb.Cenario, "sem-dono", Local(Quinta, 11), responsavelId: null);

        // ---- Ana, vendedora ----
        amb.Contexto.UsuarioId = ana;
        amb.Contexto.Papel = "vendedor";

        var daAna = await amb.Atividades.ListarAsync(null, null, null, 50, default);
        var nomes = daAna.Itens.Select(a => a.ContatoNome).ToList();

        Assert.Contains(nomes, n => n.Contains("da-ana"));
        Assert.Contains(nomes, n => n.Contains("sem-dono"));   // sem dono é de todo mundo
        Assert.DoesNotContain(nomes, n => n.Contains("do-bruno"));

        // ---- Ana PEDINDO explicitamente as atividades do Bruno ----
        var forcando = await amb.Atividades.ListarAsync(null, null, bruno, 50, default);
        Assert.DoesNotContain(forcando.Itens, a => a.ContatoNome.Contains("do-bruno"));

        // ---- o dono vê tudo ----
        amb.Contexto.UsuarioId = amb.Cenario.Dono.Id;
        amb.Contexto.Papel = "dono";

        var doDono = await amb.Atividades.ListarAsync(null, null, null, 50, default);
        Assert.Contains(doDono.Itens, a => a.ContatoNome.Contains("do-bruno"));

        // ---- e o dono PODE filtrar por um vendedor ----
        var filtrado = await amb.Atividades.ListarAsync(null, null, bruno, 50, default);
        Assert.All(filtrado.Itens, a => Assert.Equal(bruno, a.ResponsavelId));
    }

    [Fact]
    public async Task O_feed_junta_os_quatro_tipos_de_evento()
    {
        var (db, tx, amb) = await PrepararAsync("tipos");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb.Cenario, "completo", Local(Quinta, 8),
            ganhoEm: Local(Quinta, 16), valor: 400m);
        var conversa = await ConversaAsync(db, amb.Cenario, contato);
        await MensagemAsync(db, amb.Cenario, conversa, contato, DirecaoMensagem.Entrada, Local(Quinta, 9));
        await LembreteConcluidoAsync(db, amb.Cenario, contato, Local(Quinta, 12));

        var feed = await amb.Atividades.ListarAsync(null, null, null, 50, default);
        var tipos = feed.Itens.Select(a => a.Tipo).Distinct().ToList();

        Assert.Contains("contato", tipos);
        Assert.Contains("mensagem", tipos);
        Assert.Contains("venda", tipos);
        Assert.Contains("lembrete", tipos);

        // Mais novo primeiro.
        var quandos = feed.Itens.Select(a => a.Quando).ToList();
        Assert.Equal(quandos.OrderByDescending(q => q), quandos);

        // A venda leva o valor; os outros não inventam número.
        Assert.Equal(400m, feed.Itens.Single(a => a.Tipo == "venda").Valor);
    }

    [Fact]
    public async Task O_cursor_avanca_sem_repetir_nem_pular()
    {
        var (db, tx, amb) = await PrepararAsync("cursor");
        using var _ = db; using var __ = tx;

        // 9 contatos em horas distintas: 9 eventos de tipo 'contato'.
        for (var i = 0; i < 9; i++)
            await LeadAsync(db, amb.Cenario, $"c{i}", Local(Quinta, 8 + i));

        var pagina1 = await amb.Atividades.ListarAsync(null, null, null, 4, default);
        Assert.Equal(4, pagina1.Itens.Count);
        Assert.True(pagina1.TemMais);

        var ultimo = pagina1.Itens[^1];
        var pagina2 = await amb.Atividades.ListarAsync(ultimo.Quando, ultimo.Chave, null, 4, default);

        Assert.Equal(4, pagina2.Itens.Count);
        // Nenhuma chave se repete entre as páginas — é o que o desempate por `tipo:id` garante
        // quando dois eventos caem no mesmo instante.
        Assert.Empty(pagina1.Itens.Select(a => a.Chave).Intersect(pagina2.Itens.Select(a => a.Chave)));
    }

    [Fact]
    public async Task O_feed_nao_atravessa_a_fronteira_do_tenant()
    {
        var (db, tx, amb) = await PrepararAsync("feed-meu");
        using var _ = db; using var __ = tx;

        var outro = await Semeador.TenantAsync(db, "feed-outro");
        await LeadAsync(db, outro, "invasor", Local(Quinta, 9));

        var feed = await amb.Atividades.ListarAsync(null, null, null, 50, default);
        Assert.DoesNotContain(feed.Itens, a => a.ContatoNome.Contains("invasor"));
    }

    // ==================================================================== plano e desempenho
    [Fact]
    public async Task O_FILTRO_PRESERVA_O_INDICE_E_A_AGREGACAO_ACONTECE_NO_BANCO()
    {
        // ===================== O QUE ESTE TESTE PROVA =====================
        // Com 30 mil mensagens espalhadas por ~14 meses, consultando 31 dias:
        //   1. o corte por faixa (`>= @a AND < @b`) usa ix_msg_serie;
        //   2. NÃO há varredura sequencial em `mensagens`;
        //   3. a consulta devolve 31 linhas (uma por dia), não 30 mil — a agregação é do banco.
        //
        // O item 2 é o que morre se alguém trocar o filtro por `date_trunc('day', criado_em) =
        // @dia`: o resultado continua certo e o plano vira Seq Scan. Sem este teste, a regressão
        // só apareceria como lentidão em produção.
        //
        // ⚠️ A CARGA É ESPALHADA POR UM ANO DE PROPÓSITO. A primeira versão deste teste
        // concentrava as 30 mil mensagens nos mesmos 31 dias consultados — e o planejador
        // escolhia Seq Scan, CORRETAMENTE: ler 100% da tabela por índice é mais caro que varrer.
        // O teste reprovava um comportamento certo. Índice só se prova onde ele serve: recorte
        // pequeno sobre base grande, que é a forma real da pergunta do dashboard.
        // ==================================================================
        var (db, tx, amb) = await PrepararAsync("plano");
        using var _ = db; using var __ = tx;

        var contato = await LeadAsync(db, amb.Cenario, "carga", Local(Quinta.AddDays(-400), 8));
        var conversa = await ConversaAsync(db, amb.Cenario, contato);
        // 30 mil mensagens de 20 em 20 minutos ≈ 416 dias.
        await CargaDeMensagensAsync(
            db, amb.Cenario, conversa, contato, Local(Quinta.AddDays(-400), 8), 30_000, passoMinutos: 20);

        // ANALYZE: sem estatística atualizada o planejador acha que a tabela tem 0 linha e
        // escolhe Seq Scan por engano — o teste reprovaria por culpa do setup.
        await db.Database.ExecuteSqlRawAsync("ANALYZE mensagens;");

        var plano = await ExplicarAsync(db, amb.Cenario.Id,
            Local(Quinta.AddDays(-30), 0), Local(Quinta.AddDays(1), 0));

        Assert.True(plano.Contains("ix_msg_serie"), $"O plano não usou o índice:\n{plano}");
        Assert.DoesNotContain("Seq Scan on mensagens", plano);

        // Três formatos reais do dashboard, medidos sobre a mesma carga. O número vai para o
        // relatório; o teste guarda só o teto.
        var medidas = new List<(string Forma, int Pontos, long Ms)>();

        foreach (var (forma, de, ate, modo) in new[]
        {
            ("30 dias por dia",    Quinta.AddDays(-30),  Quinta, AgrupamentoSerie.Dia),
            ("90 dias por semana", Quinta.AddDays(-90),  Quinta, AgrupamentoSerie.Semana),
            ("365 dias por mês",   Quinta.AddDays(-365), Quinta, AgrupamentoSerie.Mes)
        })
        {
            // Uma passada fora da conta: a primeira execução paga o plano e o cache frio, e
            // medi-la contaria custo que o usuário não paga em toda requisição.
            await amb.Serie.ObterAsync(de, ate, modo, default);

            var relogio = Stopwatch.StartNew();
            var s = await amb.Serie.ObterAsync(de, ate, modo, default);
            relogio.Stop();

            medidas.Add((forma, s.Pontos.Count, relogio.ElapsedMilliseconds));
        }

        var feedRelogio = Stopwatch.StartNew();
        var feed = await amb.Atividades.ListarAsync(null, null, null, 20, default);
        feedRelogio.Stop();
        medidas.Add(("feed de atividades (20)", feed.Itens.Count, feedRelogio.ElapsedMilliseconds));

        saida.WriteLine("=== 30.000 mensagens, ~416 dias ===");
        foreach (var m in medidas)
            saida.WriteLine($"{m.Forma,-26} {m.Pontos,4} pontos  {m.Ms,5} ms");

        Assert.Equal(31, medidas[0].Pontos);

        // Teto FOLGADO de propósito: o número medido vive no relatório, não aqui. Um limite
        // apertado num runner compartilhado vira teste intermitente, e teste intermitente é
        // pior que teste ausente — ensina a equipe a reexecutar até passar.
        foreach (var m in medidas)
            Assert.True(m.Ms < 5_000, $"{m.Forma} levou {m.Ms}ms.");
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto,
        IServicoSerie Serie, IServicoAtividades Atividades);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)>
        PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx, new RelogioFalso(QuintaDeManha));
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"serie-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        // O Semeador deixa um contato, uma conversa e uma mensagem. Todo teste aqui CONTA
        // coisas, então esse resto entraria na conta e faria os números baterem por acaso.
        await ZerarAsync(db, cenario.Id);

        return (db, tx, new Ambiente(cenario, ctx, new ServicoSerie(db, ctx), new ServicoAtividades(db, ctx)));
    }

    /// <summary>Ordem das exclusões: mensagens antes de lembretes e conversas, e contatos por
    /// último. `fk_mensagens_lembrete` e `fk_msg_contato` são RESTRICT — inverter a ordem dá
    /// violação de chave estrangeira, não cascata.</summary>
    private static async Task ZerarAsync(NexoraDbContext db, long empresaId)
    {
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Lembretes.IgnoreQueryFilters().Where(l => l.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync();
        await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Cria o contato e FIXA `criado_em` por UPDATE.
    ///
    /// O `InterceptorAuditoria` sobrescreve `CriadoEm` com o relógio em todo INSERT — é o que
    /// impede um caminho de escrita de esquecer a coluna. Aqui isso trabalha contra o teste, que
    /// precisa de datas espalhadas, então o valor é imposto depois.</summary>
    private static async Task<Contato> LeadAsync(
        NexoraDbContext db, Cenario c, string marca, DateTime criadoEm,
        DateTime? ganhoEm = null, decimal? valor = null, long? responsavelId = null)
    {
        var contato = new Contato
        {
            EmpresaId = c.Id,
            Nome = $"Contato {marca}",
            Telefone = $"5584{Random.Shared.NextInt64(900000000, 999999999)}",
            EtapaId = ganhoEm is null ? c.Etapas[0].Id : c.Etapas[^1].Id,
            ResponsavelId = responsavelId,
            OrdemKanban = 1000m,
            Valor = valor,
            GanhoEm = ganhoEm
        };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();

        await db.Contatos.IgnoreQueryFilters().Where(x => x.Id == contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CriadoEm, criadoEm));

        // O fixture carimba `ganho_em` direto, sem passar pelo `MarcarGanhoAsync`. Desde o NEG-1
        // quem responde por faturamento é `vendas` — a mesma reconciliação que os semeadores
        // fazem, pelo mesmo motivo.
        if (ganhoEm is not null) await ReconciliadorVendas.SincronizarAsync(db, default);

        db.ChangeTracker.Clear();
        return contato;
    }

    private static async Task<Conversa> ConversaAsync(NexoraDbContext db, Cenario c, Contato contato)
    {
        var conversa = new Conversa
        {
            EmpresaId = c.Id, ContatoId = contato.Id, ConexaoId = c.Conexao.Id,
            UltimaMensagemEm = DateTime.UtcNow
        };
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return conversa;
    }

    private static async Task MensagemAsync(
        NexoraDbContext db, Cenario c, Conversa conversa, Contato contato,
        DirecaoMensagem direcao, DateTime quando)
    {
        var entrada = direcao == DirecaoMensagem.Entrada;
        var m = new Mensagem
        {
            EmpresaId = c.Id, ConversaId = conversa.Id, ContatoId = contato.Id,
            ConexaoId = c.Conexao.Id, InstanceName = c.Conexao.InstanceName,
            Direcao = direcao, Texto = entrada ? "oi" : "ola",
            DataDisparo = entrada ? null : DateOnly.FromDateTime(quando),
            RecebidaEm = entrada ? quando : null,
            EnviadaEm = entrada ? null : quando
        };
        db.Mensagens.Add(m);
        await db.SaveChangesAsync();

        await db.Mensagens.IgnoreQueryFilters().Where(x => x.Id == m.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CriadoEm, quando));

        db.ChangeTracker.Clear();
    }

    private static async Task LembreteConcluidoAsync(
        NexoraDbContext db, Cenario c, Contato contato, DateTime quando)
    {
        var l = new Lembrete
        {
            EmpresaId = c.Id, ContatoId = contato.Id,
            Titulo = "Ligar de volta",
            Origem = OrigemLembrete.Manual,
            Status = StatusLembrete.Concluido,
            DataAlvo = DateOnly.FromDateTime(quando),
            ConcluidoEm = quando,
            ResponsavelId = c.Dono.Id
        };
        db.Lembretes.Add(l);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<long> VendedorAsync(NexoraDbContext db, Cenario c, string marca)
    {
        var u = new Usuario
        {
            EmpresaId = c.Id, Nome = $"Vendedor {marca}",
            Email = $"{marca}-{Guid.NewGuid():N}@exemplo.com",
            SenhaHash = Nexora.Core.Seguranca.HashSenha.Gerar("senha-de-teste-123"),
            Papel = PapelUsuario.Vendedor, Status = StatusUsuario.Ativo
        };
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return u.Id;
    }

    /// <summary>Carga em massa por `generate_series`: 30 mil INSERTs pelo EF levariam minutos e
    /// mediriam o EF, não o banco.</summary>
    private static async Task CargaDeMensagensAsync(
        NexoraDbContext db, Cenario c, Conversa conversa, Contato contato,
        DateTime inicio, int quantas, int passoMinutos = 1)
    {
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO mensagens (
                empresa_id, conversa_id, contato_id, conexao_id, instance_name,
                direcao, texto, data_disparo, reservado_em, enviada_em, recebida_em, criado_em)
            SELECT {0}, {1}, {2}, {3}, {4},
                   (CASE WHEN i % 2 = 0 THEN 'entrada' ELSE 'saida' END)::direcao_mensagem_enum,
                   'carga ' || i,
                   CASE WHEN i % 2 = 0 THEN NULL
                        ELSE ({5}::timestamptz + ((i * {7}) || ' minutes')::interval)::date END,
                   {5}::timestamptz + ((i * {7}) || ' minutes')::interval,
                   CASE WHEN i % 2 = 0 THEN NULL
                        ELSE {5}::timestamptz + ((i * {7}) || ' minutes')::interval END,
                   CASE WHEN i % 2 = 0 THEN {5}::timestamptz + ((i * {7}) || ' minutes')::interval
                        ELSE NULL END,
                   {5}::timestamptz + ((i * {7}) || ' minutes')::interval
              FROM generate_series(1, {6}) AS i
            """,
            c.Id, conversa.Id, contato.Id, c.Conexao.Id, c.Conexao.InstanceName, inicio, quantas,
            passoMinutos);
    }

    /// <summary>EXPLAIN do MESMO recorte que o serviço faz sobre `mensagens`.</summary>
    private static async Task<string> ExplicarAsync(
        NexoraDbContext db, long empresaId, DateTime de, DateTime ate)
    {
        var conexao = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conexao.State != System.Data.ConnectionState.Open) await conexao.OpenAsync();

        await using var cmd = new NpgsqlCommand("""
            EXPLAIN SELECT count(*) FROM mensagens
             WHERE empresa_id = $1 AND criado_em >= $2 AND criado_em < $3
            """, conexao)
        {
            Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction()
        };
        cmd.Parameters.Add(new() { Value = empresaId });
        cmd.Parameters.Add(new() { Value = de });
        cmd.Parameters.Add(new() { Value = ate });

        var linhas = new List<string>();
        await using var leitor = await cmd.ExecuteReaderAsync();
        while (await leitor.ReadAsync()) linhas.Add(leitor.GetString(0));

        return string.Join('\n', linhas);
    }
}
