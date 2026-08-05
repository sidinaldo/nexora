using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Api.Controllers;
using Nexora.Api.Seguranca;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>Cadastro de empresa e primeiros passos, contra Postgres real.
///
/// O cadastro é a única rota que cria TENANT, e é pública por natureza — não há sessão antes de
/// a empresa existir. Toda a proteção mora no controller, e é por isso que parte destes testes
/// lê o atributo em vez do serviço.</summary>
[Collection("banco")]
public class OnboardingDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ==================================================================== proteção do cadastro
    [Fact]
    public async Task CADASTRO_SEM_A_CHAVE_DE_ADMINISTRACAO_E_RECUSADO()
    {
        // ===================== A REGRA MAIS IMPORTANTE =====================
        // Esta rota cria tenant, usuário dono, conexão e cinco etapas de funil. Aberta na
        // internet, sem verificação de e-mail nem aceite de termos, seria convite para lixo —
        // e cada tenant falso arrasta todas essas linhas.
        //
        // A checagem vive no CONTROLLER (não há sessão para um filtro de autorização usar), e é
        // por isso que o teste exercita o controller diretamente.
        // ==================================================================
        var semChave = await ControladorCadastro("chave-secreta", chaveEnviada: null)
            .Criar(NovaEmpresaValida("sem-chave"), default);
        Assert.IsType<Microsoft.AspNetCore.Mvc.UnauthorizedObjectResult>(semChave);

        var chaveErrada = await ControladorCadastro("chave-secreta", "chave-errada")
            .Criar(NovaEmpresaValida("chave-errada"), default);
        Assert.IsType<Microsoft.AspNetCore.Mvc.UnauthorizedObjectResult>(chaveErrada);
    }

    [Fact]
    public async Task CHAVE_VAZIA_NA_CONFIGURACAO_DESLIGA_O_CADASTRO()
    {
        // O padrão é vazio. Um clone do repositório que alguém suba sem configurar nada NÃO
        // pode ficar com criação de conta aberta — mesma disciplina do segredo do webhook.
        var r = await ControladorCadastro(chaveConfigurada: "", chaveEnviada: "")
            .Criar(NovaEmpresaValida("desligado"), default);
        Assert.IsType<Microsoft.AspNetCore.Mvc.UnauthorizedObjectResult>(r);
    }

    [Fact]
    public void A_rota_de_cadastro_e_publica_e_tem_rate_limit_proprio()
    {
        var metodo = typeof(CadastroController).GetMethod(nameof(CadastroController.Criar))!;

        // Pública: não há sessão antes da empresa existir.
        Assert.NotEmpty(typeof(CadastroController)
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), true));

        // E com política própria — o teto do cadastro é bem mais baixo que o geral.
        var limite = metodo.GetCustomAttributes(typeof(EnableRateLimitingAttribute), true)
            .Cast<EnableRateLimitingAttribute>().Single();
        Assert.Equal(RateLimitingConfig.PolCadastro, limite.PolicyName);
    }

    [Fact]
    public void O_teto_do_cadastro_e_baixo_e_por_hora()
    {
        // Se a chave vazar, o estrago fica limitado a poucos tenants por hora por origem.
        var op = new OpcoesRateLimit();
        Assert.Equal(3, op.CadastroPorHora);
    }

    // ==================================================================== o que o cadastro cria
    [Fact]
    public async Task CADASTRO_CRIA_TENANT_DONO_CONEXAO_E_AS_CINCO_ETAPAS()
    {
        var (db, tx, servico) = await PrepararAsync();
        using var _ = db; using var __ = tx;

        var id = await servico.CadastrarAsync(
            new NovaEmpresa("Padaria Nova", "12345678000190", "Marina Alves",
                "marina@padarianova.com", "senha-forte-123"), default);

        db.ChangeTracker.Clear();

        var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal("Padaria Nova", empresa.Nome);
        Assert.Equal("12345678000190", empresa.Documento);   // máscara removida
        Assert.True(empresa.Ativo);

        var dono = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.EmpresaId == id);
        Assert.Equal(PapelUsuario.Dono, dono.Papel);
        Assert.Equal(StatusUsuario.Ativo, dono.Status);
        Assert.NotNull(dono.SenhaHash);

        var conexao = await db.Conexoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.EmpresaId == id);
        Assert.Equal($"emp-{id}", conexao.InstanceName);
        Assert.Equal(StatusConexao.NaoCriada, conexao.Status);

        // AS CINCO ETAPAS, na ordem, com UMA marcada como ganho. Sem elas o kanban não renderiza
        // e `Contato.EtapaId` é NOT NULL — a empresa nasceria quebrada.
        var etapas = await db.EtapasFunil.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.EmpresaId == id).OrderBy(e => e.Ordem).ToListAsync();

        Assert.Equal(5, etapas.Count);
        Assert.Equal([1, 2, 3, 4, 5], etapas.Select(e => (int)e.Ordem).ToArray());
        var ganho = Assert.Single(etapas.Where(e => e.EGanho));
        Assert.Equal("Venda", ganho.Nome);
        Assert.All(etapas, e => Assert.StartsWith("#", e.Cor));
    }

    [Fact]
    public async Task E_MAIL_JA_USADO_E_RECUSADO_COM_MENSAGEM_CLARA()
    {
        // O índice de e-mail é FUNCIONAL e GLOBAL (lower(email)). Sem esta checagem o INSERT
        // estouraria no índice, com erro ilegível para quem está cadastrando.
        var (db, tx, servico) = await PrepararAsync();
        using var _ = db; using var __ = tx;

        await servico.CadastrarAsync(
            new NovaEmpresa("Primeira", null, "Dono Um", "repetido@exemplo.com", "senha-forte-123"),
            default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.CadastrarAsync(
                new NovaEmpresa("Segunda", null, "Dono Dois", "REPETIDO@exemplo.com", "senha-forte-123"),
                default));

        Assert.True(erro.Conflito);
        Assert.Contains("Já existe usuário com este e-mail", erro.Message);
    }

    // ==================================================================== onboarding derivado
    [Fact]
    public async Task EMPRESA_NOVA_VE_OS_TRES_PASSOS_EM_ABERTO()
    {
        var (db, tx, amb) = await PrepararTenantAsync("nova");
        using var _ = db; using var __ = tx;

        await RecemNascidaAsync(db, amb.Cenario.Id);

        var o = await amb.Onboarding.ObterAsync(default);

        Assert.Equal(3, o.Total);
        Assert.Equal(0, o.Concluidos);
        Assert.False(o.Completo);
        Assert.True(o.Mostrar);
        Assert.All(o.Passos, p => Assert.False(p.Concluido));

        // O passo 3 NÃO tem rota nem ação: é espera. Um botão ali prometeria que há algo a
        // clicar, e não há — a mensagem tem que sair de um celular de verdade.
        var espera = o.Passos.Single(p => p.Chave == "primeira_mensagem");
        Assert.Null(espera.Rota);
        Assert.Null(espera.RotuloAcao);
    }

    [Fact]
    public async Task O_CHECKLIST_E_DERIVADO_DO_ESTADO_NAO_DE_UMA_FLAG()
    {
        // ===================== POR QUE ISSO IMPORTA =====================
        // Com flag de "já configurou", a empresa que teve o WhatsApp derrubado continuaria
        // vendo "tudo pronto" enquanto nada chega. Derivado, o passo volta a acender sozinho.
        // ===============================================================
        var (db, tx, amb) = await PrepararTenantAsync("derivado");
        using var _ = db; using var __ = tx;

        // O Semeador já entrega a conexão conectada.
        Assert.True((await amb.Onboarding.ObterAsync(default))
            .Passos.Single(p => p.Chave == "conexao").Concluido);

        // A conexão CAI. Sem nenhuma outra ação, o passo volta a ficar em aberto.
        await db.Conexoes.IgnoreQueryFilters().Where(c => c.EmpresaId == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, StatusConexao.Desconectado));
        db.ChangeTracker.Clear();
        Assert.False((await amb.Onboarding.ObterAsync(default))
            .Passos.Single(p => p.Chave == "conexao").Concluido);
    }

    [Fact]
    public async Task Convidar_alguem_conclui_o_passo_da_equipe()
    {
        var (db, tx, amb) = await PrepararTenantAsync("equipe");
        using var _ = db; using var __ = tx;

        Assert.False((await amb.Onboarding.ObterAsync(default))
            .Passos.Single(p => p.Chave == "equipe").Concluido);

        // Convite pendente JÁ CONTA: o dono fez a parte dele quando mandou.
        await amb.Equipe.ConvidarAsync(new NovoConvite("Novo", "novo@exemplo.com", "vendedor"), default);
        db.ChangeTracker.Clear();

        Assert.True((await amb.Onboarding.ObterAsync(default))
            .Passos.Single(p => p.Chave == "equipe").Concluido);
    }

    [Fact]
    public async Task A_PRIMEIRA_MENSAGEM_CONCLUI_O_PASSO_3_E_REGISTRA_O_TEMPO_ATE_O_VALOR()
    {
        var (db, tx, amb) = await PrepararTenantAsync("tempo");
        using var _ = db; using var __ = tx;

        // A empresa foi criada duas horas atrás.
        var cadastro = QuintaDeManha.UtcDateTime.AddHours(-2);
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.CriadoEm, cadastro)
                .SetProperty(e => e.PrimeiraMensagemEm, QuintaDeManha.UtcDateTime.AddMinutes(-30)));
        db.ChangeTracker.Clear();

        var o = await amb.Onboarding.ObterAsync(default);

        Assert.True(o.Passos.Single(p => p.Chave == "primeira_mensagem").Concluido);
        // 2h de cadastro menos 30min antes de agora = 90 minutos até o valor.
        Assert.Equal(90, o.MinutosAteAPrimeiraMensagem);
    }

    [Fact]
    public async Task PASSO_3_OLHA_A_MENSAGEM_DE_ENTRADA_NAO_A_COLUNA()
    {
        // ===================== O CASO QUE QUEBRAVA =====================
        // `primeira_mensagem_em` só passou a existir na migration deste bloco, e o webhook só
        // carimba dali em diante. Toda empresa que JÁ recebia mensagem antes disso tem a coluna
        // NULL — e o passo 3 ficaria aceso para sempre numa conta em plena operação.
        //
        // O passo é "existe ao menos uma mensagem de entrada", e é isso que ele pergunta. A
        // coluna é atalho de leitura e métrica, nunca a verdade.
        // ==============================================================
        var (db, tx, amb) = await PrepararTenantAsync("msg-sem-coluna");
        using var _ = db; using var __ = tx;

        // Zera primeiro: o Semeador já deixa uma mensagem de entrada, e sem isto o teste passaria
        // pela mensagem DELE, não pela que interessa — provando nada.
        await RecemNascidaAsync(db, amb.Cenario.Id);
        await MensagemDeEntradaAsync(db, amb.Cenario, QuintaDeManha.UtcDateTime.AddDays(-14));

        // A coluna continua NULL de propósito: é exatamente o estado de quem veio de antes.
        Assert.Null((await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == amb.Cenario.Id)).PrimeiraMensagemEm);

        Assert.True((await amb.Onboarding.ObterAsync(default))
            .Passos.Single(p => p.Chave == "primeira_mensagem").Concluido);
    }

    [Fact]
    public async Task Mensagem_de_SAIDA_nao_conclui_o_passo_3()
    {
        // Mensagem que o próprio vendedor mandou não prova que o produto funcionou — prova que
        // ele digitou. O passo 3 é sobre o cliente chegar.
        var (db, tx, amb) = await PrepararTenantAsync("so-saida");
        using var _ = db; using var __ = tx;

        await RecemNascidaAsync(db, amb.Cenario.Id);
        await MensagemDeEntradaAsync(db, amb.Cenario, QuintaDeManha.UtcDateTime.AddDays(-1),
            direcao: DirecaoMensagem.Saida);

        Assert.False((await amb.Onboarding.ObterAsync(default))
            .Passos.Single(p => p.Chave == "primeira_mensagem").Concluido);
    }

    [Fact]
    public async Task Sem_primeira_mensagem_a_metrica_e_NULL_e_nao_zero()
    {
        // Zero diria "chegou na hora do cadastro", que é diferente de "não chegou". A métrica
        // é para olhar; um zero falso a envenenaria.
        var (db, tx, amb) = await PrepararTenantAsync("sem-metrica");
        using var _ = db; using var __ = tx;

        Assert.Null((await amb.Onboarding.ObterAsync(default)).MinutosAteAPrimeiraMensagem);
    }

    // ==================================================================== pular
    [Fact]
    public async Task PULAR_A_EQUIPE_RESOLVE_O_PASSO_SEM_CUMPRI_LO()
    {
        var (db, tx, amb) = await PrepararTenantAsync("pular-equipe");
        using var _ = db; using var __ = tx;

        await amb.Onboarding.DispensarEquipeAsync(default);
        db.ChangeTracker.Clear();

        var passo = (await amb.Onboarding.ObterAsync(default)).Passos.Single(p => p.Chave == "equipe");
        Assert.False(passo.Concluido);    // não foi cumprido
        Assert.True(passo.Dispensado);    // mas está resolvido
    }

    [Fact]
    public async Task FECHAR_O_PAINEL_TIRA_A_TELA_MESMO_COM_PASSO_EM_ABERTO()
    {
        // Onboarding que prende o usuário irrita mais do que ajuda.
        var (db, tx, amb) = await PrepararTenantAsync("fechar");
        using var _ = db; using var __ = tx;

        Assert.True((await amb.Onboarding.ObterAsync(default)).Mostrar);

        await amb.Onboarding.DispensarAsync(default);
        db.ChangeTracker.Clear();

        var o = await amb.Onboarding.ObterAsync(default);
        Assert.True(o.Dispensado);
        Assert.False(o.Mostrar);
        Assert.False(o.Completo);   // e continua honesto sobre o que falta
    }

    [Fact]
    public async Task Dispensar_duas_vezes_preserva_a_data_da_PRIMEIRA_decisao()
    {
        // Recarregar a tela e clicar de novo não pode reescrever o instante — a métrica de
        // onboarding perderia sentido.
        var (db, tx, amb) = await PrepararTenantAsync("dispensar-2x");
        using var _ = db; using var __ = tx;

        await amb.Onboarding.DispensarAsync(default);
        db.ChangeTracker.Clear();
        var primeira = (await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == amb.Cenario.Id)).OnboardingDispensadoEm;

        amb.Relogio.Avancar(TimeSpan.FromHours(3));
        await amb.Onboarding.DispensarAsync(default);
        db.ChangeTracker.Clear();

        var depois = (await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == amb.Cenario.Id)).OnboardingDispensadoEm;

        Assert.Equal(primeira, depois);
    }

    [Fact]
    public async Task Empresa_com_tudo_pronto_nao_ve_a_tela()
    {
        var (db, tx, amb) = await PrepararTenantAsync("pronta");
        using var _ = db; using var __ = tx;

        await db.Conexoes.IgnoreQueryFilters().Where(c => c.EmpresaId == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, StatusConexao.Conectado));
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PrimeiraMensagemEm, QuintaDeManha.UtcDateTime));
        db.ChangeTracker.Clear();
        await amb.Onboarding.DispensarEquipeAsync(default);
        db.ChangeTracker.Clear();

        var o = await amb.Onboarding.ObterAsync(default);
        Assert.True(o.Completo);
        Assert.False(o.Mostrar);   // completo -> não mostra, sem ninguém ter fechado nada
    }

    [Fact]
    public void Dispensar_e_so_do_dono_e_LER_e_de_qualquer_papel()
    {
        var tipo = typeof(OnboardingController);

        foreach (var m in new[] { nameof(OnboardingController.DispensarEquipe),
                                  nameof(OnboardingController.Dispensar) })
        {
            var a = tipo.GetMethod(m)!.GetCustomAttributes(typeof(AuthorizeAttribute), true)
                .Cast<AuthorizeAttribute>().Single();
            Assert.Equal("dono", a.Roles);
        }

        // GET sem restrição de papel: o vendedor numa conta recém-criada também merece saber
        // que o WhatsApp ainda não foi conectado.
        Assert.Empty(tipo.GetMethod(nameof(OnboardingController.Obter))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), true));
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto, RelogioFalso Relogio,
        IServicoOnboarding Onboarding, IServicoEquipe Equipe);

    private static CadastroController ControladorCadastro(
        string chaveConfigurada, string? chaveEnviada)
    {
        var controller = new CadastroController(
            new CadastroQueNuncaDeveriaSerChamado(),
            new OpcoesCadastro { ChaveAdministracao = chaveConfigurada },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CadastroController>.Instance)
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

    /// <summary>Se a chave barrar como deve, o serviço nunca é chamado — e se for, o teste
    /// quebra alto em vez de passar por engano.</summary>
    private sealed class CadastroQueNuncaDeveriaSerChamado : IServicoCadastroEmpresa
    {
        public Task<long> CadastrarAsync(NovaEmpresa nova, CancellationToken ct) =>
            throw new InvalidOperationException(
                "A chave de administração deveria ter barrado antes de chegar aqui.");
    }

    /// <summary>Devolve o tenant ao estado de quem ACABOU de se cadastrar: conexão não criada e
    /// nenhuma mensagem.
    ///
    /// O `Semeador` monta deliberadamente um tenant EM OPERAÇÃO — conexão conectada e uma
    /// mensagem de entrada —, que é o oposto do cenário de primeiros passos. Sem zerar, todo
    /// teste de checklist mede o semeador em vez do produto.</summary>
    private static async Task RecemNascidaAsync(NexoraDbContext db, long empresaId)
    {
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == empresaId)
            .ExecuteDeleteAsync();
        await db.Conexoes.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, StatusConexao.NaoCriada));
        db.ChangeTracker.Clear();
    }

    /// <summary>Contato + conversa + uma mensagem, direto no banco. Não passa pelo webhook de
    /// propósito: o cenário que interessa é justamente a linha que existe SEM ninguém ter
    /// carimbado `primeira_mensagem_em`.</summary>
    private static async Task MensagemDeEntradaAsync(
        NexoraDbContext db, Cenario c, DateTime quando,
        DirecaoMensagem direcao = DirecaoMensagem.Entrada)
    {
        var contato = new Contato
        {
            EmpresaId = c.Id, Nome = "Cliente antigo",
            Telefone = $"55849{Random.Shared.Next(10_000_000, 99_999_999)}",
            EtapaId = c.PrimeiraEtapa.Id
        };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();

        var conversa = new Conversa
        {
            EmpresaId = c.Id, ContatoId = contato.Id, ConexaoId = c.Conexao.Id,
            UltimaMensagemEm = quando
        };
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();

        var entrada = direcao == DirecaoMensagem.Entrada;
        db.Mensagens.Add(new Mensagem
        {
            EmpresaId = c.Id, ConversaId = conversa.Id, ContatoId = contato.Id,
            ConexaoId = c.Conexao.Id, InstanceName = c.Conexao.InstanceName,
            Direcao = direcao, Texto = "oi",
            // ck_msg_data_disparo: só saída exige data_disparo.
            DataDisparo = entrada ? null : DateOnly.FromDateTime(quando),
            RecebidaEm = entrada ? quando : null,
            EnviadaEm = entrada ? null : quando,
            CriadoEm = quando
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static NovaEmpresa NovaEmpresaValida(string sufixo) =>
        new($"Empresa {sufixo}", null, "Dono", $"dono-{sufixo}@exemplo.com", "senha-forte-123");

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, IServicoCadastroEmpresa Servico)>
        PrepararAsync()
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx, new RelogioFalso(QuintaDeManha));
        var tx = await db.Database.BeginTransactionAsync();
        return (db, tx, new ServicoCadastroEmpresa(db));
    }

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)>
        PrepararTenantAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"onb-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new Ambiente(
            cenario, ctx, relogio,
            new ServicoOnboarding(db, relogio),
            new ServicoEquipe(db, ctx, relogio, new NotificadorEmailFalso(), new FilaSegundoPlanoFalsa())));
    }
}
