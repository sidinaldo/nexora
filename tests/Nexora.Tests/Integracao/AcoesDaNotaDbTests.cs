using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== NPS-1 ETAPA 2e — O QUE ACONTECE DEPOIS DA NOTA =====================
///
/// Tres faixas: promotor (9-10) agradece, neutro (7-8) nao faz nada, detrator (0-6) gera TRABALHO
/// HUMANO e, opcionalmente, responde.
///
/// ⚠️ A SUITE ENTRA PELA LEITURA, e nao chamando `AcoesDaNota` direto, nos testes que importam. E a
/// unica forma de provar o que o prompt pede de verdade: a nota do cliente produz a acao, uma vez.
/// ==========================================================================================</summary>
[Collection("banco")]
public class AcoesDaNotaDbTests(BancoTeste banco)
{
    private static readonly DateOnly Hoje = new(2026, 8, 6);
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 0, 0, TimeSpan.Zero);

    // ==================================================================== o detrator

    /// <summary>===================== O DETRATOR GERA TRABALHO HUMANO =====================
    ///
    /// Critério de aceite do prompt: "Detrator gera lembrete para o responsavel e aviso para o
    /// dono". Os dois sao lembrete, porque o MEU DIA e onde trabalho aparece neste produto — e nao
    /// existe outro mecanismo: `INotificadorPainel` tem cinco eventos de tempo real e nenhum de
    /// alerta.
    ///
    /// ⚠️ `EnviaMensagem = false` NOS DOIS, e importa duas vezes: e TAREFA (alguem tem de LIGAR), e
    /// `uq_lembrete_teto_diario` cobre so `automatico AND envia_mensagem` — com `true`, um
    /// follow-up do mesmo dia barraria este em silencio e o aviso se perderia.
    /// ========================================================================</summary>
    [Fact]
    public async Task O_DETRATOR_GERA_LEMBRETE_PARA_O_VENDEDOR_E_PARA_O_DONO()
    {
        var (db, tx, amb) = await PrepararAsync("detrator");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "ana");
        await PesquisaEnviadaAsync(db, amb, responsavel: vendedor.Id);
        var mensagem = await EntradaAsync(db, amb, "3");

        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, "3", null, default);

        db.ChangeTracker.Clear();
        var lembretes = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ToListAsync();

        Assert.Equal(2, lembretes.Count);
        Assert.Equal(
            new[] { amb.Cenario.Dono.Id, vendedor.Id }.Order(),
            lembretes.Select(l => l.ResponsavelId!.Value).Order());

        Assert.All(lembretes, l =>
        {
            Assert.False(l.EnviaMensagem);
            Assert.Equal(OrigemLembrete.Automatico, l.Origem);
            Assert.Equal(StatusLembrete.Pendente, l.Status);
            // ⚠️ PARA HOJE: cliente insatisfeito e o unico caso deste produto em que esperar um dia
            // muda o resultado.
            Assert.Equal(Hoje, l.DataAlvo);
            Assert.Contains("3", l.Titulo);
        });
    }

    /// <summary>⚠️ UM LEMBRETE SO QUANDO O DONO E QUEM VENDEU — o caso comum na empresa pequena.
    /// Dois identicos na mesma lista nao avisam duas vezes: avisam que o sistema nao sabe quem e
    /// quem.</summary>
    [Fact]
    public async Task DONO_QUE_VENDEU_RECEBE_UM_LEMBRETE_SO()
    {
        var (db, tx, amb) = await PrepararAsync("dono-vendeu");
        using var _ = db; using var __ = tx;

        await PesquisaEnviadaAsync(db, amb, responsavel: amb.Cenario.Dono.Id);
        var mensagem = await EntradaAsync(db, amb, "2");

        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, "2", null, default);

        db.ChangeTracker.Clear();
        var lembrete = Assert.Single(await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ToListAsync());

        Assert.Equal(amb.Cenario.Dono.Id, lembrete.ResponsavelId);
    }

    /// <summary>Venda sem responsavel: o dono recebe sozinho. O aviso tem de aparecer no Meu Dia de
    /// ALGUEM — lembrete que ninguem ve e pior que nenhum, porque da a impressao de que avisou.</summary>
    [Fact]
    public async Task VENDA_SEM_RESPONSAVEL_AVISA_SO_O_DONO()
    {
        var (db, tx, amb) = await PrepararAsync("sem-responsavel");
        using var _ = db; using var __ = tx;

        await PesquisaEnviadaAsync(db, amb, responsavel: null);
        var mensagem = await EntradaAsync(db, amb, "0");

        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, "0", null, default);

        db.ChangeTracker.Clear();
        var lembrete = Assert.Single(await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ToListAsync());

        Assert.Equal(amb.Cenario.Dono.Id, lembrete.ResponsavelId);
    }

    /// <summary>⚠️ ZERO E DETRATOR, e e o caso extremo que nao pode se perder. `if (nota)` em vez de
    /// `if (nota != null)` o trataria como ausencia de nota — e o cliente mais insatisfeito de todos
    /// nao geraria aviso nenhum.</summary>
    [Fact]
    public async Task NOTA_ZERO_GERA_OS_AVISOS()
    {
        var (db, tx, amb) = await PrepararAsync("zero");
        using var _ = db; using var __ = tx;

        var vendedor = await VendedorAsync(db, amb, "ana");
        await PesquisaEnviadaAsync(db, amb, responsavel: vendedor.Id);
        var mensagem = await EntradaAsync(db, amb, "0");

        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, "0", null, default);

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == amb.Cenario.Id).CountAsync());
    }

    // ==================================================================== as faixas

    /// <summary>===================== AS TRES FAIXAS, UMA TABELA =====================
    ///
    /// ⚠️ 6 E DETRATOR E 7 E NEUTRO: as bordas da definicao do NPS, e e por isso que estao as duas
    /// aqui. Um `&lt;` no lugar de `&lt;=` moveria a faixa inteira em silencio.
    /// ==================================================================</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    public async Task SO_O_DETRATOR_GERA_LEMBRETE(int nota, bool esperaLembrete)
    {
        var (db, tx, amb) = await PrepararAsync($"faixa{nota}");
        using var _ = db; using var __ = tx;

        await PesquisaEnviadaAsync(db, amb, responsavel: amb.Cenario.Dono.Id);
        var texto = nota.ToString();
        var mensagem = await EntradaAsync(db, amb, texto);

        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, texto, null, default);

        db.ChangeTracker.Clear();
        var quantos = await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == amb.Cenario.Id).CountAsync();

        Assert.Equal(esperaLembrete ? 1 : 0, quantos);
    }

    /// <summary>⚠️ NEUTRO NAO MANDA NADA, nem com as duas mensagens configuradas. Nao e esquecimento:
    /// 7 e 8 nao contam no NPS, e escrever para dizer "obrigado pela sua indiferenca" nao melhora
    /// relacao nenhuma.</summary>
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public async Task O_NEUTRO_NAO_RECEBE_MENSAGEM_NENHUMA(int nota)
    {
        var (db, tx, amb) = await PrepararAsync($"neutro{nota}");
        using var _ = db; using var __ = tx;

        await ConfigurarMensagensAsync(db, amb.Cenario.Id, promotor: "Valeu!", detrator: "Desculpe.");
        await PesquisaEnviadaAsync(db, amb, responsavel: amb.Cenario.Dono.Id);

        var texto = nota.ToString();
        var mensagem = await EntradaAsync(db, amb, texto);

        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, texto, null, default);

        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    // ==================================================================== o agradecimento

    /// <summary>⚠️ A MENSAGEM NASCE MARCADA `automatica`/`nps`, e sem isso ela entraria no relatorio
    /// de tempo de resposta como se um humano tivesse escrito — que e exatamente o defeito que a
    /// etapa 1 consertou.
    ///
    /// ⚠️ E `negociacao_id` FICA NULO: `uq_msg_nps` e unico em `negociacao_id` filtrado por
    /// `tipo_automacao = 'nps'`, e a PERGUNTA ja ocupa aquela vaga. Preencher aqui faria o
    /// agradecimento ser recusado pelo indice.</summary>
    [Fact]
    public async Task O_PROMOTOR_RECEBE_O_AGRADECIMENTO_MARCADO_COMO_AUTOMATICO()
    {
        var (db, tx, amb) = await PrepararAsync("promotor");
        using var _ = db; using var __ = tx;

        await ConfigurarMensagensAsync(
            db, amb.Cenario.Id, promotor: "{{saudacao}} Que bom! Obrigado pela nota.", detrator: null);
        await PesquisaEnviadaAsync(db, amb, responsavel: amb.Cenario.Dono.Id);

        var mensagem = await EntradaAsync(db, amb, "10");
        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, "10", null, default);

        var enviada = Assert.Single(amb.Cliente.TextosEnviados);
        Assert.StartsWith("Oi, ", enviada.Texto);
        Assert.DoesNotContain("{{", enviada.Texto);

        db.ChangeTracker.Clear();
        var m = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Direcao == DirecaoMensagem.Saida
                     && x.EmpresaId == amb.Cenario.Id
                     && x.Texto!.Contains("Obrigado"))
            .SingleAsync();

        Assert.Equal(OrigemMensagem.Automatica, m.Origem);
        Assert.Equal(TipoAutomacao.Nps, m.TipoAutomacao);
        Assert.Null(m.NegociacaoId);
        Assert.NotNull(m.EnviadaEm);
    }

    /// <summary>⚠️ VAZIO = NAO ENVIA, e e o padrao. Uma segunda automatica depois da primeira dobra o
    /// risco do numero, e nem toda empresa quer. ⚠️ MAS O LEMBRETE DO DETRATOR SAI DE QUALQUER
    /// JEITO: a mensagem e opcional, a acao humana nao.</summary>
    [Fact]
    public async Task SEM_MENSAGEM_CONFIGURADA_NAO_ENVIA_MAS_O_DETRATOR_AINDA_AVISA()
    {
        var (db, tx, amb) = await PrepararAsync("sem-texto");
        using var _ = db; using var __ = tx;

        // Sem `ConfigurarMensagensAsync`: as duas nascem nulas.
        await PesquisaEnviadaAsync(db, amb, responsavel: amb.Cenario.Dono.Id);

        var mensagem = await EntradaAsync(db, amb, "1");
        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, "1", null, default);

        Assert.Empty(amb.Cliente.TextosEnviados);

        db.ChangeTracker.Clear();
        Assert.Single(await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ToListAsync());
    }

    /// <summary>===================== DESLIGADA, NADA SAI PARA O CLIENTE =====================
    ///
    /// ⚠️ O CASO DA REVISAO: o dono desligou a pesquisa porque cliente reclamou, e uma pesquisa
    /// que ja tinha saido recebe "10, obrigado" dias depois. Antes, o webhook registrava a nota E
    /// mandava o agradecimento automatico, com a funcao desligada.
    ///
    /// Agora a nota e registrada e o lembrete do detrator sai — sao internos, e o cliente respondeu
    /// a uma pergunta que de fato recebeu. A MENSAGEM ao cliente e que nao sai.
    /// ===================================================================================</summary>
    [Fact]
    public async Task COM_A_PESQUISA_DESLIGADA_A_NOTA_E_REGISTRADA_MAS_NADA_SAI_PARA_O_CLIENTE()
    {
        var (db, tx, amb) = await PrepararAsync("desligada");
        using var _ = db; using var __ = tx;

        await ConfigurarMensagensAsync(db, amb.Cenario.Id, promotor: "Obrigado!", detrator: "Desculpe.");
        var pesquisa = await PesquisaEnviadaAsync(db, amb, responsavel: amb.Cenario.Dono.Id);

        // O dono desliga DEPOIS de a pergunta ter saido.
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.NpsAtivo, false));
        db.ChangeTracker.Clear();

        var mensagem = await EntradaAsync(db, amb, "1");
        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, "1", null, default);

        Assert.Empty(amb.Cliente.TextosEnviados);

        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.Respondida, await db.PesquisasNps.IgnoreQueryFilters()
            .Where(p => p.Id == pesquisa).Select(p => p.Status).SingleAsync());
        Assert.Single(await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ToListAsync());
    }

    // ==================================================================== a corrida

    /// <summary>===================== A ACAO CORRE UMA VEZ, E SO UMA =====================
    ///
    /// ⚠️ O WEBHOOK NAO SERIALIZA POR CONTATO. Duas mensagens do mesmo cliente podem ser
    /// processadas em paralelo, e as duas leriam a pesquisa como `enviada`. Com entidade rastreada,
    /// as duas gravariam e as duas chamariam a acao: DOIS agradecimentos e lembrete em dobro.
    ///
    /// A transicao e um UPDATE condicional — `WHERE status IN (enviada, possivel_nota)` — e a
    /// segunda afeta ZERO linhas. Mesma disciplina do `ServicoVendas.ConcluirAsync`.
    ///
    /// Aqui as duas leituras sao SEQUENCIAIS, que e o que um teste pode fazer de forma deterministica
    /// — e ja prova a parte que importa: a segunda passada nao repete o efeito.
    /// =====================================================================</summary>
    [Fact]
    public async Task LER_A_MESMA_NOTA_DUAS_VEZES_NAO_DOBRA_A_ACAO()
    {
        var (db, tx, amb) = await PrepararAsync("corrida");
        using var _ = db; using var __ = tx;

        await ConfigurarMensagensAsync(db, amb.Cenario.Id, promotor: null, detrator: "Desculpe.");
        await PesquisaEnviadaAsync(db, amb, responsavel: amb.Cenario.Dono.Id);

        var primeira = await EntradaAsync(db, amb, "2");
        var segunda = await EntradaAsync(db, amb, "2");

        var r1 = await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, primeira, "2", null, default);
        var r2 = await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, segunda, "2", null, default);

        Assert.Equal(RespostaDaPesquisa.NotaRegistrada, r1);
        // A pesquisa ja saiu dos estados abertos: a segunda nao acha nada.
        Assert.Equal(RespostaDaPesquisa.Nenhuma, r2);

        Assert.Single(amb.Cliente.TextosEnviados);

        db.ChangeTracker.Clear();
        Assert.Single(await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ToListAsync());
    }

    /// <summary>⚠️ A SUSPEITA NAO E REESCRITA POR UMA MENSAGEM SEGUINTE. O vendedor esta olhando a
    /// primeira duvida na conversa; trocar o numero embaixo dele faria o botao "Confirmar nota 2"
    /// confirmar outra coisa.</summary>
    [Fact]
    public async Task UMA_SEGUNDA_DUVIDA_NAO_SOBRESCREVE_A_PRIMEIRA()
    {
        var (db, tx, amb) = await PrepararAsync("duvida-dupla");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaEnviadaAsync(db, amb, responsavel: amb.Cenario.Dono.Id);

        var um = await EntradaAsync(db, amb, "quero 2 unidades");
        var dois = await EntradaAsync(db, amb, "manda 5 caixas");

        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, um, "quero 2 unidades", null, default);
        var segunda = await amb.Leitura.LerAsync(
            amb.Cenario.Id, amb.Contato.Id, dois, "manda 5 caixas", null, default);

        Assert.Equal(RespostaDaPesquisa.Nenhuma, segunda);

        db.ChangeTracker.Clear();
        Assert.Equal((short)2, await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).Select(x => x.Nota).SingleAsync());
    }

    /// <summary>===================== A GUARDA MORA NO CHAMADOR, E ESTE TESTE DIZ ISSO =====================
    ///
    /// ⚠️ `AcoesDaNota` NAO E IDEMPOTENTE POR CONTA PROPRIA, e e deliberado: chamada duas vezes, ela
    /// agradece duas vezes e cria lembrete em dobro. Quem garante a unica execucao e a
    /// `LeituraDaResposta`, com um UPDATE condicional na transicao de status.
    ///
    /// Este teste existe por duas razoes:
    ///
    ///   · dizer ONDE a guarda mora, para quem for mexer em qualquer um dos dois;
    ///   · impedir a "defesa" silenciosa — alguem tornar `AcoesDaNota` idempotente tambem e
    ///     DEIXAR a guarda do chamador, duplicando a responsabilidade. Duas protecoes para o mesmo
    ///     fato divergem, e a que fica sem teste e a que apodrece.
    ///
    /// ⚠️ E ELE REGISTRA UM LIMITE DA SUITE. Sabotei o `WHERE` da transicao e NADA CAIU: no caso
    /// SEQUENCIAL, a consulta de pesquisa aberta ja barra a segunda passada. O predicado cobre a
    /// concorrencia de verdade — as duas leituras antes de qualquer escrita —, e um teste de
    /// integracao nao monta isso, porque tudo roda numa transacao so.
    /// ==========================================================================================</summary>
    [Fact]
    public async Task ACOES_DA_NOTA_NAO_SE_PROTEGE_SOZINHA_E_NAO_DEVE()
    {
        var (db, tx, amb) = await PrepararAsync("guarda");
        using var _ = db; using var __ = tx;

        await ConfigurarMensagensAsync(db, amb.Cenario.Id, promotor: null, detrator: "Desculpe.");
        var pesquisa = await PesquisaEnviadaAsync(db, amb, responsavel: amb.Cenario.Dono.Id);

        // A nota tem de ESTAR na linha: as acoes saem cedo quando ela e nula — e e assim que o
        // caminho normal funciona, porque a leitura grava a nota ANTES de chamar.
        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.Id == pesquisa)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Nota, (short?)2));
        db.ChangeTracker.Clear();

        // Chamando DIRETO, sem passar pela leitura — exatamente o que a guarda impede.
        var acoes = AcoesDeTeste(db, amb);

        await acoes.ExecutarAsync(pesquisa, default);
        await acoes.ExecutarAsync(pesquisa, default);

        // A pesquisa precisa ter nota para as acoes correrem; o status nao importa para elas.
        db.ChangeTracker.Clear();

        Assert.Equal(2, amb.Cliente.TextosEnviados.Count);
        Assert.Equal(2, await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == amb.Cenario.Id).CountAsync());
    }

    // ==================================================================== o andaime

    private sealed record Ambiente(
        Cenario Cenario, Contato Contato, Conversa Conversa,
        ILeituraDaResposta Leitura, ClienteWhatsAppFalso Cliente);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"nps-ac-{sufixo}");
        var relogio = new RelogioFalso(QuintaDeManha);
        var cliente = new ClienteWhatsAppFalso();

        // ⚠️ O CONTEXTO FICA VAZIO: isto roda dentro do webhook, sem tenant.
        return (db, tx, new Ambiente(
            cenario, cenario.Contato, cenario.Conversa,
            LeituraNpsDeTeste.Novo(db, relogio, cliente), cliente));
    }

    /// <summary>As acoes SOLTAS, sem a leitura em volta — so o teste da guarda usa isso.</summary>
    private static IAcoesDaNota AcoesDeTeste(NexoraDbContext db, Ambiente amb)
    {
        var relogio = new RelogioFalso(QuintaDeManha);

        var enviador = new EnviadorMensagem(
            new DadosMensagem(db, relogio), amb.Cliente,
            new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero },
            relogio, NullLogger<EnviadorMensagem>.Instance);

        return new AcoesDaNota(db, enviador, relogio, NullLogger<AcoesDaNota>.Instance);
    }

    private static async Task ConfigurarMensagensAsync(
        NexoraDbContext db, long empresaId, string? promotor, string? detrator)
    {
        // ⚠️ LIGA A PESQUISA JUNTO, como o dono faz na tela. Os testes daqui esperavam o agradecimento
        // com a pesquisa DESLIGADA (o padrao) — e passavam porque a mensagem saia mesmo desligada,
        // que era o defeito da revisao NPS-1.
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == empresaId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.NpsAtivo, true)
                .SetProperty(e => e.NpsMensagemPromotor, promotor)
                .SetProperty(e => e.NpsMensagemDetrator, detrator));
        db.ChangeTracker.Clear();
    }

    private static async Task<Usuario> VendedorAsync(NexoraDbContext db, Ambiente amb, string marca)
    {
        var u = new Usuario
        {
            EmpresaId = amb.Cenario.Id,
            Nome = $"Vend {marca}",
            Email = $"{marca}-{Random.Shared.Next(100000)}@x.com",
            SenhaHash = "x",
            Papel = PapelUsuario.Vendedor,
            Status = StatusUsuario.Ativo
        };
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return u;
    }

    /// <summary>Uma pesquisa ja ENVIADA, com a venda atribuida a quem for pedido.</summary>
    private static async Task<long> PesquisaEnviadaAsync(
        NexoraDbContext db, Ambiente amb, long? responsavel)
    {
        var etapa = amb.Cenario.Etapas[0];

        var negocio = new Negociacao
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Contato.Id,
            PipelineId = etapa.PipelineId,
            EtapaId = etapa.Id,
            Status = StatusNegociacao.Concluida,
            Valor = 1000m,
            ResponsavelId = responsavel,
            GanhaEm = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            ConcluidaEm = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)
        };
        db.Negociacoes.Add(negocio);
        await db.SaveChangesAsync();

        var pergunta = new Mensagem
        {
            EmpresaId = amb.Cenario.Id,
            ConversaId = amb.Conversa.Id,
            ContatoId = amb.Contato.Id,
            ConexaoId = amb.Cenario.Conexao.Id,
            InstanceName = amb.Cenario.Conexao.InstanceName,
            Direcao = DirecaoMensagem.Saida,
            Texto = "De 0 a 10, quanto você recomendaria a gente?",
            Origem = OrigemMensagem.Automatica,
            TipoAutomacao = TipoAutomacao.Nps,
            NegociacaoId = negocio.Id,
            DataDisparo = Hoje
        };
        db.Mensagens.Add(pergunta);
        await db.SaveChangesAsync();

        var pesquisa = new PesquisaNps
        {
            EmpresaId = amb.Cenario.Id,
            NegociacaoId = negocio.Id,
            ContatoId = amb.Contato.Id,
            MensagemEnvioId = pergunta.Id,
            Status = StatusPesquisaNps.Enviada,
            DataAgendada = Hoje,
            DataLimite = Hoje.AddDays(7),
            DataEnvio = new DateTime(2026, 8, 6, 11, 0, 0, DateTimeKind.Utc)
        };
        db.PesquisasNps.Add(pesquisa);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return pesquisa.Id;
    }

    private static async Task<long> EntradaAsync(NexoraDbContext db, Ambiente amb, string? texto)
    {
        var m = new Mensagem
        {
            EmpresaId = amb.Cenario.Id,
            ConversaId = amb.Conversa.Id,
            ContatoId = amb.Contato.Id,
            ConexaoId = amb.Cenario.Conexao.Id,
            InstanceName = amb.Cenario.Conexao.InstanceName,
            Direcao = DirecaoMensagem.Entrada,
            Texto = texto,
            Origem = OrigemMensagem.Humana
        };
        db.Mensagens.Add(m);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return m.Id;
    }
}
