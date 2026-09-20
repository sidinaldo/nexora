using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>A IMPORTAÇÃO GRANDE, PROCESSADA DEPOIS (INT-XX, commit 4).
///
/// O spec: "até 500 linhas, síncrono no request; acima, BackgroundService com a tela fazendo
/// polling". O que estes testes cobrem é o que muda de mão — o clique guarda as escolhas e sai, e
/// o job as executa mais tarde, sem ninguém na frente da tela.
///
/// ⚠️ O CONTEXTO DO JOB É O DA PRODUÇÃO, e não um atalho de teste: `ContextoComFundo` faz o que o
/// `ContextoEmpresaHttp` faz — usa o usuário da requisição quando há um, e cai para a empresa que
/// o job assumiu quando não há. É o que permite o job rodar o MESMO código do botão, com o query
/// filter protegendo o isolamento igual.</summary>
[Collection("banco")]
public class ImportacaoEmSegundoPlanoDbTests(BancoTeste banco)
{
    private const string Cabecalho = "id,created_time,campaign_name,full_name,phone_number,email";

    private static string Lead(int n) =>
        $"l:{900000 + n},2026-09-10T14:32:11+0000,Campanha Setembro,Pessoa {n},"
        + $"p:+55849{3_000_000 + n:D7},p{n}@x.com";

    private static byte[] Arquivo(int quantas)
    {
        var linhas = new List<string> { Cabecalho };
        for (var i = 0; i < quantas; i++) linhas.Add(Lead(i));
        return new UTF8Encoding(false).GetBytes(string.Join("\n", linhas));
    }

    /// <summary>⚠️ ACIMA DO CORTE, O CLIQUE NÃO GRAVA NINGUÉM — ele guarda as escolhas e devolve
    /// `processando`. 10.000 linhas num request é o navegador desistindo no meio e o dono sem saber
    /// se importou.
    ///
    /// E as escolhas ficam no banco porque o job precisa delas: a requisição que as recebeu já
    /// acabou quando ele começa, e não há a quem perguntar.</summary>
    [Fact]
    public async Task ACIMA_DO_CORTE_O_CLIQUE_SO_ENFILEIRA()
    {
        var (db, tx, amb) = await PrepararAsync("enfileira");
        using var _1 = db; using var _2 = tx;

        var quantas = IServicoImportacaoMeta.CorteSincrono + 1;
        var r = await amb.Servico.ReceberAsync("grande.csv", Arquivo(quantas), default);

        var fim = await amb.Servico.GravarAsync(r.Id, new GravarImportacao(
            r.Mapeamento, PipelineId: amb.Cenario.Pipeline.Id,
            ResponsavelId: amb.Cenario.Dono.Id, AvisarIntegracoes: true), default);

        Assert.Equal(StatusImportacao.Processando, fim.Status);
        Assert.Equal(0, fim.Importados);

        db.ChangeTracker.Clear();
        var imp = await db.Importacoes.AsNoTracking().SingleAsync(i => i.Id == r.Id);

        // As escolhas guardadas, e a fila ainda LIVRE: ninguém reservou este trabalho.
        Assert.Equal(amb.Cenario.Pipeline.Id, imp.PipelineId);
        Assert.Equal(amb.Cenario.Dono.Id, imp.ResponsavelId);
        Assert.True(imp.AvisarIntegracoes);
        Assert.Null(imp.ProcessandoDesde);

        // E nenhum contato ainda.
        Assert.Equal(0, await db.Contatos.CountAsync(c => c.Origem == OrigemLead.MetaAds));
    }

    /// <summary>E o job pega da fila e grava — com as escolhas que o dono fez, na empresa dele.</summary>
    [Fact]
    public async Task O_JOB_PEGA_DA_FILA_E_GRAVA_COM_AS_ESCOLHAS_DO_DONO()
    {
        var (db, tx, amb) = await PrepararAsync("job-grava");
        using var _1 = db; using var _2 = tx;

        var quantas = IServicoImportacaoMeta.CorteSincrono + 1;
        var r = await amb.Servico.ReceberAsync("grande.csv", Arquivo(quantas), default);
        await amb.Servico.GravarAsync(r.Id, new GravarImportacao(
            r.Mapeamento, PipelineId: amb.Cenario.Pipeline.Id,
            ResponsavelId: amb.Cenario.Dono.Id), default);

        // ---------- o job: sem usuário no contexto, como acontece de verdade
        amb.Humano.EmpresaId = 0;
        amb.Humano.UsuarioId = 0;
        db.ChangeTracker.Clear();

        Assert.Equal(1, await amb.Motor.ExecutarAsync());

        db.ChangeTracker.Clear();
        var imp = await db.Importacoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.Id == r.Id);

        Assert.Equal(StatusImportacao.Concluida, imp.Status);
        Assert.Equal(quantas, imp.Importados);
        Assert.NotNull(imp.ProcessandoDesde);

