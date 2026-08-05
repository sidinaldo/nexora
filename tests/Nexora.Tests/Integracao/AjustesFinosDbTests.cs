using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core;
using Nexora.Core.Email;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>Os cinco ajustes do PI-5 que tocam o banco. Cada um é independente dos outros.</summary>
[Collection("banco")]
public class AjustesFinosDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ==================================================================== 1. fuso editável
    [Fact]
    public async Task FUSO_INVALIDO_E_RECUSADO_E_NAO_CAI_EM_FALLBACK_SILENCIOSO()
    {
        // ===================== O QUE ISTO PROTEGE =====================
        // `FusoDeNegocio.Resolver` cai em UTC-3 quando o id não existe — e cai calado. É o certo
        // LÁ (container sem tzdata não pode derrubar o agendador) e seria péssimo AQUI: a empresa
        // de Manaus salvaria um id com erro de digitação, a tela mostraria o valor salvo, e a
        // rodada dispararia uma hora errada para sempre, sem nada no log.
        // ==============================================================
        var (db, tx, amb) = await PrepararAsync("fuso");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Config.AtualizarDadosAsync(
                new EditarDadosEmpresa("Padaria", null, "America/Sao_Paulo_ERRADO", null), default));

        Assert.Contains("não existe neste servidor", erro.Message);

        // E o VALOR ANTIGO continua no banco: recusar não pode ter gravado metade.
        db.ChangeTracker.Clear();
        var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == amb.Cenario.Id);
        Assert.Equal("America/Sao_Paulo", empresa.FusoHorario);

        // A prova de que o fallback ENGOLIRIA o erro se a validação não existisse.
        Assert.Equal(TimeSpan.FromHours(-3),
            FusoDeNegocio.Resolver("America/Sao_Paulo_ERRADO").GetUtcOffset(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Fuso_valido_e_gravado_e_UF_normalizada()
    {
        var (db, tx, amb) = await PrepararAsync("fuso-ok");
        using var _ = db; using var __ = tx;

        await amb.Config.AtualizarDadosAsync(
            new EditarDadosEmpresa("Padaria", null, "America/Manaus", "rn"), default);

        db.ChangeTracker.Clear();
        var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == amb.Cenario.Id);

        Assert.Equal("America/Manaus", empresa.FusoHorario);
        Assert.Equal("RN", empresa.Uf);   // maiúscula, sem depender de quem digitou
    }

    [Fact]
    public async Task UF_invalida_e_recusada_e_vazia_vira_nula()
    {
        var (db, tx, amb) = await PrepararAsync("uf");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Config.AtualizarDadosAsync(
                new EditarDadosEmpresa("Padaria", null, "America/Sao_Paulo", "XX"), default));

        // Vazio é legítimo: empresa sem UF recebe só os feriados nacionais.
        await amb.Config.AtualizarDadosAsync(
            new EditarDadosEmpresa("Padaria", null, "America/Sao_Paulo", "  "), default);

        db.ChangeTracker.Clear();
        Assert.Null((await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == amb.Cenario.Id)).Uf);
    }

    [Fact]
    public async Task Os_fusos_oferecidos_existem_de_verdade_no_host()
    {
        // Oferecer um id que o servidor não conhece levaria o dono direto ao erro de validação,
        // por culpa nossa.
        var (db, tx, amb) = await PrepararAsync("fusos-lista");
        using var _ = db; using var __ = tx;

        var fusos = amb.Config.FusosDisponiveis();

        Assert.NotEmpty(fusos);
        foreach (var f in fusos)
        {
            var resolvido = TimeZoneInfo.FindSystemTimeZoneById(f.Id);   // lança se não existir
            Assert.StartsWith("UTC", f.OffsetAtual);
            Assert.NotEqual("", resolvido.Id);
        }
    }

    // ==================================================================== 2. feriados estaduais
    [Fact]
    public async Task SEED_ESTADUAL_E_IDEMPOTENTE_E_NAO_FALHA_COM_UF_SEM_CADASTRO()
    {
        var (db, tx, amb) = await PrepararAsync("estaduais");
        using var _ = db; using var __ = tx;

        // RN tem cadastro; SP ainda não. As duas empresas convivem, e o seed roda uma vez só.
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Uf, "RN"));

        var outra = await Semeador.TenantAsync(db, "estaduais-sp");
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == outra.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Uf, "SP"));
        db.ChangeTracker.Clear();

        // UF SEM CADASTRO NÃO PODE DERRUBAR O SEED — se derrubasse, nem os NACIONAIS entrariam,
        // trocando uma lacuna pequena por uma grande.
        await amb.Feriados.GarantirAtualEProximoAsync(default);

        var estaduais = await db.Feriados.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Abrangencia == AbrangenciaFeriado.Estadual)
            .ToListAsync();

        Assert.Contains(estaduais, f => f.Uf == "RN" && f.Data == new DateOnly(2026, 10, 3));
        Assert.DoesNotContain(estaduais, f => f.Uf == "SP");
        // Os nacionais entraram apesar da UF sem cadastro.
        Assert.NotEmpty(await db.Feriados.IgnoreQueryFilters()
            .Where(f => f.Abrangencia == AbrangenciaFeriado.Nacional).ToListAsync());

        // ===== IDEMPOTÊNCIA: roda todo dia no boot do agendador =====
        var antes = await db.Feriados.IgnoreQueryFilters().CountAsync();
        await amb.Feriados.GarantirAtualEProximoAsync(default);
        await amb.Feriados.GarantirAtualEProximoAsync(default);
        var depois = await db.Feriados.IgnoreQueryFilters().CountAsync();

        Assert.Equal(antes, depois);
    }

    [Fact]
    public async Task Estadual_e_nacional_na_mesma_data_convivem()
    {
        // `uq_feriados` inclui COALESCE(uf,''), então um estadual não colide com o nacional do
        // mesmo dia. Sem isso, o ON CONFLICT DO NOTHING descartaria um dos dois em silêncio.
        var (db, tx, amb) = await PrepararAsync("colisao");
        using var _ = db; using var __ = tx;

        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Uf, "RN"));
        db.ChangeTracker.Clear();

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO feriados (empresa_id, data, nome, abrangencia, uf, criado_em) " +
            "VALUES (NULL, {0}, 'Nacional inventado', 'nacional'::abrangencia_feriado_enum, NULL, now()) " +
            "ON CONFLICT DO NOTHING", [new DateOnly(2026, 10, 3)]);

        await amb.Feriados.GarantirAtualEProximoAsync(default);

        var noDia = await db.Feriados.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Data == new DateOnly(2026, 10, 3)).ToListAsync();

        Assert.Equal(2, noDia.Count);
    }

    [Fact]
    public void UF_sem_cadastro_devolve_lista_vazia_e_nunca_lanca()
    {
        foreach (var uf in ConfiguracaoRef.Ufs)
        {
            var lista = CalculadoraFeriados.Estaduais(2026, uf);   // não pode lançar
            if (!CalculadoraFeriados.UfsConhecidas.Contains(uf))
                Assert.Empty(lista);
        }

        Assert.Empty(CalculadoraFeriados.Estaduais(2026, null));
        Assert.Empty(CalculadoraFeriados.Estaduais(2026, "ZZ"));
    }

    // ==================================================================== 3. concorrência
    [Fact]
    public async Task MOVER_CARD_COM_VERSAO_DESATUALIZADA_DEVOLVE_409()
    {
        // ===================== O CASO REAL =====================
        // Dois vendedores com o quadro aberto. O primeiro arrasta; o segundo, que ainda vê o
        // estado antigo, arrasta em seguida. Antes disto o segundo VENCIA, calado — e o primeiro
        // via seu card em outro lugar no próximo carregamento, sem explicação.
        // =======================================================
        var (db, tx, amb) = await PrepararAsync("concorrencia");
        using var _ = db; using var __ = tx;

        var quadro = await amb.Funil.QuadroAsync(50, default);
        var card = quadro.Colunas.SelectMany(c => c.Contatos).Single(c => c.Id == amb.Cenario.Contato.Id);
        var versaoQueOsDoisViram = card.Versao;

        var segundaEtapa = amb.Cenario.Etapas[1].Id;

        // O primeiro vendedor move.
        await amb.Funil.MoverAsync(card.Id, new MoverContato(segundaEtapa, null, versaoQueOsDoisViram), default);
        db.ChangeTracker.Clear();

        // O segundo tenta mover com a versão VELHA.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Funil.MoverAsync(card.Id, new MoverContato(segundaEtapa, null, versaoQueOsDoisViram), default));

        Assert.True(erro.Conflito, "O conflito precisa virar 409 — é o que a tela já sabe tratar.");
        Assert.Contains("Outra pessoa moveu", erro.Message);
    }

    [Fact]
    public async Task Mover_com_a_versao_atual_passa_e_a_versao_muda_depois()
    {
        var (db, tx, amb) = await PrepararAsync("versao-ok");
        using var _ = db; using var __ = tx;

        var card = (await amb.Funil.QuadroAsync(50, default))
            .Colunas.SelectMany(c => c.Contatos).Single();

        await amb.Funil.MoverAsync(
            card.Id, new MoverContato(amb.Cenario.Etapas[1].Id, null, card.Versao), default);
        db.ChangeTracker.Clear();

        var depois = (await amb.Funil.QuadroAsync(50, default))
            .Colunas.SelectMany(c => c.Contatos).Single();

        // O `xmin` mudou sozinho — é o Postgres que o mantém, ninguém precisou incrementar nada.
        Assert.NotEqual(card.Versao, depois.Versao);
    }

    [Fact]
    public async Task Mover_SEM_versao_continua_funcionando()
    {
        // `MarcarGanhoAsync` e outros caminhos que movem o card não vêm de arrasto e não têm
        // versão para mandar. Exigir sempre quebraria a porta única do ganho.
        var (db, tx, amb) = await PrepararAsync("sem-versao");
        using var _ = db; using var __ = tx;

        var card = (await amb.Funil.QuadroAsync(50, default))
            .Colunas.SelectMany(c => c.Contatos).Single();

        await amb.Funil.MoverAsync(card.Id, new MoverContato(amb.Cenario.Etapas[1].Id, null), default);

        db.ChangeTracker.Clear();
        Assert.Equal(amb.Cenario.Etapas[1].Id,
            (await db.Contatos.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == card.Id)).EtapaId);
    }

    // ==================================================================== 4. timing do reset
    [Fact]
    public async Task ESQUECI_MINHA_SENHA_GASTA_TEMPO_COMPARAVEL_NOS_DOIS_CAMINHOS()
    {
        // ===================== O QUE ESTE TESTE PROTEGE =====================
        // O corpo da resposta já era idêntico exista o e-mail ou não. O TEMPO não era: o caminho
        // com conta gera token, grava e notifica, enquanto o sem conta saía na hora. A diferença
        // é medível de fora e transforma o endpoint num verificador de contas mais lento — o
        // rate limit de 3/15min por IP reduz, não elimina.
        //
        // A proteção é um PISO de tempo, e o teste mede as duas coisas que ele precisa garantir:
        // que os DOIS caminhos ficam acima do piso, e que a diferença entre eles é pequena.
        // ==================================================================
        var (db, tx, amb) = await PrepararAsync("timing");
        using var _ = db; using var __ = tx;

        var existente = amb.Cenario.Dono.Email;
        const string inexistente = "ninguem-com-esse-endereco@exemplo.com";

        // Aquecimento: a PRIMEIRA execução paga JIT e o cache de plano do EF.
        await amb.Equipe.SolicitarResetSenhaAsync(existente, default);
        await amb.Equipe.SolicitarResetSenhaAsync(inexistente, default);

        var comConta = await MedirAsync(() => amb.Equipe.SolicitarResetSenhaAsync(existente, default));
        var semConta = await MedirAsync(() => amb.Equipe.SolicitarResetSenhaAsync(inexistente, default));

        var piso = ServicoEquipe.PisoDeTempoReset.TotalMilliseconds;

        // (1) Os dois pagam o piso. É ISTO que morre se alguém remover a proteção: sem ela o
        //     caminho sem conta volta a sair em poucos milissegundos.
        //     Margem de 10% para o `Task.Delay`, que garante o mínimo mas não é exato.
        Assert.True(semConta >= piso * 0.9,
            $"O caminho SEM conta levou {semConta}ms, abaixo do piso de {piso}ms — " +
            "dá para enumerar contas pelo tempo de resposta.");
        Assert.True(comConta >= piso * 0.9,
            $"O caminho COM conta levou {comConta}ms, abaixo do piso de {piso}ms.");

        // (2) E a diferença entre eles é pequena perto do piso. Não se exige igualdade
        //     cronométrica — isso não existe num runner compartilhado.
        Assert.True(Math.Abs(comConta - semConta) < piso * 0.5,
            $"A diferença entre os caminhos ({comConta}ms vs {semConta}ms) é grande demais " +
            $"perto do piso de {piso}ms.");
    }

    [Fact]
    public async Task O_reset_nao_revela_nada_pelo_retorno_nem_por_excecao()
    {
        var (db, tx, amb) = await PrepararAsync("reset-mudo");
        using var _ = db; using var __ = tx;

        // Nenhum dos dois lança, e os dois devolvem `Task` sem valor: quem chama não tem como
        // distinguir. É a mesma disciplina do login.
        await amb.Equipe.SolicitarResetSenhaAsync("nao-existe@exemplo.com", default);
        await amb.Equipe.SolicitarResetSenhaAsync(amb.Cenario.Dono.Email, default);

        // Só o e-mail que EXISTE gerou trabalho — enfileirar para todo mundo "por simetria"
        // mandaria mensagem para endereço que não é de ninguém. A simetria é de TEMPO.
        Assert.Equal(1, amb.Fila.Enfileirados);

        // E, quando a fila drena, sai exatamente uma notificação de reset.
        await amb.Fila.ExecutarPendentesAsync(new ProvedorFalso(amb.Email));
        Assert.Single(amb.Email.Chamadas, c => c.Tipo == "reset");
    }

    [Fact]
    public async Task COM_REMETENTE_LENTO_O_TEMPO_CONTINUA_IGUAL_NOS_DOIS_CAMINHOS()
    {
        // ===================== O RESÍDUO QUE ISTO FECHA =====================
        // O piso de 250 ms igualava a diferença entre gravar um token e não gravar nada. Não
        // igualava o SMTP: o envio acontecia DENTRO da requisição, e num relay lento o caminho
        // COM conta estourava o piso enquanto o SEM conta continuava em 250 ms.
        //
        // A assimetria voltava — mais lenta, mas ainda mensurável de fora, e ainda suficiente
        // para enumerar contas devagar.
        //
        // Agora o envio vai para a fila de segundo plano. A prova é este teste: com um remetente
        // que dorme 2 segundos, os DOIS caminhos continuam no piso.
        // ====================================================================
        var (db, tx, amb) = await PrepararAsync("timing-lento");
        using var _ = db; using var __ = tx;

        var lento = new NotificadorEmailLento(TimeSpan.FromSeconds(2));
        var fila = new FilaSegundoPlanoFalsa();
        var equipe = new ServicoEquipe(db, amb.Contexto, new RelogioFalso(QuintaDeManha), lento, fila);

        var existente = amb.Cenario.Dono.Email;
        const string inexistente = "ninguem-com-esse-endereco@exemplo.com";

        await equipe.SolicitarResetSenhaAsync(existente, default);      // aquecimento
        await equipe.SolicitarResetSenhaAsync(inexistente, default);

        var comConta = await MedirAsync(() => equipe.SolicitarResetSenhaAsync(existente, default));
        var semConta = await MedirAsync(() => equipe.SolicitarResetSenhaAsync(inexistente, default));

        var piso = ServicoEquipe.PisoDeTempoReset.TotalMilliseconds;

        // Antes da correção, `comConta` seria ~2250 ms contra ~250 ms — uma diferença que
        // qualquer um mede com um cronômetro.
        Assert.True(comConta < piso * 2,
            $"O caminho COM conta levou {comConta}ms: o envio ainda está na requisição.");
        Assert.True(Math.Abs(comConta - semConta) < piso * 0.5,
            $"Diferença grande demais entre os caminhos ({comConta}ms vs {semConta}ms).");

        // E o e-mail NÃO foi perdido: ficou enfileirado, e sai quando a fila drenar.
        Assert.True(fila.Enfileirados > 0, "O envio sumiu em vez de ir para a fila.");

        var enfileirados = fila.Enfileirados;
        await fila.ExecutarPendentesAsync(new ProvedorFalso(lento));

        // Um envio por chamada com conta (aquecimento + as duas medições), nenhum perdido.
        Assert.Equal(enfileirados, lento.Chamadas.Count);
        Assert.All(lento.Chamadas, c => Assert.Equal("reset", c.Tipo));
        Assert.All(lento.Chamadas, c => Assert.Equal(existente, c.Email));
    }

    [Fact]
    public async Task E_mail_inexistente_NAO_enfileira_nada()
    {
        // Enfileirar para todo mundo "para ficar simétrico" mandaria e-mail para endereço que
        // não é de ninguém — o oposto de proteger. A simetria é de TEMPO, não de trabalho.
        var (db, tx, amb) = await PrepararAsync("fila-vazia");
        using var _ = db; using var __ = tx;

        var fila = new FilaSegundoPlanoFalsa();
        var equipe = new ServicoEquipe(
            db, amb.Contexto, new RelogioFalso(QuintaDeManha), new NotificadorEmailFalso(), fila);

        await equipe.SolicitarResetSenhaAsync("nao-existe@exemplo.com", default);

        Assert.Equal(0, fila.Enfileirados);
    }

    /// <summary>Remetente que dorme — simula o relay lento que reabria a janela de timing.</summary>
    private sealed class NotificadorEmailLento(TimeSpan atraso) : INotificadorEmail
    {
        public List<(string Tipo, string Email)> Chamadas { get; } = [];

        public Task ConviteAsync(long e, string email, string n, string en, string t, CancellationToken ct) =>
            Registrar("convite", email, ct);

        public Task ResetSenhaAsync(long? e, string email, string n, string t, CancellationToken ct) =>
            Registrar("reset", email, ct);

        public Task SenhaAlteradaAsync(long e, string email, string n, CancellationToken ct) =>
            Registrar("senha-alterada", email, ct);

        private async Task Registrar(string tipo, string email, CancellationToken ct)
        {
            await Task.Delay(atraso, ct);
            Chamadas.Add((tipo, email));
        }
    }

    private static async Task<long> MedirAsync(Func<Task> acao)
    {
        // Melhor de duas: um pico de GC ou de escalonamento numa única medição faria o teste
        // piscar. Duas bastam porque o piso torna a medida estável por construção.
        var medidas = new List<long>();
        for (var i = 0; i < 2; i++)
        {
            var relogio = Stopwatch.StartNew();
            await acao();
            relogio.Stop();
            medidas.Add(relogio.ElapsedMilliseconds);
        }
        return medidas.Min();
    }

    // ==================================================================== 5. janela de espera
    [Fact]
    public async Task ESPERA_ACIMA_DE_30_DIAS_DEVOLVE_MARCADOR_E_NAO_NUMERO()
    {
        // ===================== POR QUE NÃO UM NÚMERO =====================
        // Os feriados carregados cobrem 30 dias. Para uma espera mais velha, o cálculo sairia
        // SEM descontar os feriados anteriores ao recorte: maior que o real, e com cara de exato.
        // Quem lê "12.480 minutos úteis" acredita.
        // ================================================================
        var (db, tx, amb) = await PrepararAsync("espera-longa");
        using var _ = db; using var __ = tx;

        await ConversaEsperandoAsync(db, amb.Cenario, QuintaDeManha.UtcDateTime.AddDays(-45));

        var dia = await amb.MeuDia.MeuDiaAsync(default);
        var acao = dia.Acoes.Single(a => a.Tipo == "responder");

        Assert.True(acao.EsperaAcimaDaJanela);
        Assert.Null(acao.MinutosUteis);
    }

    [Fact]
    public async Task Espera_DENTRO_da_janela_continua_devolvendo_numero()
    {
        var (db, tx, amb) = await PrepararAsync("espera-curta");
        using var _ = db; using var __ = tx;

        // "Agora" é quinta 10h30 LOCAL (13h30 UTC, fuso -3). Três horas antes é 07h30 — meia hora
        // ANTES de a janela abrir às 8h.
        await ConversaEsperandoAsync(db, amb.Cenario, QuintaDeManha.UtcDateTime.AddHours(-3));

        var acao = (await amb.MeuDia.MeuDiaAsync(default)).Acoes.Single(a => a.Tipo == "responder");

        Assert.False(acao.EsperaAcimaDaJanela);
        // 150, não 180: a meia hora antes da abertura não conta. É o desconto de tempo útil
        // funcionando, e vale mais como caso de teste do que uma espera inteiramente dentro da
        // janela — que não exercitaria nada.
        Assert.Equal(150, acao.MinutosUteis);
    }

    [Fact]
    public async Task A_fronteira_da_janela_e_o_limite_declarado()
    {
        // Fixa o limite no valor da constante em vez de num 30 literal: se alguém mudar
        // `JanelaDeEspera.Dias`, o teste acompanha em vez de virar mentira.
        var (db, tx, amb) = await PrepararAsync("fronteira");
        using var _ = db; using var __ = tx;

        await ConversaEsperandoAsync(
            db, amb.Cenario, QuintaDeManha.UtcDateTime.AddDays(-(JanelaDeEspera.Dias + 1)));

        Assert.True((await amb.MeuDia.MeuDiaAsync(default))
            .Acoes.Single(a => a.Tipo == "responder").EsperaAcimaDaJanela);
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto,
        IServicoConfiguracao Config, IServicoFeriados Feriados,
        IServicoFunil Funil, IServicoMeuDia MeuDia,
        IServicoEquipe Equipe, NotificadorEmailFalso Email, FilaSegundoPlanoFalsa Fila);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)>
        PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"pi5-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var email = new NotificadorEmailFalso();
        var fila = new FilaSegundoPlanoFalsa();

        return (db, tx, new Ambiente(
            cenario, ctx,
            new ServicoConfiguracao(db),
            new ServicoFeriados(db, ctx, relogio, NullLogger<ServicoFeriados>.Instance),
            new ServicoFunil(db),
            new ServicoMeuDia(db, ctx, relogio),
            new ServicoEquipe(db, ctx, relogio, email, fila),
            email, fila));
    }

    /// <summary>Põe a conversa do cenário esperando resposta desde `desde`.</summary>
    private static async Task ConversaEsperandoAsync(NexoraDbContext db, Cenario c, DateTime desde)
    {
        await db.Conversas.IgnoreQueryFilters().Where(v => v.Id == c.Conversa.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.AguardandoDesde, desde)
                .SetProperty(v => v.Status, StatusConversa.Aberta)
                .SetProperty(v => v.ResponsavelId, (long?)c.Dono.Id));
        db.ChangeTracker.Clear();
    }
}
