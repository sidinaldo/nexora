using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Api.Controllers;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O tenant de demonstração: as três barreiras de segurança e a coerência do que o seed
/// escreve direto no banco.
///
/// As barreiras vêm primeiro, e não é ordem alfabética: o que elas impedem é o tenant de
/// demonstração mandar WhatsApp para números de estranhos.</summary>
[Collection("banco")]
public class DemonstracaoDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ==================================================================== barreira 1: a faixa
    [Fact]
    public void BARREIRA_1_A_FAIXA_USA_UM_DDD_QUE_NAO_EXISTE()
    {
        // ===================== POR QUE DDD 00 =====================
        // Os DDDs brasileiros vão de 11 a 99. `00` não existe e não pode passar a existir sem uma
        // renumeração do plano nacional inteiro — então `5500...@s.whatsapp.net` não corresponde
        // a conta nenhuma, hoje nem depois.
        //
        // A alternativa comum (um prefixo "não alocado" dentro de um DDD real) é mais bonita na
        // tela e pior aqui: blocos de numeração são alocados o tempo todo, e ninguém iria
        // reavaliar esta escolha antes de a faixa virar o celular de alguém.
        // ==========================================================
        Assert.Equal("5500", TelefoneDemonstracao.Prefixo);

        var numero = TelefoneDemonstracao.Numero(1);

        Assert.StartsWith("5500", numero);
        Assert.Equal(13, numero.Length);
        // DDD extraído do canônico: posições 2 e 3.
        Assert.Equal("00", numero[2..4]);

        // Passa pela validação do cadastro: o seed grava pelo mesmo caminho de qualquer contato,
        // sem exceção para si mesmo.
        Assert.True(CanonicalizadorTelefone.EhValido(numero));
    }

    [Fact]
    public void A_faixa_e_reconhecida_e_nao_pega_numero_de_verdade()
    {
        Assert.True(TelefoneDemonstracao.EhDemonstracao("5500900000042"));
        Assert.True(TelefoneDemonstracao.EhDemonstracao("+55 (00) 90000-0042"));   // com máscara

        // Números plausíveis de verdade NÃO podem cair na faixa — um falso positivo aqui
        // bloquearia envio de cliente pagante.
        Assert.False(TelefoneDemonstracao.EhDemonstracao("5584988887777"));
        Assert.False(TelefoneDemonstracao.EhDemonstracao("5511999998888"));
        Assert.False(TelefoneDemonstracao.EhDemonstracao(null));
        Assert.False(TelefoneDemonstracao.EhDemonstracao(""));
    }

    // ==================================================================== barreira 2: a rodada
    [Fact]
    public async Task BARREIRA_2_EMPRESA_DE_DEMONSTRACAO_E_IGNORADA_PELO_MOTOR()
    {
        // ===================== O QUE ISTO IMPEDE =====================
        // Tenant de demonstração tem contato com telefone e conversa parada — exatamente o que a
        // regra de elegibilidade do follow-up procura. Se ele entrasse na rodada e a empresa
        // estivesse pareada a uma Evolution real, a rodada mandaria mensagem para os números
        // semeados.
        // ============================================================
        var (db, tx, dados) = await PrepararAsync();
        using var _ = db; using var __ = tx;

        var comum = await Semeador.TenantAsync(db, "motor-comum");
        var demo = await Semeador.TenantAsync(db, "motor-demo");

        await MarcarDemonstracaoAsync(db, demo.Id);

        var empresas = await dados.EmpresasAtivasAsync(default);
        var ids = empresas.Select(e => e.Id).ToList();

        Assert.Contains(comum.Id, ids);
        Assert.DoesNotContain(demo.Id, ids);
    }

    [Fact]
    public async Task Empresa_de_demonstracao_INATIVA_tambem_fica_de_fora()
    {
        // As duas condições são independentes (`Ativo && !Demonstracao`); este teste impede que
        // alguém "simplifique" para um OR e reabra a porta.
        var (db, tx, dados) = await PrepararAsync();
        using var _ = db; using var __ = tx;

        var demo = await Semeador.TenantAsync(db, "motor-demo-inativa");
        await MarcarDemonstracaoAsync(db, demo.Id);
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == demo.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Ativo, false));
        db.ChangeTracker.Clear();

        Assert.DoesNotContain(demo.Id, (await dados.EmpresasAtivasAsync(default)).Select(e => e.Id));
    }

    // ==================================================================== barreira 3: o envio
    [Fact]
    public async Task BARREIRA_3_O_ENVIADOR_RECUSA_DISPARO_DE_TENANT_DE_DEMONSTRACAO()
    {
        // A ÚLTIMA barreira, no ponto onde todo envio passa. Mesmo que a empresa entrasse na
        // rodada (barreira 2 furada) e o número fosse real (barreira 1 furada), o disparo para
        // aqui.
        var (db, tx, _) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var demo = await Semeador.TenantAsync(db, "envio-demo");
        await MarcarDemonstracaoAsync(db, demo.Id);

        var whatsapp = new ClienteWhatsAppFalso();
        var enviador = MontarEnviador(db, whatsapp);

        var mensagem = NovaMensagem(demo);

        // Telefone REAL de propósito: quem barra aqui é a marca da EMPRESA, não a faixa.
        var (id, resultado) = await enviador.EnviarManualAsync(mensagem, "5584988887777", default);

        Assert.Equal(ResultadoEnvio.Falhou, resultado);
        Assert.Empty(whatsapp.TextosEnviados);   // o disparo NÃO chegou à Evolution

        // A linha FICA, com o motivo gravado: é o mesmo protocolo de qualquer falha de entrega, e
        // a tela já sabe mostrar "não entregue". Apagar liberaria a invariante de dedupe.
        db.ChangeTracker.Clear();
        var gravada = await db.Mensagens.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == id);
        Assert.Equal(EnviadorMensagem.MotivoDemonstracao, gravada.Erro);
        Assert.Null(gravada.EnviadaEm);
    }

    [Fact]
    public async Task O_enviador_recusa_pelo_NUMERO_mesmo_em_tenant_comum()
    {
        // O caso inverso da barreira 3: contato de demonstração que acabou num tenant normal
        // (importação, cópia de base, engano). As duas checagens são independentes e cobrem
        // falhas diferentes.
        var (db, tx, _) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var comum = await Semeador.TenantAsync(db, "envio-comum");

        var whatsapp = new ClienteWhatsAppFalso();
        var enviador = MontarEnviador(db, whatsapp);

        var (_, resultado) = await enviador.EnviarManualAsync(
            NovaMensagem(comum), TelefoneDemonstracao.Numero(7), default);

        Assert.Equal(ResultadoEnvio.Falhou, resultado);
        Assert.Empty(whatsapp.TextosEnviados);
    }

    [Fact]
    public async Task Tenant_comum_com_numero_comum_continua_enviando()
    {
        // A prova de que as barreiras não pegam quem não devem: sem este teste, uma checagem
        // ampla demais bloquearia cliente pagante e ninguém notaria até ele reclamar.
        var (db, tx, _) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var comum = await Semeador.TenantAsync(db, "envio-ok");
        var whatsapp = new ClienteWhatsAppFalso { IdParaDevolver = "WA-OK-1" };
        var enviador = MontarEnviador(db, whatsapp);

        var (id, resultado) = await enviador.EnviarManualAsync(
            NovaMensagem(comum), "5584988887777", default);

        Assert.Equal(ResultadoEnvio.Enviada, resultado);
        Assert.Single(whatsapp.TextosEnviados);

        db.ChangeTracker.Clear();
        Assert.NotNull((await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.Id == id)).EnviadaEm);
    }

    // ==================================================================== o seed
    [Fact]
    public async Task SEED_RODADO_DUAS_VEZES_NAO_DUPLICA()
    {
        var (db, tx, _) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var seed = MontarSeed(db);

        var primeira = await seed.SemearAsync(default);
        var segunda = await seed.SemearAsync(default);

        // MESMA empresa: recriar mudaria o id a cada execução e qualquer link salvo quebraria.
        Assert.Equal(primeira.EmpresaId, segunda.EmpresaId);
        Assert.Equal(primeira.Contatos, segunda.Contatos);
        Assert.Equal(primeira.Conversas, segunda.Conversas);
        Assert.Equal(primeira.Mensagens, segunda.Mensagens);

        db.ChangeTracker.Clear();
        Assert.Equal(segunda.Contatos, await db.Contatos.IgnoreQueryFilters()
            .CountAsync(c => c.EmpresaId == segunda.EmpresaId));
        Assert.Equal(1, await db.Empresas.IgnoreQueryFilters().CountAsync(e => e.Demonstracao));
    }

    [Fact]
    public async Task O_SEED_MARCA_A_EMPRESA_E_USA_SO_A_FAIXA_RESERVADA()
    {
        var (db, tx, _) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var resumo = await MontarSeed(db).SemearAsync(default);
        db.ChangeTracker.Clear();

        Assert.True((await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == resumo.EmpresaId)).Demonstracao);

        // NENHUM telefone fora da faixa. Um só já seria mensagem para um estranho.
        var telefones = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EmpresaId == resumo.EmpresaId)
            .Select(c => c.Telefone).ToListAsync();

        Assert.NotEmpty(telefones);
        Assert.All(telefones, t => Assert.True(
            TelefoneDemonstracao.EhDemonstracao(t), $"Telefone fora da faixa reservada: {t}"));
    }

    [Fact]
    public async Task AGUARDANDO_DESDE_E_NAO_LIDAS_COERENTES_COM_A_ULTIMA_MENSAGEM()
    {
        // ===================== POR QUE ISTO IMPORTA =====================
        // O seed escreve direto no banco, então as invariantes que os serviços mantêm não vêm de
        // graça. Estado incoerente aqui faz a tela mostrar algo que o produto real nunca
        // produziria — e alguém vai depurar um bug que não existe.
        // ===============================================================
        var (db, tx, _) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var resumo = await MontarSeed(db).SemearAsync(default);
        db.ChangeTracker.Clear();

        var conversas = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EmpresaId == resumo.EmpresaId).ToListAsync();

        Assert.NotEmpty(conversas);

        foreach (var conversa in conversas)
        {
            var mensagens = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.ConversaId == conversa.Id)
                .OrderBy(m => m.CriadoEm).ThenBy(m => m.Id)
                .ToListAsync();

            Assert.NotEmpty(mensagens);
            var ultima = mensagens[^1];

            // (1) ultima_mensagem_* batendo com a última mensagem de verdade
            Assert.Equal(ultima.Direcao, conversa.UltimaMensagemDirecao);
            Assert.Equal(ultima.Texto, conversa.UltimaMensagemPrevia);

            // (2) aguardando_desde: tem valor se a última foi de ENTRADA, NULL se foi de saída
            var indiceUltimaSaida = mensagens.FindLastIndex(m => m.Direcao == DirecaoMensagem.Saida);
            var pendentes = mensagens.Skip(indiceUltimaSaida + 1)
                .Where(m => m.Direcao == DirecaoMensagem.Entrada).ToList();

            if (ultima.Direcao == DirecaoMensagem.Saida)
                Assert.Null(conversa.AguardandoDesde);
            else
                Assert.Equal(pendentes[0].RecebidaEm, conversa.AguardandoDesde);

            // (3) nao_lidas = entradas depois da última saída
            Assert.Equal(pendentes.Count, conversa.NaoLidas);
        }
    }

    [Fact]
    public async Task NENHUM_CONTATO_GANHO_E_PERDIDO_AO_MESMO_TEMPO()
    {
        // `ck_contatos_terminal` barraria no banco — este teste existe para a falha ser um
        // assert legível em vez de uma violação de constraint no meio do seed.
        var (db, tx, _) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var resumo = await MontarSeed(db).SemearAsync(default);
        db.ChangeTracker.Clear();

        Assert.Empty(await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EmpresaId == resumo.EmpresaId && c.GanhoEm != null && c.PerdidoEm != null)
            .ToListAsync());

        // E existem os dois lados: sem perdido a conversão é 100%, sem ganho é 0%.
        Assert.True(resumo.Ganhos > 0);
        Assert.True(resumo.Perdidos > 0);
    }

    [Fact]
    public async Task ORDEM_KANBAN_SEM_COLISAO_DENTRO_DA_ETAPA()
    {
        var (db, tx, _) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var resumo = await MontarSeed(db).SemearAsync(default);
        db.ChangeTracker.Clear();

        var porEtapa = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EmpresaId == resumo.EmpresaId)
            .Select(c => new { c.EtapaId, c.OrdemKanban })
            .ToListAsync();

        foreach (var grupo in porEtapa.GroupBy(c => c.EtapaId))
            Assert.Equal(grupo.Count(), grupo.Select(c => c.OrdemKanban).Distinct().Count());
    }

    [Fact]
    public async Task A_TAXA_DE_CONVERSAO_NAO_E_ZERO_NEM_CEM_POR_CENTO()
    {
        // Uma demonstração com conversão de 100% não convence ninguém, e com 0% assusta. O
        // dashboard conta ganhos ÷ (ganhos + perdidos) DO MÊS, então o seed precisa ter os dois
        // dentro do mês corrente — não só em algum lugar dos últimos 6 meses.
        var (db, tx, ctx) = await PrepararComContextoAsync();
        using var _1 = db; using var _2 = tx;

        var resumo = await MontarSeed(db).SemearAsync(default);
        ctx.EmpresaId = resumo.EmpresaId;
        db.ChangeTracker.Clear();

        var dashboard = await new ServicoDashboard(db, new RelogioFalso(QuintaDeManha))
            .DashboardAsync(default);

        Assert.True(dashboard.TaxaConversao > 0, "Conversão zerada: faltou ganho no mês.");
        Assert.True(dashboard.TaxaConversao < 1, "Conversão de 100%: faltou perdido no mês.");
    }

    [Fact]
    public async Task A_ROSCA_DE_ORIGENS_TEM_FATIAS_E_A_SOMA_FECHA()
    {
        // A rosca só conta história com VÁRIAS origens: com uma fatia só ela vira um círculo
        // liso, e o cartão passa a ocupar espaço sem informar nada.
        var (db, tx, ctx) = await PrepararComContextoAsync();
        using var _1 = db; using var _2 = tx;

        var resumo = await MontarSeed(db).SemearAsync(default);
        ctx.EmpresaId = resumo.EmpresaId;
        db.ChangeTracker.Clear();

        var dashboard = await new ServicoDashboard(db, new RelogioFalso(QuintaDeManha))
            .DashboardAsync(default);

        Assert.True(dashboard.Origens.Count >= 5,
            $"Só {dashboard.Origens.Count} origens: a rosca fica sem forma.");

        // A soma das fatias É o total de contatos não anonimizados — se não fechasse, a rosca
        // desenharia percentuais que não somam 100% e ninguém confiaria no cartão.
        var esperado = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(c => c.EmpresaId == resumo.EmpresaId && c.AnonimizadoEm == null);

        Assert.Equal(esperado, dashboard.Origens.Sum(o => o.Leads));

        // Maior primeiro: a legenda lê de cima para baixo.
        Assert.Equal(dashboard.Origens.OrderByDescending(o => o.Leads).Select(o => o.Leads),
            dashboard.Origens.Select(o => o.Leads));

        // Minúsculas, como todo enum desta API — o cliente compara com 'instagram', não 'Instagram'.
        Assert.All(dashboard.Origens, o => Assert.Equal(o.Origem.ToLowerInvariant(), o.Origem));
    }

    [Fact]
    public async Task O_FUNIL_AFUNILA_E_A_ROSCA_TEM_FATIAS_DESIGUAIS()
    {
        // ===================== POR QUE ISTO É TESTE, E NÃO GOSTO =====================
        // A primeira distribuição do seed era redonda demais: 10 contatos em cada etapa aberta e
        // as 9 origens em rodízio. O resultado passava em todos os testes de coerência e não
        // mostrava NADA na tela — o funil saía um retângulo (com a etapa de venda mais larga que
        // o topo) e a rosca, nove fatias idênticas. Gráfico sem forma parece defeito.
        //
        // Estes dois asserts fixam a FORMA, que é o que o dado de demonstração existe para ter.
        // ============================================================================
        var (db, tx, ctx) = await PrepararComContextoAsync();
        using var _1 = db; using var _2 = tx;

        var resumo = await MontarSeed(db).SemearAsync(default);
        ctx.EmpresaId = resumo.EmpresaId;
        db.ChangeTracker.Clear();

        var d = await new ServicoDashboard(db, new RelogioFalso(QuintaDeManha)).DashboardAsync(default);

        // O funil AFUNILA nas etapas abertas: cada uma tem menos que a anterior.
        var abertas = d.Funil.Where(e => !e.Nome.Equals("Venda", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Ordem).Select(e => e.Contatos).ToList();

        for (var i = 1; i < abertas.Count; i++)
            Assert.True(abertas[i] < abertas[i - 1],
                $"O funil não afunila: {string.Join(" → ", abertas)}");

        // A rosca tem fatias DESIGUAIS: a maior vale pelo menos o dobro da menor.
        var leads = d.Origens.Select(o => o.Leads).ToList();
        Assert.True(leads.Max() >= leads.Min() * 2,
            $"Fatias uniformes demais ({string.Join(", ", leads)}): a rosca não informa nada.");
    }

    [Fact]
    public async Task Contato_anonimizado_nao_entra_na_rosca()
    {
        // Ele foi apagado a pedido do titular. Contá-lo como lead de um canal manteria o rastro
        // que a anonimização existe para remover.
        var (db, tx, ctx) = await PrepararComContextoAsync();
        using var _1 = db; using var _2 = tx;

        var resumo = await MontarSeed(db).SemearAsync(default);
        ctx.EmpresaId = resumo.EmpresaId;

        var servico = new ServicoDashboard(db, new RelogioFalso(QuintaDeManha));
        var antes = (await servico.DashboardAsync(default)).Origens.Sum(o => o.Leads);

        var alvo = await db.Contatos.IgnoreQueryFilters()
            .FirstAsync(c => c.EmpresaId == resumo.EmpresaId);
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == alvo.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AnonimizadoEm, QuintaDeManha.UtcDateTime));
        db.ChangeTracker.Clear();

        Assert.Equal(antes - 1, (await servico.DashboardAsync(default)).Origens.Sum(o => o.Leads));
    }

    [Fact]
    public async Task O_SEED_E_DETERMINISTICO()
    {
        // Semente fixa: captura de tela reproduzível e teste estável. Sem isso, um teste que
        // afirma "12 ganhos" quebraria a cada execução.
        var (db, tx, _) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var seed = MontarSeed(db);
        var a = await seed.SemearAsync(default);
        var b = await seed.SemearAsync(default);

        Assert.Equal(a.Ganhos, b.Ganhos);
        Assert.Equal(a.Perdidos, b.Perdidos);
        Assert.Equal(a.Lembretes, b.Lembretes);
    }

    // ==================================================================== a guarda do comando
    [Fact]
    public void O_SEED_NAO_RODA_SEM_A_GUARDA_LIGADA()
    {
        // ===================== DUAS TRAVAS INDEPENDENTES =====================
        // 1. `Demonstracao:Habilitado` — falso por padrão. Em produção ninguém liga.
        // 2. a mesma chave de administração do cadastro de empresa.
        //
        // O padrão do POCO é o que vale num clone limpo do repositório: se ele nascesse `true`,
        // qualquer implantação que esquecesse de configurar teria o comando aberto.
        // ====================================================================
        Assert.False(new OpcoesDemonstracao().Habilitado);
    }

    [Fact]
    public async Task Sem_a_guarda_o_comando_devolve_404_e_nem_chega_ao_seed()
    {
        // 404 e não 403: 403 confirmaria que a rota existe para quem estivesse sondando.
        var controller = MontarController(habilitado: false, chave: "chave", chaveEnviada: "chave");
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Semear(default));
    }

    [Fact]
    public async Task Com_a_guarda_ligada_mas_sem_a_chave_devolve_401()
    {
        var semChave = MontarController(habilitado: true, chave: "chave-secreta", chaveEnviada: null);
        Assert.IsType<Microsoft.AspNetCore.Mvc.UnauthorizedObjectResult>(await semChave.Semear(default));

        var errada = MontarController(habilitado: true, chave: "chave-secreta", chaveEnviada: "outra");
        Assert.IsType<Microsoft.AspNetCore.Mvc.UnauthorizedObjectResult>(await errada.Semear(default));

        // Chave vazia na configuração DESLIGA, mesmo com a guarda de ambiente ligada.
        var vazia = MontarController(habilitado: true, chave: "", chaveEnviada: "");
        Assert.IsType<Microsoft.AspNetCore.Mvc.UnauthorizedObjectResult>(await vazia.Semear(default));
    }

    [Fact]
    public void A_rota_de_seed_e_publica_e_tem_rate_limit()
    {
        // Pública porque roda antes de existir sessão em qualquer tenant — quem opera é quem tem
        // a chave. Sem `[Authorize]`, o rate limit é o que resta contra tentativa em rajada.
        Assert.NotEmpty(typeof(DemonstracaoController)
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), true));

        var limite = typeof(DemonstracaoController)
            .GetMethod(nameof(DemonstracaoController.Semear))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), true)
            .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()
            .Single();

        Assert.Equal(Nexora.Api.Seguranca.RateLimitingConfig.PolCadastro, limite.PolicyName);
    }

    // ==================================================================== apoio
    private static Mensagem NovaMensagem(Cenario c) => new()
    {
        EmpresaId = c.Id,
        ConversaId = c.Conversa.Id,
        ContatoId = c.Contato.Id,
        ConexaoId = c.Conexao.Id,
        InstanceName = c.Conexao.InstanceName,
        Direcao = DirecaoMensagem.Saida,
        Texto = "teste",
        DataDisparo = DateOnly.FromDateTime(QuintaDeManha.UtcDateTime)
    };

    private static EnviadorMensagem MontarEnviador(NexoraDbContext db, IClienteWhatsApp whatsapp)
    {
        var relogio = new RelogioFalso(QuintaDeManha);
        return new EnviadorMensagem(
            new DadosMensagem(db, relogio), whatsapp,
            new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero },
            relogio, NullLogger<EnviadorMensagem>.Instance);
    }

    private static IServicoSeedDemonstracao MontarSeed(NexoraDbContext db) =>
        new ServicoSeedDemonstracao(
            db, new ServicoCadastroEmpresa(db), new RelogioFalso(QuintaDeManha),
            NullLogger<ServicoSeedDemonstracao>.Instance);

    private static DemonstracaoController MontarController(
        bool habilitado, string chave, string? chaveEnviada)
    {
        var controller = new DemonstracaoController(
            new SeedQueNuncaDeveriaSerChamado(),
            new OpcoesDemonstracao { Habilitado = habilitado },
            new OpcoesCadastro { ChaveAdministracao = chave },
            NullLogger<DemonstracaoController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            }
        };

        if (chaveEnviada is not null)
            controller.Request.Headers[CadastroController.CabecalhoChave] = chaveEnviada;

        return controller;
    }

    /// <summary>Se a guarda barrar como deve, o seed nunca é chamado — e se for, o teste quebra
    /// alto em vez de passar por engano.</summary>
    private sealed class SeedQueNuncaDeveriaSerChamado : IServicoSeedDemonstracao
    {
        public Task<ResumoSeedDemonstracao> SemearAsync(CancellationToken ct) =>
            throw new InvalidOperationException("A guarda deveria ter barrado antes de chegar aqui.");
    }

    private static async Task MarcarDemonstracaoAsync(NexoraDbContext db, long empresaId)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == empresaId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Demonstracao, true));
        db.ChangeTracker.Clear();
    }

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, DadosFollowUp Dados)>
        PrepararAsync()
    {
        var (db, tx, _) = await PrepararComContextoAsync();
        return (db, tx, new DadosFollowUp(db, new RelogioFalso(QuintaDeManha)));
    }

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, ContextoMutavel Ctx)>
        PrepararComContextoAsync()
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx, new RelogioFalso(QuintaDeManha));
        var tx = await db.Database.BeginTransactionAsync();
        return (db, tx, ctx);
    }
}
