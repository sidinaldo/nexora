using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== NPS-1 ETAPA 2d — LER A NOTA NA RESPOSTA =====================
///
/// A decisao de "isto e uma nota?" NAO esta aqui: ela e do `LeitorDeNota`, que e puro e tem a suite
/// inteira em `LeitorDeNotaTests`. Esta suite cobre o que o banco precisa saber — ha pesquisa
/// esperando? qual mensagem foi citada? o que gravar? — e, sobretudo, O QUE A NOTA NAO FAZ.
///
/// ⚠️ O QUE MAIS IMPORTA AQUI E O SEMAFORO. "10" nao e pergunta: ninguem tem de responder. Mas
/// "quero 2 unidades" E um pedido esperando resposta, e apagar a espera dele para perguntar "isto e
/// uma nota?" trocaria um atendimento perdido por uma duvida respondida.
/// ====================================================================================</summary>
[Collection("banco")]
public class LeituraDaRespostaDbTests(BancoTeste banco)
{
    private static readonly DateOnly Hoje = new(2026, 8, 6);

    // ==================================================================== o caminho da nota

    [Fact]
    public async Task A_NOTA_E_REGISTRADA_COM_O_COMENTARIO()
    {
        var (db, tx, amb) = await PrepararAsync("nota");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaEnviadaAsync(db, amb);
        var mensagem = await EntradaAsync(db, amb, "10, adorei o atendimento");

        var r = await amb.Leitura.LerAsync(
            amb.Cenario.Id, amb.Contato.Id, mensagem, "10, adorei o atendimento", null, default);

        Assert.Equal(RespostaDaPesquisa.NotaRegistrada, r);

        db.ChangeTracker.Clear();
        var p = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).SingleAsync();

        Assert.Equal(StatusPesquisaNps.Respondida, p.Status);
        Assert.Equal((short)10, p.Nota);
        Assert.Equal("adorei o atendimento", p.Comentario);
        Assert.Equal(mensagem, p.MensagemRespostaId);
        Assert.NotNull(p.DataResposta);