        var importados = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EmpresaId == amb.Cenario.Id && c.Origem == OrigemLead.MetaAds)
            .ToListAsync();

        Assert.Equal(quantas, importados.Count);
        Assert.All(importados, c => Assert.Equal(amb.Cenario.Dono.Id, c.ResponsavelId));

        // Os cards foram para o funil escolhido — a escolha sobreviveu ao fim da requisição.
        var ids = importados.Select(c => c.Id).ToList();
        Assert.Equal(quantas, await db.Negociacoes.IgnoreQueryFilters()
            .CountAsync(n => ids.Contains(n.ContatoId) && n.PipelineId == amb.Cenario.Pipeline.Id));

        // ---------- e não há o que pegar de novo
        Assert.Equal(0, await amb.Motor.ExecutarAsync());
    }

    /// <summary>⚠️ DUAS INSTÂNCIAS NÃO GRAVAM A MESMA IMPORTAÇÃO. `processando_desde` é a reserva:
    /// quem marcar primeiro processa, e o outro vai embora sem trabalho.
    ///
    /// Os outros jobs deste projeto convivem com a corrida — webhook duplicado o receptor
    /// deduplica. Aqui o efeito seria a mesma pessoa duas vezes na base do cliente, e os
    /// contadores da primeira rodada sobrescritos pela segunda.</summary>
    [Fact]
    public async Task O_QUE_JA_FOI_RESERVADO_NAO_E_PEGO_DE_NOVO()
    {
        var (db, tx, amb) = await PrepararAsync("reserva");
        using var _1 = db; using var _2 = tx;

        var r = await amb.Servico.ReceberAsync("grande.csv",
            Arquivo(IServicoImportacaoMeta.CorteSincrono + 1), default);
        await amb.Servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default);

        // A outra instância chegou primeiro e reservou.
        await db.Importacoes.IgnoreQueryFilters().Where(i => i.Id == r.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ProcessandoDesde, DateTime.UtcNow));

        amb.Humano.EmpresaId = 0;
        db.ChangeTracker.Clear();

        Assert.Equal(0, await amb.Motor.ExecutarAsync());

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Contatos.IgnoreQueryFilters()
            .CountAsync(c => c.EmpresaId == amb.Cenario.Id && c.Origem == OrigemLead.MetaAds));
    }

    /// <summary>⚠️ E A RESERVA EM SI: duas instâncias que acharam a MESMA linha antes de qualquer
    /// uma marcar. Só uma marca.
    ///
    /// O teste acima cobre o outro lado — a busca não traz o que já está reservado —, e ele passa
    /// mesmo com a reserva escrita errada. Este chama a reserva duas vezes, que é o mais perto de
    /// uma corrida que um teste de uma thread consegue chegar: se o `WHERE processando_desde IS
    /// NULL` sair, a segunda chamada também ganha, e as duas instâncias gravariam.</summary>
    [Fact]
    public async Task SO_UMA_INSTANCIA_GANHA_A_RESERVA()
    {
        var (db, tx, amb) = await PrepararAsync("corrida");
        using var _1 = db; using var _2 = tx;

        var r = await amb.Servico.ReceberAsync("grande.csv",
            Arquivo(IServicoImportacaoMeta.CorteSincrono + 1), default);
        await amb.Servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default);
        db.ChangeTracker.Clear();

        Assert.True(await amb.Motor.ReservarAsync(r.Id), "a primeira instância tem de ganhar");
        Assert.False(await amb.Motor.ReservarAsync(r.Id), "a segunda NÃO pode ganhar a mesma");
    }

    /// <summary>⚠️ O JOB NÃO MISTURA EMPRESAS. Ele roda sem tenant até ASSUMIR a empresa da
    /// importação que pegou — e é o que faz o contato importado nascer na empresa certa.
    ///
    /// Sem isso, a alternativa seria um segundo caminho de gravação escrito com
    /// `IgnoreQueryFilters`, e um `Where` esquecido ali põe o lead de um cliente na base de outro.</summary>
    [Fact]
    public async Task CADA_IMPORTACAO_GRAVA_NA_EMPRESA_DELA()
    {
        var (db, tx, amb) = await PrepararAsync("duas-empresas");
        using var _1 = db; using var _2 = tx;

        // A empresa A enfileira a dela…
        var deA = await amb.Servico.ReceberAsync("a.csv",
            Arquivo(IServicoImportacaoMeta.CorteSincrono + 1), default);
        await amb.Servico.GravarAsync(deA.Id, new GravarImportacao(deA.Mapeamento), default);

        // …e a empresa B, a dela.
        var b = await Semeador.TenantAsync(db, "importacao-fundo-b");
        amb.Humano.EmpresaId = b.Id;
        amb.Humano.UsuarioId = b.Dono.Id;
        db.ChangeTracker.Clear();

        var deB = await amb.Servico.ReceberAsync("b.csv",
            Arquivo(IServicoImportacaoMeta.CorteSincrono + 1), default);
        await amb.Servico.GravarAsync(deB.Id, new GravarImportacao(deB.Mapeamento), default);

        // ---------- o job, sem ninguém no contexto, processa as duas
        amb.Humano.EmpresaId = 0;
        amb.Humano.UsuarioId = 0;
        db.ChangeTracker.Clear();

        Assert.Equal(1, await amb.Motor.ExecutarAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(1, await amb.Motor.ExecutarAsync());
        db.ChangeTracker.Clear();

        // ⚠️ O MESMO TELEFONE nas duas: as duas empresas importaram o mesmo arquivo. Se o job
        // vazasse de uma para a outra, a segunda teria 0 importados — todos "já cadastrados".
        foreach (var empresa in new[] { amb.Cenario.Id, b.Id })
        {
            var quantos = await db.Contatos.IgnoreQueryFilters()
                .CountAsync(c => c.EmpresaId == empresa && c.Origem == OrigemLead.MetaAds);

            Assert.Equal(IServicoImportacaoMeta.CorteSincrono + 1, quantos);
        }
    }

    /// <summary>A tela pergunta onde a importação está enquanto o job trabalha — e os contadores
    /// sobem a cada lote, então o número anda em vez de ficar em zero até o fim.</summary>
    [Fact]
    public async Task A_TELA_ACOMPANHA_O_PROGRESSO()
    {
        var (db, tx, amb) = await PrepararAsync("acompanhar");
        using var _1 = db; using var _2 = tx;

        var r = await amb.Servico.ReceberAsync("grande.csv",
            Arquivo(IServicoImportacaoMeta.CorteSincrono + 1), default);
        await amb.Servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default);

        var naFila = await amb.Servico.AcompanharAsync(r.Id, default);
        Assert.Equal(StatusImportacao.Processando, naFila.Status);
        Assert.Equal(0, naFila.Importados);

        amb.Humano.EmpresaId = 0;
        db.ChangeTracker.Clear();
        await amb.Motor.ExecutarAsync();

        amb.Humano.EmpresaId = amb.Cenario.Id;
        db.ChangeTracker.Clear();

        var depois = await amb.Servico.AcompanharAsync(r.Id, default);
        Assert.Equal(StatusImportacao.Concluida, depois.Status);
        Assert.Equal(IServicoImportacaoMeta.CorteSincrono + 1, depois.Importados);
    }

    /// <summary>Até o corte, nada de fila: grava no próprio request e devolve `concluida`. É o caso
    /// comum — um export de algumas dezenas de leads —, e mandá-lo para a fila faria a tela ficar
    /// esperando um job para um trabalho que leva um segundo.</summary>
    [Fact]
    public async Task ATE_O_CORTE_GRAVA_NA_HORA()
    {
        var (db, tx, amb) = await PrepararAsync("na-hora");
        using var _1 = db; using var _2 = tx;

        var r = await amb.Servico.ReceberAsync("pequeno.csv", Arquivo(3), default);
        var fim = await amb.Servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default);

        Assert.Equal(StatusImportacao.Concluida, fim.Status);
        Assert.Equal(3, fim.Importados);

        // E o job não tem o que fazer.
        amb.Humano.EmpresaId = 0;
        db.ChangeTracker.Clear();
        Assert.Equal(0, await amb.Motor.ExecutarAsync());
    }

    // ====================================================================
    /// <summary>O contexto da PRODUÇÃO: o usuário da requisição quando há um, a empresa que o job
    /// assumiu quando não há. Ver `ContextoEmpresaHttp`, que faz exatamente isto com os claims.</summary>
    private sealed class ContextoComFundo(ContextoMutavel humano, ContextoDeFundo fundo) : IContextoEmpresa
    {
        public long EmpresaId => humano.EmpresaId != 0 ? humano.EmpresaId : fundo.EmpresaId;
        public long UsuarioId => humano.UsuarioId != 0 ? humano.UsuarioId : fundo.UsuarioId;
        public string? Papel => humano.EmpresaId != 0 ? humano.Papel : null;
        public bool EstaAutenticado => EmpresaId != 0;
    }

    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Humano, ContextoDeFundo Fundo,
        ServicoImportacaoMeta Servico, MotorImportacoes Motor);

    /// <summary>Transação como nos outros testes de banco — o job lê por fora do TENANT, não por
    /// fora da transação: é a mesma conexão, e ele enxerga o que o teste acabou de gravar.</summary>
    private async Task<(NexoraDbContext, IDbContextTransaction, Ambiente)> PrepararAsync(string sufixo)
    {
        var humano = new ContextoMutavel();
        var fundo = new ContextoDeFundo();
        var contexto = new ContextoComFundo(humano, fundo);
        var trilha = new ColetorAuditoria();

        var db = banco.NovoContexto(contexto, coletor: trilha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"importacao-fundo-{sufixo}");
        humano.EmpresaId = cenario.Id;
        humano.UsuarioId = cenario.Dono.Id;
        humano.Papel = "dono";
        db.ChangeTracker.Clear();

        var servico = new ServicoImportacaoMeta(
            db, contexto, PublicadorDeTeste.Novo(db), trilha, TimeProvider.System);

        var motor = new MotorImportacoes(
            db, fundo, servico, TimeProvider.System, NullLogger<MotorImportacoes>.Instance);

        return (db, tx, new Ambiente(cenario, humano, fundo, servico, motor));
    }
}
