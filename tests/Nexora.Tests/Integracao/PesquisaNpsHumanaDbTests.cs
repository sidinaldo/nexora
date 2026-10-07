using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== NPS-1 ETAPA 2f — O QUE UM HUMANO DECIDE =====================
///
/// Os tres gestos sobre a pesquisa (confirmar, nao e nota, cancelar), a configuracao do dono, e a
/// LGPD.
///
/// ⚠️ ESTE SERVICO RODA NA REQUISICAO, com tenant no contexto — diferente do `MotorNps` (job) e da
/// `LeituraDaResposta` (webhook). Entao o filtro global VALE, e o teste de isolamento aqui prova
/// outra coisa: que ninguem precisou de `IgnoreQueryFilters` para fazer funcionar.
/// ====================================================================================</summary>
[Collection("banco")]
public class PesquisaNpsHumanaDbTests(BancoTeste banco)
{
    private static readonly DateOnly Hoje = new(2026, 8, 6);
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 0, 0, TimeSpan.Zero);

    // ==================================================================== confirmar a nota

    /// <summary>===================== CONFIRMAR NA MAO FAZ TUDO QUE A LEITURA FARIA =====================
    ///
    /// ⚠️ INCLUSIVE AS ACOES DA FAIXA. Confirmar nota 2 na mao tem de criar o lembrete do detrator
    /// igual a nota 2 lida sozinha — senao o aviso dependeria de o `LeitorDeNota` ter acertado, e a
    /// `PossivelNota` existe justamente para quando ele NAO acertou.
    ///
    /// ⚠️ E `ConfirmadaPorUsuarioId` E PREENCHIDA, enquanto a nota lida sozinha a deixa nula: e essa
    /// distincao que permite medir depois se as regras do leitor estao estreitas demais.
    /// ==========================================================================================</summary>
    [Fact]
    public async Task CONFIRMAR_A_NOTA_RESPONDE_A_PESQUISA_E_DISPARA_A_ACAO_DA_FAIXA()
    {
        var (db, tx, amb) = await PrepararAsync("confirmar");
        using var _ = db; using var __ = tx;

        await ConfigurarAsync(db, amb.Cenario.Id, detrator: "Desculpe.");
        var pesquisa = await PesquisaAsync(db, amb, StatusPesquisaNps.PossivelNota, nota: 2);

        await amb.Servico.ConfirmarNotaAsync(pesquisa, default);

        db.ChangeTracker.Clear();
        var p = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).SingleAsync();

        Assert.Equal(StatusPesquisaNps.Respondida, p.Status);
        Assert.Equal((short)2, p.Nota);
        Assert.NotNull(p.DataResposta);
        Assert.Equal(amb.Cenario.Dono.Id, p.ConfirmadaPorUsuarioId);

        // A acao da faixa correu: lembrete do detrator e a mensagem.
        Assert.Single(await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ToListAsync());
        Assert.Single(amb.Cliente.TextosEnviados);
    }

    [Theory]
    [InlineData(StatusPesquisaNps.Enviada)]
    [InlineData(StatusPesquisaNps.Respondida)]
    [InlineData(StatusPesquisaNps.Expirada)]
    [InlineData(StatusPesquisaNps.Cancelada)]
    public async Task CONFIRMAR_O_QUE_NAO_ESTA_EM_DUVIDA_E_RECUSADO(StatusPesquisaNps status)
    {
        var (db, tx, amb) = await PrepararAsync($"conf-{status}");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb, status, nota: 7);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Servico.ConfirmarNotaAsync(pesquisa, default));

        Assert.True(erro.Conflito);
    }

    // ==================================================================== não é nota

    /// <summary>===================== "NAO ERA NOTA" APAGA A SUSPEITA =====================
    ///
    /// ⚠️ A `nota` TEM DE SAIR, e nao so o status: uma nota pendurada numa pesquisa `enviada` seria
    /// lida como resultado pelo proximo que olhasse a linha — e o check do banco NAO barra, porque
    /// `ck_pesquisas_nps_respondida` so exige nota em `respondida`.
    ///
    /// A pesquisa segue esperando, e `DataEnvio` nao e tocada: o relogio da expiracao conta de
    /// quando a PERGUNTA saiu, nao de quando alguem desfez uma duvida.
    /// ==================================================================</summary>
    [Fact]
    public async Task NAO_E_NOTA_VOLTA_A_ESPERAR_E_APAGA_A_SUSPEITA()
    {
        var (db, tx, amb) = await PrepararAsync("nao-e-nota");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb, StatusPesquisaNps.PossivelNota, nota: 2);

        var envioAntes = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).Select(x => x.DataEnvio).SingleAsync();

        await amb.Servico.NaoEhNotaAsync(pesquisa, default);

        db.ChangeTracker.Clear();
        var p = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).SingleAsync();

        Assert.Equal(StatusPesquisaNps.Enviada, p.Status);
        Assert.Null(p.Nota);
        Assert.Null(p.MensagemRespostaId);
        Assert.Equal(envioAntes, p.DataEnvio);

        // E nenhuma acao de faixa correu: nao houve nota.
        Assert.Empty(amb.Cliente.TextosEnviados);
        Assert.Empty(await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == amb.Cenario.Id).ToListAsync());
    }

    /// <summary>⚠️ DEPOIS DE "NAO ERA NOTA", A PESQUISA VOLTA A LER. Ela esta em `enviada`, que e um
    /// dos dois estados abertos — e e isso que faz o cliente ainda poder mandar a nota de verdade.</summary>
    [Fact]
    public async Task DEPOIS_DE_NAO_E_NOTA_A_PESQUISA_AINDA_ACEITA_A_NOTA()
    {
        var (db, tx, amb) = await PrepararAsync("volta-a-ler");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb, StatusPesquisaNps.PossivelNota, nota: 2);
        await amb.Servico.NaoEhNotaAsync(pesquisa, default);
        db.ChangeTracker.Clear();

        var mensagem = await EntradaAsync(db, amb, "9");
        var r = await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, "9", null, default);

        Assert.Equal(RespostaDaPesquisa.NotaRegistrada, r);

        db.ChangeTracker.Clear();
        Assert.Equal((short)9, await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).Select(x => x.Nota).SingleAsync());
    }

    // ==================================================================== cancelar

    [Fact]
    public async Task CANCELAR_PARA_A_PESQUISA_SEM_NOTA()
    {
        var (db, tx, amb) = await PrepararAsync("cancelar");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb, StatusPesquisaNps.Enviada, nota: null);

        await amb.Servico.CancelarAsync(pesquisa, default);

        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.Cancelada,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == pesquisa).Select(x => x.Status).SingleAsync());
    }

    /// <summary>⚠️ RESPONDIDA NAO SE CANCELA. A nota ja esta no relatorio, e cancelar depois a
    /// tiraria — o faturamento de um mes fechado mudando sozinho, que e o defeito que este projeto
    /// combate em varios lugares.</summary>
    [Fact]
    public async Task CANCELAR_UMA_RESPONDIDA_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("cancelar-resp");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, nota: 9);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Servico.CancelarAsync(pesquisa, default));

        Assert.True(erro.Conflito);
    }

    // ==================================================================== a trilha

    /// <summary>===================== A TRILHA DOS TRES GESTOS =====================
    ///
    /// ⚠️ CONFIRMAR E O UNICO PONTO EM QUE UMA PESSOA MUDA UM NUMERO DE RELATORIO COM UM CLIQUE.
    /// "Quem disse que aquele 2 era uma nota" e pergunta que aparece quando a metrica do mes nao
    /// fecha, e sem o VALOR na linha a trilha diria so "alguem resolveu algo".
    /// ==================================================================</summary>
    [Fact]
    public async Task CONFIRMAR_E_DESFAZER_DEIXAM_A_NOTA_NA_TRILHA()
    {
        var (db, tx, amb) = await PrepararAsync("trilha");
        using var _ = db; using var __ = tx;

        var confirmada = await PesquisaAsync(db, amb, StatusPesquisaNps.PossivelNota, nota: 4);
        await amb.Servico.ConfirmarNotaAsync(confirmada, default);

        db.ChangeTracker.Clear();
        var evento = await db.Auditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Entidade == EntidadeAuditada.Contato
                     && a.EntidadeId == amb.Contato.Id
                     && a.Acao == AcaoAuditoria.Resolveu)
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.Contains("notaNps", evento.Alteracoes);
        Assert.Contains("4", evento.Alteracoes);
        Assert.Equal(amb.Cenario.Dono.Id, evento.UsuarioId);
    }

    [Fact]
    public async Task CANCELAR_A_MAO_ENTRA_NA_TRILHA()
    {
        var (db, tx, amb) = await PrepararAsync("trilha-cancela");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb, StatusPesquisaNps.Enviada, nota: null);
        await amb.Servico.CancelarAsync(pesquisa, default);

        db.ChangeTracker.Clear();
        Assert.Single(await db.Auditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.EntidadeId == amb.Contato.Id && a.Acao == AcaoAuditoria.Cancelou)
            .ToListAsync());
    }

    /// <summary>⚠️ A CONFIGURACAO DA PESQUISA E AUDITADA, E A DE ATENDIMENTO NAO. A assimetria e
    /// deliberada: isto decide MENSAGEM SAINDO para cliente, e "quem mudou o texto que o cliente
    /// recebeu" e pergunta que aparece depois de a mensagem chegar errada.</summary>
    [Fact]
    public async Task MUDAR_A_CONFIGURACAO_DEIXA_O_DE_PARA_NA_TRILHA()
    {
        var (db, tx, amb) = await PrepararAsync("trilha-config");
        using var _ = db; using var __ = tx;

        await amb.Config.AtualizarPesquisaNpsAsync(
            new EditarPesquisaNps(true, 5, 2, "{{saudacao}} Nota de 0 a 10?", null, null), default);

        db.ChangeTracker.Clear();
        var evento = Assert.Single(await db.Auditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Entidade == EntidadeAuditada.Empresa
                     && a.EntidadeId == amb.Cenario.Id)
            .ToListAsync());

        Assert.Contains("npsAtivo", evento.Alteracoes);
        Assert.Contains("npsTexto", evento.Alteracoes);
    }

    // ==================================================================== a configuração

    [Fact]
    public async Task A_CONFIGURACAO_E_GRAVADA_E_LIDA_DE_VOLTA()
    {
        var (db, tx, amb) = await PrepararAsync("config");
        using var _ = db; using var __ = tx;

        await amb.Config.AtualizarPesquisaNpsAsync(
            new EditarPesquisaNps(
                true, 5, 2, "  {{saudacao}} De 0 a 10?  ", "  Valeu!  ", "   "), default);

        db.ChangeTracker.Clear();
        var c = await amb.Config.ObterAsync(default);

        Assert.True(c.NpsAtivo);
        Assert.Equal((short)5, c.NpsDiasAposConclusao);
        Assert.Equal((short)2, c.NpsDiasExpiracao);

        // Aparado nas duas pontas.
        Assert.Equal("{{saudacao}} De 0 a 10?", c.NpsTexto);
        Assert.Equal("Valeu!", c.NpsMensagemPromotor);

        // ⚠️ SO ESPACO VIRA NULO: vazio e nulo querem dizer a mesma coisa — nao envia. Guardar
        // string em branco deixaria o campo dizendo "ha uma mensagem" sem mensagem nenhuma.
        Assert.Null(c.NpsMensagemDetrator);
    }

    /// <summary>===================== O CAMPO OMITIDO NAO DESLIGA A PESQUISA =====================
    ///
    /// ⚠️ `bool?` E A RAZAO, e e a mesma do `ConclusaoAutomatica` (POS-1): o PUT manda o documento
    /// inteiro, e campo omitido desserializa para o default do tipo. Em `bool` isso da `false` — um
    /// valor VALIDO —, e a pesquisa seria desligada em silencio por algo que ninguem escolheu.
    /// ==============================================================================</summary>
    [Fact]
    public async Task CONFIGURACAO_SEM_O_ATIVO_E_RECUSADA_EM_VEZ_DE_DESLIGAR()
    {
        var (db, tx, amb) = await PrepararAsync("config-nulo");
        using var _ = db; using var __ = tx;

        await amb.Config.AtualizarPesquisaNpsAsync(
            new EditarPesquisaNps(true, 3, 3, "{{saudacao}} De 0 a 10?", null, null), default);
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Config.AtualizarPesquisaNpsAsync(
                new EditarPesquisaNps(null, 3, 3, "{{saudacao}} De 0 a 10?", null, null), default));

        db.ChangeTracker.Clear();
        Assert.True(await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == amb.Cenario.Id).Select(e => e.NpsAtivo).SingleAsync());
    }

    /// <summary>⚠️ O TEXTO VAZIO E O PIOR DOS CASOS: a coluna e NOT NULL, entao o banco aceitaria
    /// `''` — e sairia uma mensagem EM BRANCO no WhatsApp do cliente. Nenhum erro, nenhum log.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PERGUNTA_VAZIA_E_RECUSADA(string texto)
    {
        var (db, tx, amb) = await PrepararAsync($"vazio{texto.Length}");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Config.AtualizarPesquisaNpsAsync(
                new EditarPesquisaNps(true, 3, 3, texto, null, null), default));
    }

    /// <summary>⚠️ ZERO DIAS DE ESPERA FARIA A PESQUISA EXPIRAR NO INSTANTE DO ENVIO — o cliente
    /// receberia uma pergunta que o sistema ja desistiu de ler.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public async Task ESPERA_FORA_DA_FAIXA_E_RECUSADA(int dias)
    {
        var (db, tx, amb) = await PrepararAsync($"espera{dias}");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Config.AtualizarPesquisaNpsAsync(
                new EditarPesquisaNps(true, 3, (short)dias, "De 0 a 10?", null, null), default));
    }

    // ==================================================================== a LGPD

    /// <summary>===================== ANONIMIZAR APAGA O COMENTARIO, NAO A NOTA =====================
    ///
    /// ⚠️ MESMA DIVISAO DOS `meta_*` — pessoa sai, medida fica.
    ///
    /// O comentario e TEXTO LIVRE do cliente: "o entregador Joao chegou atrasado na rua tal". Pode
    /// conter nome, endereco, qualquer coisa. E dado pessoal, e sai.
    ///
    /// A NOTA fica, sem vinculo identificavel: e um 7 ligado a um contato que agora se chama
    /// "Contato anonimizado". Apagar as notas para proteger um numero de 0 a 10 quebraria a metrica
    /// de um mes inteiro em troca de nada.
    /// ==========================================================================================</summary>
    [Fact]
    public async Task ANONIMIZAR_APAGA_O_COMENTARIO_E_PRESERVA_A_NOTA()
    {
        var (db, tx, amb) = await PrepararAsync("lgpd");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, nota: 7);

        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.Id == pesquisa)
            .ExecuteUpdateAsync(u => u.SetProperty(
                x => x.Comentario, "o entregador Joao chegou atrasado na rua tal"));
        db.ChangeTracker.Clear();

        await amb.Contatos.AnonimizarAsync(amb.Contato.Id, default);

        db.ChangeTracker.Clear();
        var p = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).SingleAsync();

        Assert.Null(p.Comentario);
        Assert.Equal((short)7, p.Nota);
        Assert.Equal(StatusPesquisaNps.Respondida, p.Status);
    }

    /// <summary>Varias pesquisas do mesmo contato — uma por venda — perdem o comentario TODAS. Um
    /// `First` no lugar do `ExecuteUpdate` deixaria as outras para tras.</summary>
    [Fact]
    public async Task ANONIMIZAR_APAGA_O_COMENTARIO_DE_TODAS_AS_PESQUISAS()
    {
        var (db, tx, amb) = await PrepararAsync("lgpd-varias");
        using var _ = db; using var __ = tx;

        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, nota: 7, comentario: "um");
        await PesquisaAsync(db, amb, StatusPesquisaNps.Respondida, nota: 3, comentario: "dois");

        await amb.Contatos.AnonimizarAsync(amb.Contato.Id, default);

        db.ChangeTracker.Clear();
        var comentarios = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.ContatoId == amb.Contato.Id).Select(x => x.Comentario).ToListAsync();

        Assert.Equal(2, comentarios.Count);
        Assert.All(comentarios, Assert.Null);
    }

    // ==================================================================== o isolamento

    /// <summary>⚠️ AQUI O FILTRO GLOBAL E QUE PROTEGE — este servico roda na REQUISICAO, com tenant
    /// no contexto. O teste prova que ninguem precisou de `IgnoreQueryFilters` para fazer
    /// funcionar: a pesquisa da vizinha nao volta, e o gesto falha com "nao encontrada".</summary>
    [Fact]
    public async Task OS_GESTOS_NAO_ALCANCAM_A_PESQUISA_DA_VIZINHA()
    {
        var (db, tx, amb) = await PrepararAsync("tenant");
        using var _ = db; using var __ = tx;

        var vizinha = await Semeador.TenantAsync(db, "nps-hum-vizinha");
        var ambDela = amb with { Cenario = vizinha, Contato = vizinha.Contato, Conversa = vizinha.Conversa };
        var dela = await PesquisaAsync(db, ambDela, StatusPesquisaNps.PossivelNota, nota: 2);

        foreach (var gesto in new Func<Task>[]
        {
            () => amb.Servico.ConfirmarNotaAsync(dela, default),
            () => amb.Servico.NaoEhNotaAsync(dela, default),
            () => amb.Servico.CancelarAsync(dela, default)
        })
        {
            var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(gesto);
            Assert.Equal("Pesquisa não encontrada.", erro.Message);
        }

        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.PossivelNota,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == dela).Select(x => x.Status).SingleAsync());
    }

    // ==================================================================== o andaime

    private sealed record Ambiente(
        Cenario Cenario, Contato Contato, Conversa Conversa,
        IServicoPesquisaNps Servico, IServicoConfiguracao Config, IServicoContatos Contatos,
        ILeituraDaResposta Leitura, ClienteWhatsAppFalso Cliente);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        var trilha = new ColetorAuditoria();
        var db = banco.NovoContexto(ctx, relogio, trilha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"nps-hum-{sufixo}");

        // ⚠️ COM TENANT, ao contrario das outras suites de NPS: este servico roda na REQUISICAO.
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var cliente = new ClienteWhatsAppFalso();

        var enviador = new EnviadorMensagem(
            new DadosMensagem(db, relogio), cliente,
            new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero },
            relogio, NullLogger<EnviadorMensagem>.Instance);

        var acoes = new AcoesDaNota(db, enviador, relogio, NullLogger<AcoesDaNota>.Instance);

        return (db, tx, new Ambiente(
            cenario, cenario.Contato, cenario.Conversa,
            new ServicoPesquisaNps(db, ctx, acoes, trilha, relogio),
            new ServicoConfiguracao(db, trilha),
            new ServicoContatos(
                db, ctx, PublicadorDeTeste.Novo(db, relogio),
                PublicadorConversoesDeTeste.Novo(db, relogio), trilha, relogio),
            new LeituraDaResposta(db, acoes, relogio),
            cliente));
    }

    private static async Task ConfigurarAsync(
        NexoraDbContext db, long empresaId, string? promotor = null, string? detrator = null)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == empresaId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.NpsMensagemPromotor, promotor)
                .SetProperty(e => e.NpsMensagemDetrator, detrator));
        db.ChangeTracker.Clear();
    }

    private static async Task<long> PesquisaAsync(
        NexoraDbContext db, Ambiente amb, StatusPesquisaNps status, short? nota,
        string? comentario = null)
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
            ResponsavelId = amb.Cenario.Dono.Id,
            GanhaEm = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            ConcluidaEm = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)
        };
        db.Negociacoes.Add(negocio);
        await db.SaveChangesAsync();

        var pesquisa = new PesquisaNps
        {
            EmpresaId = amb.Cenario.Id,
            NegociacaoId = negocio.Id,
            ContatoId = amb.Contato.Id,
            Status = status,
            Nota = nota,
            Comentario = comentario,
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