        // ⚠️ NULO: foi o cliente que respondeu de forma que o leitor aceitou sozinho. E essa
        // distincao que permite medir depois se as regras do leitor estao apertadas.
        Assert.Null(p.ConfirmadaPorUsuarioId);
    }

    /// <summary>===================== A NOTA MARCA A MENSAGEM COMO TRATADA =====================
    ///
    /// ⚠️ ESTA COLUNA FICOU DE FORA DA ETAPA 1 DE PROPOSITO — "coluna sem leitor e coluna que
    /// ninguem sabe se esta certa". Agora ha leitor, e ela e o que diz ao resto do sistema que
    /// aquela entrada nao espera gente.
    /// ================================================================================</summary>
    [Fact]
    public async Task A_MENSAGEM_DA_NOTA_FICA_MARCADA_COMO_TRATADA()
    {
        var (db, tx, amb) = await PrepararAsync("tratada");
        using var _ = db; using var __ = tx;

        await PesquisaEnviadaAsync(db, amb);
        var mensagem = await EntradaAsync(db, amb, "9");

        await amb.Leitura.LerAsync(amb.Cenario.Id, amb.Contato.Id, mensagem, "9", null, default);

        db.ChangeTracker.Clear();
        Assert.True(await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Id == mensagem).Select(m => m.TratadaPorAutomacao).SingleAsync());
    }

    // ==================================================================== a duvida

    /// <summary>===================== A DUVIDA NAO E RESPOSTA =====================
    ///
    /// ⚠️ A NOTA E GRAVADA, MAS O STATUS NAO E `Respondida` — e e o status que o relatorio le. Ele
    /// NUNCA le `nota IS NOT NULL`, e e por isso que `ck_pesquisas_nps_respondida` nao cobre
    /// `possivel_nota`: ali o numero e uma SUSPEITA.
    ///
    /// ⚠️ E A MENSAGEM *NAO* E MARCADA COMO TRATADA: ela pode nao ser nota nenhuma.
    /// ==================================================================</summary>
    [Fact]
    public async Task A_DUVIDA_GRAVA_A_SUSPEITA_E_NAO_MARCA_A_MENSAGEM()
    {
        var (db, tx, amb) = await PrepararAsync("duvida");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaEnviadaAsync(db, amb);
        var mensagem = await EntradaAsync(db, amb, "quero 2 unidades");

        var r = await amb.Leitura.LerAsync(
            amb.Cenario.Id, amb.Contato.Id, mensagem, "quero 2 unidades", null, default);

        Assert.Equal(RespostaDaPesquisa.DuvidaRegistrada, r);

        db.ChangeTracker.Clear();
        var p = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).SingleAsync();

        Assert.Equal(StatusPesquisaNps.PossivelNota, p.Status);
        Assert.Equal((short)2, p.Nota);
        Assert.Null(p.DataResposta);

        Assert.False(await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Id == mensagem).Select(m => m.TratadaPorAutomacao).SingleAsync());
    }

    // ==================================================================== a citação

    /// <summary>===================== CITAR A PESQUISA VENCE =====================
    ///
    /// "quero 2 unidades" solta e duvida; citando a pergunta, e a resposta aquela mensagem. O
    /// prompt poe a citacao em primeiro lugar na ordem das regras.
    ///
    /// ⚠️ MEDIDO NO `nexora_dev`: so 3 de 1440 entradas tem `stanzaId`. E o sinal mais forte quando
    /// aparece, e os outros caminhos e que carregam o trabalho.
    /// ==============================================================</summary>
    [Fact]
    public async Task CITAR_A_PESQUISA_TRANSFORMA_DUVIDA_EM_NOTA()
    {
        var (db, tx, amb) = await PrepararAsync("citou");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaEnviadaAsync(db, amb, waIdDoEnvio: "WAMID-PERGUNTA");
        var mensagem = await EntradaAsync(db, amb, "quero 2 unidades");

        var r = await amb.Leitura.LerAsync(
            amb.Cenario.Id, amb.Contato.Id, mensagem, "quero 2 unidades", "WAMID-PERGUNTA", default);

        Assert.Equal(RespostaDaPesquisa.NotaRegistrada, r);

        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.Respondida,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == pesquisa).Select(x => x.Status).SingleAsync());
    }

    /// <summary>===================== CITAR *OUTRA* COISA NAO CONTA =====================
    ///
    /// ⚠️ MEDIDO: das tres entradas com `stanzaId` no `nexora_dev`, UMA cita outra ENTRADA — o
    /// cliente citando a propria mensagem. "Houve citacao" nao basta; tem de ser a citacao DESTA
    /// pergunta. Com o teste anterior sozinho, trocar a conferencia por `stanzaId != null` passaria.
    /// ======================================================================</summary>
    [Fact]
    public async Task CITAR_OUTRA_MENSAGEM_NAO_CONTA_COMO_CITAR_A_PESQUISA()
    {
        var (db, tx, amb) = await PrepararAsync("citou-outra");
        using var _ = db; using var __ = tx;

        await PesquisaEnviadaAsync(db, amb, waIdDoEnvio: "WAMID-PERGUNTA");
        var mensagem = await EntradaAsync(db, amb, "quero 2 unidades");

        var r = await amb.Leitura.LerAsync(
            amb.Cenario.Id, amb.Contato.Id, mensagem, "quero 2 unidades", "WAMID-OUTRA", default);

        // Cai nos criterios normais: duvida, nao nota.
        Assert.Equal(RespostaDaPesquisa.DuvidaRegistrada, r);
    }

    // ==================================================================== quando nao ha pesquisa

    /// <summary>⚠️ SEM PESQUISA ABERTA, "10" E SO UMA MENSAGEM. O caminho quente sai antes de olhar
    /// o texto: toda mensagem recebida passa por aqui, e o cliente que escreve "10" numa conversa
    /// normal nao pode ter a mensagem engolida.</summary>
    [Fact]
    public async Task SEM_PESQUISA_ABERTA_A_MENSAGEM_SEGUE_NORMAL()
    {
        var (db, tx, amb) = await PrepararAsync("sem-pesquisa");
        using var _ = db; using var __ = tx;

        var mensagem = await EntradaAsync(db, amb, "10");

        var r = await amb.Leitura.LerAsync(
            amb.Cenario.Id, amb.Contato.Id, mensagem, "10", null, default);

        Assert.Equal(RespostaDaPesquisa.Nenhuma, r);

        db.ChangeTracker.Clear();
        Assert.False(await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Id == mensagem).Select(m => m.TratadaPorAutomacao).SingleAsync());
    }

    /// <summary>Pesquisa EXPIRADA tambem nao le mais: `ix_pesquisas_nps_aberta` e parcial em
    /// `enviada`/`possivel_nota`, e o cliente que responde no quinto dia nao reabre nada.</summary>
    [Theory]
    [InlineData(StatusPesquisaNps.Expirada)]
    [InlineData(StatusPesquisaNps.Cancelada)]
    [InlineData(StatusPesquisaNps.Respondida)]
    [InlineData(StatusPesquisaNps.Agendada)]
    public async Task PESQUISA_FORA_DOS_DOIS_ESTADOS_ABERTOS_NAO_LE(StatusPesquisaNps status)
    {
        var (db, tx, amb) = await PrepararAsync($"fechada{status}");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaEnviadaAsync(db, amb);

        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.Id == pesquisa)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, status)
                // `ck_pesquisas_nps_respondida` exige nota quando respondida.
                .SetProperty(x => x.Nota, (short?)7));
        db.ChangeTracker.Clear();

        var mensagem = await EntradaAsync(db, amb, "10");

        var r = await amb.Leitura.LerAsync(
            amb.Cenario.Id, amb.Contato.Id, mensagem, "10", null, default);

        Assert.Equal(RespostaDaPesquisa.Nenhuma, r);
    }

    /// <summary>⚠️ MENSAGEM SEM TEXTO DEIXA A PESQUISA ABERTA. Audio, imagem e figurinha chegam
    /// assim, e o cliente que mandou um audio dizendo "dez" nao pode perder a vez.</summary>
    [Fact]
    public async Task MENSAGEM_SEM_TEXTO_NAO_FECHA_A_PESQUISA()
    {
        var (db, tx, amb) = await PrepararAsync("sem-texto");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaEnviadaAsync(db, amb);
        var mensagem = await EntradaAsync(db, amb, null);

        var r = await amb.Leitura.LerAsync(
            amb.Cenario.Id, amb.Contato.Id, mensagem, null, null, default);

        Assert.Equal(RespostaDaPesquisa.Nenhuma, r);

        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.Enviada,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == pesquisa).Select(x => x.Status).SingleAsync());
    }

    // ==================================================================== o isolamento

    /// <summary>⚠️ A LEITURA RODA SEM TENANT (e webhook), entao o recorte e o `empresa_id` passado a
    /// mao. Mandar o id de contato da vizinha nao pode achar a pesquisa dela.</summary>
    [Fact]
    public async Task A_LEITURA_NAO_ACHA_A_PESQUISA_DA_VIZINHA()
    {
        var (db, tx, amb) = await PrepararAsync("tenant");
        using var _ = db; using var __ = tx;

        var vizinha = await Semeador.TenantAsync(db, "nps-leitura-vizinha");
        var ambDela = amb with { Cenario = vizinha, Contato = vizinha.Contato, Conversa = vizinha.Conversa };
        var dela = await PesquisaEnviadaAsync(db, ambDela);

        var mensagem = await EntradaAsync(db, ambDela, "10");

        // A minha empresa perguntando pelo contato DELA.
        var r = await amb.Leitura.LerAsync(
            amb.Cenario.Id, vizinha.Contato.Id, mensagem, "10", null, default);

        Assert.Equal(RespostaDaPesquisa.Nenhuma, r);

        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.Enviada,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == dela).Select(x => x.Status).SingleAsync());
    }

    // ==================================================================== o andaime

    private sealed record Ambiente(
        Cenario Cenario, Contato Contato, Conversa Conversa, ILeituraDaResposta Leitura);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"nps-le-{sufixo}");

        // ⚠️ O CONTEXTO FICA VAZIO: a leitura roda dentro do webhook, sem tenant. E isso que prova
        // que o `empresa_id` passado a mao esta no lugar.
        return (db, tx, new Ambiente(
            cenario, cenario.Contato, cenario.Conversa,
            LeituraNpsDeTeste.Novo(db, TimeProvider.System)));
    }

    /// <summary>Uma pesquisa ja ENVIADA, com a mensagem da pergunta.</summary>
    private static async Task<long> PesquisaEnviadaAsync(
        NexoraDbContext db, Ambiente amb, string? waIdDoEnvio = null)
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
            DataDisparo = Hoje,
            WaMessageId = waIdDoEnvio
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
