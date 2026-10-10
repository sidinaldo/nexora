using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Entidades;
using Nexora.Core.FollowUp;
using Nexora.Core.Nps;
using Nexora.Core.Seguranca;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Evolution;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Whatsapp;

namespace Nexora.Tests.Integracao;

/// <summary>AS AUTOMATICAS PELA API OFICIAL (INT-XX): follow-up, lembrete e pesquisa saem dias depois
/// de o cliente escrever — quase sempre com a janela de 24h fechada, quando a Meta so aceita
/// template aprovado.
///
/// O que estes testes seguram: com a janela fechada sai o template da automacao, preenchido; sem
/// template, NADA sai — e o motivo fica na thread (follow-up) ou a pesquisa espera (NPS); e o
/// lembrete automatico usa o template do follow-up, o manual o dele.
///
/// Os motores rodam SEM tenant, pelo `RoteadorWhatsApp` de verdade. So a Graph API e duble.</summary>
[Collection("banco")]
public class AutomacoesCloudApiDbTests(BancoTeste banco)
{
    /// <summary>Quinta, 06/08/2026, 10h30 de Brasilia — dentro do horario de atendimento.</summary>
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    private static readonly DateOnly Hoje = new(2026, 8, 6);

    private const string Corpo = "Oi {{nome}}, aqui é o {{vendedor}} da {{empresa}}. Ainda tem interesse?";

    // ==================================================================== a decisao
    [Fact]
    public async Task COM_A_JANELA_ABERTA_E_TEXTO_LIVRE()
    {
        var (db, tx, amb) = await PrepararAsync("aberta", horasDesdeOCliente: 2);
        using var _ = db; using var __ = tx;
        await EscolherAsync(db, amb, nps: await ModeloAsync(db, amb, StatusModelo.Aprovado));

        var saida = await amb.Saida.DecidirAsync(Linha(amb), TipoAutomacao.Nps, default);

        Assert.Null(saida.Modelo);
        Assert.False(saida.Descartada);
    }

    /// <summary>Na Evolution a janela nao manda em nada: texto livre, mesmo depois de dias.</summary>
    [Fact]
    public async Task NA_EVOLUTION_E_SEMPRE_TEXTO_LIVRE()
    {
        var (db, tx, amb) = await PrepararAsync("evolution", horasDesdeOCliente: 100);
        using var _ = db; using var __ = tx;

        var saida = await amb.Saida.DecidirAsync(
            Linha(amb, conexao: amb.Cenario.Conexao), TipoAutomacao.Nps, default);

        Assert.Null(saida.Modelo);
        Assert.False(saida.Descartada);
    }

    [Fact]
    public async Task JANELA_FECHADA_SEM_TEMPLATE_ESCOLHIDO_NAO_SAI_E_DIZ_POR_QUE()
    {
        var (db, tx, amb) = await PrepararAsync("sem-escolha", horasDesdeOCliente: 30);
        using var _ = db; using var __ = tx;

        var saida = await amb.Saida.DecidirAsync(Linha(amb), TipoAutomacao.Nps, default);

        Assert.True(saida.Descartada);
        Assert.Contains("não há template escolhido para a pesquisa de satisfação", saida.Motivo);
    }

    /// <summary>A escolha pode ter envelhecido: a Meta pausou o template, ou ele e de outro numero.
    /// O envio confere de novo, e nao manda o que a Meta recusaria.</summary>
    [Fact]
    public async Task TEMPLATE_PAUSADO_OU_DE_OUTRO_NUMERO_NAO_SAI()
    {
        var (db, tx, amb) = await PrepararAsync("envelheceu", horasDesdeOCliente: 30);
        using var _ = db; using var __ = tx;

        await EscolherAsync(db, amb, nps: await ModeloAsync(db, amb, StatusModelo.Rejeitado));
        Assert.Contains("não está aprovado",
            (await amb.Saida.DecidirAsync(Linha(amb), TipoAutomacao.Nps, default)).Motivo);

        var outro = await OutraConexaoOficialAsync(db, amb);
        await EscolherAsync(db, amb, nps: await ModeloAsync(db, amb, StatusModelo.Aprovado, outro.Id, "do_outro"));
        Assert.Contains("é de outro número",
            (await amb.Saida.DecidirAsync(Linha(amb), TipoAutomacao.Nps, default)).Motivo);
    }

    [Fact]
    public async Task JANELA_FECHADA_COM_TEMPLATE_APROVADO_SAI_PREENCHIDO_PELO_RESPONSAVEL()
    {
        var (db, tx, amb) = await PrepararAsync("preenche", horasDesdeOCliente: 30);
        using var _ = db; using var __ = tx;
        var id = await ModeloAsync(db, amb, StatusModelo.Aprovado);
        await EscolherAsync(db, amb, nps: id);

        var saida = await amb.Saida.DecidirAsync(Linha(amb), TipoAutomacao.Nps, default);

        Assert.Equal(id, saida.ModeloId);
        Assert.Equal("retomada", saida.Modelo!.Nome);
        Assert.Equal(["Contato", "Dono", "Empresa automacoes-preenche"], saida.Modelo.Parametros);
        Assert.Equal("Oi Contato, aqui é o Dono da Empresa automacoes-preenche. Ainda tem interesse?", saida.Texto);
    }

    /// <summary>A reserva grava os dois como `lembrete`; quem separa e a ORIGEM do lembrete. O que a
    /// rodada criou sozinha e o follow-up.</summary>
    [Fact]
    public async Task O_LEMBRETE_AUTOMATICO_USA_O_TEMPLATE_DO_FOLLOW_UP_E_O_MANUAL_O_DELE()
    {
        var (db, tx, amb) = await PrepararAsync("origem", horasDesdeOCliente: 30);
        using var _ = db; using var __ = tx;
        var doFollowUp = await ModeloAsync(db, amb, StatusModelo.Aprovado, nome: "follow_up");
        var doLembrete = await ModeloAsync(db, amb, StatusModelo.Aprovado, nome: "lembrete");
        await EscolherAsync(db, amb, followUp: doFollowUp, lembrete: doLembrete);

        var automatico = await LembreteAsync(db, amb, OrigemLembrete.Automatico);
        var manual = await LembreteAsync(db, amb, OrigemLembrete.Manual);

        Assert.Equal(doFollowUp, (await amb.Saida.DecidirAsync(
            Linha(amb, lembreteId: automatico), TipoAutomacao.Lembrete, default)).ModeloId);
        Assert.Equal(doLembrete, (await amb.Saida.DecidirAsync(
            Linha(amb, lembreteId: manual), TipoAutomacao.Lembrete, default)).ModeloId);
    }

    // ==================================================================== o follow-up
    [Fact]
    public async Task FOLLOW_UP_COM_A_JANELA_FECHADA_SAI_COMO_TEMPLATE()
    {
        var (db, tx, amb) = await PrepararAsync("follow-template", horasDesdeOCliente: 6 * 24);
        using var _ = db; using var __ = tx;
        var id = await ModeloAsync(db, amb, StatusModelo.Aprovado);
        await EscolherAsync(db, amb, followUp: id);
        await PararConversaAsync(db, amb, diasAtras: 5);

        var r = await FollowUp(db, amb).ExecutarAsync();

        Assert.Equal(1, r.Gerados);
        Assert.Equal(1, r.Enviados);
        Assert.Empty(amb.Meta.Enviadas);
        var (para, nome, _, _) = Assert.Single(amb.Meta.ModelosEnviados);
        Assert.Equal(amb.Cenario.Contato.Telefone, para);
        Assert.Equal("retomada", nome);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().SingleAsync(l => l.ContatoId == amb.Cenario.Contato.Id);
        Assert.Equal(StatusLembrete.Concluido, lembrete.Status);
        var linha = await db.Mensagens.IgnoreQueryFilters().SingleAsync(m => m.LembreteId == lembrete.Id);
        Assert.Equal(id, linha.ModeloId);
        Assert.StartsWith("Oi Contato, aqui é o Dono", linha.Texto);
        Assert.NotNull(linha.EnviadaEm);
    }

    /// <summary>===================== SEM TEMPLATE, NADA SAI — E A THREAD DIZ POR QUE =====================
    ///
    /// Mandar texto livre seria recusado pela Meta (131047) e viraria uma falha tentada todo dia. A
    /// linha nasce EXPIRADA com o motivo, ocupa a vaga do lembrete, e ele conclui: a rodada seguinte
    /// nao tenta de novo.
    /// =========================================================================================</summary>
    [Fact]
    public async Task SEM_TEMPLATE_O_FOLLOW_UP_NAO_SAI_E_O_MOTIVO_FICA_NA_THREAD()
    {
        var (db, tx, amb) = await PrepararAsync("follow-sem", horasDesdeOCliente: 6 * 24);
        using var _ = db; using var __ = tx;
        await PararConversaAsync(db, amb, diasAtras: 5);

        var r = await FollowUp(db, amb).ExecutarAsync();

        Assert.Equal(1, r.Descartados);
        Assert.Equal(0, r.Enviados);
        Assert.Equal(0, r.Falhas);
        Assert.Empty(amb.Meta.Enviadas);
        Assert.Empty(amb.Meta.ModelosEnviados);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().SingleAsync(l => l.ContatoId == amb.Cenario.Contato.Id);
        Assert.Equal(StatusLembrete.Concluido, lembrete.Status);
        var linha = await db.Mensagens.IgnoreQueryFilters().SingleAsync(m => m.LembreteId == lembrete.Id);
        Assert.NotNull(linha.ExpiradaEm);
        Assert.Null(linha.EnviadaEm);
        Assert.Contains("não há template escolhido para o follow-up", linha.Erro);

        // A rodada seguinte nao tenta de novo.
        var outra = await FollowUp(db, amb).ExecutarAsync();
        Assert.Equal(0, outra.Descartados + outra.Enviados + outra.Falhas);
    }

    /// <summary>O follow-up reservado com o numero fora do ar sai quando ele volta — e a decisao e
    /// feita DE NOVO na drenagem: a linha, reservada como texto, sai como template.
    ///
    /// BUG-XX: era "reservado fora do horario"; desde a rodada de hora em hora, fora do horario nada
    /// e reservado, e o numero caido e o que sobrou do reserve-defer.</summary>
    [Fact]
    public async Task O_FOLLOW_UP_ADIADO_E_DRENADO_COMO_TEMPLATE()
    {
        var (db, tx, amb) = await PrepararAsync("follow-drena", horasDesdeOCliente: 6 * 24);
        using var _ = db; using var __ = tx;
        var id = await ModeloAsync(db, amb, StatusModelo.Aprovado);
        await EscolherAsync(db, amb, followUp: id);
        await PararConversaAsync(db, amb, diasAtras: 5);

        amb.Meta.Estado = "close";
        var caido = await FollowUp(db, amb).ExecutarAsync();
        Assert.Equal(1, caido.Adiados);
        Assert.Empty(amb.Meta.ModelosEnviados);

        amb.Meta.Estado = "open";
        amb.Relogio.Avancar(TimeSpan.FromHours(1));
        var deManha = await FollowUp(db, amb).ExecutarAsync();

        Assert.Equal(1, deManha.Enviados);
        Assert.Single(amb.Meta.ModelosEnviados);
        Assert.Empty(amb.Meta.Enviadas);

        db.ChangeTracker.Clear();
        var linha = await db.Mensagens.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversaId == amb.Cenario.Conversa.Id && m.LembreteId != null);
        Assert.Equal(id, linha.ModeloId);
        Assert.StartsWith("Oi Contato", linha.Texto);
        Assert.NotNull(linha.EnviadaEm);
    }

    // ==================================================================== a pesquisa
    /// <summary>A pesquisa tem data propria: sem template, nada e reservado e ela fica para o dia
    /// seguinte — se o cliente escrever ate la, ou um template for escolhido, ela sai.</summary>
    [Fact]
    public async Task SEM_TEMPLATE_A_PESQUISA_FICA_PARA_O_DIA_SEGUINTE()
    {
        var (db, tx, amb) = await PrepararAsync("nps-sem", horasDesdeOCliente: 6 * 24);
        using var _ = db; using var __ = tx;
        await LigarNpsAsync(db, amb);
        var negociacao = await VendaConcluidaAsync(db, amb, Hoje.AddDays(-3));

        var r = await Nps(db, amb).ExecutarAsync();

        Assert.Equal(0, r.Enviadas);
        Assert.Equal(1, r.Adiadas);
        Assert.Empty(amb.Meta.ModelosEnviados);
        Assert.Empty(amb.Meta.Enviadas);

        db.ChangeTracker.Clear();
        var p = await db.PesquisasNps.IgnoreQueryFilters().SingleAsync(x => x.NegociacaoId == negociacao);
        Assert.Equal(StatusPesquisaNps.Agendada, p.Status);
        Assert.Equal(Hoje.AddDays(1), p.DataAgendada);
        Assert.False(await db.Mensagens.IgnoreQueryFilters()
            .AnyAsync(m => m.NegociacaoId == negociacao && m.TipoAutomacao == TipoAutomacao.Nps));
    }

    [Fact]
    public async Task COM_TEMPLATE_A_PESQUISA_SAI_COMO_TEMPLATE()
    {
        var (db, tx, amb) = await PrepararAsync("nps-template", horasDesdeOCliente: 6 * 24);
        using var _ = db; using var __ = tx;
        var id = await ModeloAsync(db, amb, StatusModelo.Aprovado, nome: "pesquisa");
        await EscolherAsync(db, amb, nps: id);
        await LigarNpsAsync(db, amb);
        var negociacao = await VendaConcluidaAsync(db, amb, Hoje.AddDays(-3));

        var r = await Nps(db, amb).ExecutarAsync();

        Assert.Equal(1, r.Enviadas);
        Assert.Equal("pesquisa", Assert.Single(amb.Meta.ModelosEnviados).Nome);
        Assert.Empty(amb.Meta.Enviadas);

        db.ChangeTracker.Clear();
        var p = await db.PesquisasNps.IgnoreQueryFilters().SingleAsync(x => x.NegociacaoId == negociacao);
        Assert.Equal(StatusPesquisaNps.Enviada, p.Status);
        var linha = await db.Mensagens.IgnoreQueryFilters().SingleAsync(m => m.Id == p.MensagemEnvioId);
        Assert.Equal(id, linha.ModeloId);
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, Conexao Oficial, RelogioFalso Relogio, ClienteCloudApiFalso Meta,
        EnviadorMensagem Enviador, SaidaDaAutomatica Saida);

    /// <summary>Uma empresa com a conversa num numero oficial, e o cliente sem escrever ha
    /// `horasDesdeOCliente` horas. Contexto SEM tenant: os motores sao jobs.</summary>
    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo, double horasDesdeOCliente, DateTimeOffset? quando = null)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"automacoes-{sufixo}");
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id != cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.Ativo, false));
        // A conversa semeada tem de ser velha: conversa viva nao leva robo (ver `MotorNpsDbTests`).
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(
                m => m.CriadoEm, new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc)));

        var relogio = new RelogioFalso(quando ?? QuintaDeManha);
        var cifra = CifraDeTeste.Nova();
        var oficial = new Conexao
        {
            EmpresaId = cenario.Id, Nome = "Oficial", InstanceName = $"cloud-automacoes-{sufixo}",
            Canal = CanalWhatsapp.CloudApi, PhoneNumberId = "1090000000801", WabaId = "2090000000801",
            AccessTokenCifrado = cifra.Cifrar("EAAG-automacoes", FinalidadeSegredo.AccessToken),
            AppSecretCifrado = cifra.Cifrar("seg", FinalidadeSegredo.AppSecret),
            Status = StatusConexao.Conectado
        };
        db.Conexoes.Add(oficial);
        await db.SaveChangesAsync();

        var ultimaEntrada = relogio.GetUtcNow().UtcDateTime.AddHours(-horasDesdeOCliente);
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == cenario.Conversa.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ConexaoId, oficial.Id)
                .SetProperty(c => c.UltimaEntradaEm, ultimaEntrada));
        db.ChangeTracker.Clear();

        // A Evolution do numero antigo da empresa responde "fora do ar" na hora: os motores
        // conferem todos os numeros, e este teste e sobre o oficial.
        var evolution = new ClienteEvolution(
            new HttpClient(new SemRede()) { BaseAddress = new Uri("http://evolution.invalid/") },
            NullLogger<ClienteEvolution>.Instance);
        var meta = new ClienteCloudApiFalso();
        var roteador = new RoteadorWhatsApp(db, evolution, meta, cifra);
        var saida = new SaidaDaAutomatica(db, relogio);

        var enviador = new EnviadorMensagem(
            new DadosMensagem(db, relogio), roteador,
            new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero }, relogio,
            NullLogger<EnviadorMensagem>.Instance, saida);

        return (db, tx, new Ambiente(cenario, oficial, relogio, meta, enviador, saida));
    }

    private sealed class SemRede : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage pedido, CancellationToken ct) =>
            throw new HttpRequestException("sem rede no teste");
    }

    private static MotorFollowUp FollowUp(NexoraDbContext db, Ambiente amb) =>
        new(new DadosFollowUp(db, amb.Relogio), amb.Enviador, amb.Relogio, NullLogger<MotorFollowUp>.Instance);

    private static MotorNps Nps(NexoraDbContext db, Ambiente amb) =>
        new(new DadosNps(db, amb.Relogio), new DadosFollowUp(db, amb.Relogio), amb.Enviador, amb.Relogio,
            NullLogger<MotorNps>.Instance);

    /// <summary>Uma linha de automatica da conversa — o que o enviador consulta. Pelo numero oficial,
    /// a menos que se diga outra conexao.</summary>
    private static Mensagem Linha(Ambiente amb, long? lembreteId = null, Conexao? conexao = null)
    {
        var por = conexao ?? amb.Oficial;
        return new Mensagem
        {
            EmpresaId = amb.Cenario.Id, ConversaId = amb.Cenario.Conversa.Id, ContatoId = amb.Cenario.Contato.Id,
            ConexaoId = por.Id, InstanceName = por.InstanceName, Direcao = DirecaoMensagem.Saida,
            LembreteId = lembreteId
        };
    }

    private static async Task<long> ModeloAsync(
        NexoraDbContext db, Ambiente amb, StatusModelo status, long? conexaoId = null, string nome = "retomada")
    {
        var modelo = new ModeloMensagem
        {
            EmpresaId = amb.Cenario.Id, ConexaoId = conexaoId ?? amb.Oficial.Id, WabaId = "2090000000801",
            Nome = nome, Categoria = CategoriaModelo.Utility, Idioma = "pt_BR", Corpo = Corpo,
            Variaveis = ["nome", "vendedor", "empresa"], Status = status, IdMeta = "594425479261500"
        };
        db.ModelosMensagem.Add(modelo);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return modelo.Id;
    }

    private static async Task EscolherAsync(
        NexoraDbContext db, Ambiente amb, long? followUp = null, long? lembrete = null, long? nps = null)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.ModeloFollowUpId, followUp)
                .SetProperty(e => e.ModeloLembreteId, lembrete)
                .SetProperty(e => e.ModeloNpsId, nps));
        db.ChangeTracker.Clear();
    }

    private static async Task<Conexao> OutraConexaoOficialAsync(NexoraDbContext db, Ambiente amb)
    {
        var cifra = CifraDeTeste.Nova();
        var outra = new Conexao
        {
            EmpresaId = amb.Cenario.Id, Nome = "Outro oficial", InstanceName = amb.Oficial.InstanceName + "-2",
            Canal = CanalWhatsapp.CloudApi, PhoneNumberId = "1090000000802", WabaId = "2090000000801",
            AccessTokenCifrado = cifra.Cifrar("EAAG-2", FinalidadeSegredo.AccessToken),
            AppSecretCifrado = cifra.Cifrar("seg", FinalidadeSegredo.AppSecret),
            Status = StatusConexao.Conectado
        };
        db.Conexoes.Add(outra);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return outra;
    }

    private static async Task<long> LembreteAsync(NexoraDbContext db, Ambiente amb, OrigemLembrete origem)
    {
        var agora = amb.Relogio.GetUtcNow().UtcDateTime;
        var lembrete = new Lembrete
        {
            EmpresaId = amb.Cenario.Id, ContatoId = amb.Cenario.Contato.Id, ConversaId = amb.Cenario.Conversa.Id,
            Origem = origem, Status = StatusLembrete.Pendente, DataAlvo = Hoje, Titulo = "Retomar",
            EnviaMensagem = true, TextoMensagem = "Oi!", CriadoEm = agora, AtualizadoEm = agora
        };
        db.Lembretes.Add(lembrete);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return lembrete.Id;
    }

    /// <summary>A ultima mensagem foi do vendedor, ha `diasAtras` dias: o caso do follow-up.</summary>
    private static async Task PararConversaAsync(NexoraDbContext db, Ambiente amb, int diasAtras)
    {
        var quando = amb.Relogio.GetUtcNow().UtcDateTime.AddDays(-diasAtras);
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Cenario.Conversa.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UltimaMensagemEm, quando)
                .SetProperty(c => c.UltimaMensagemDirecao, DirecaoMensagem.Saida)
                .SetProperty(c => c.UltimaMensagemPrevia, "última mensagem")
                .SetProperty(c => c.AguardandoDesde, (DateTime?)null));
        db.ChangeTracker.Clear();
    }

    private static async Task LigarNpsAsync(NexoraDbContext db, Ambiente amb)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.NpsAtivo, true));
        db.ChangeTracker.Clear();
    }

    private static async Task<long> VendaConcluidaAsync(NexoraDbContext db, Ambiente amb, DateOnly concluidaEm)
    {
        var etapa = amb.Cenario.Etapas[0];
        var negocio = new Negociacao
        {
            EmpresaId = amb.Cenario.Id, ContatoId = amb.Cenario.Contato.Id, PipelineId = etapa.PipelineId,
            EtapaId = etapa.Id, Status = StatusNegociacao.Concluida, Valor = 1000m,
            GanhaEm = concluidaEm.ToDateTime(new TimeOnly(12, 0)).ToUniversalTime(),
            ConcluidaEm = concluidaEm.ToDateTime(new TimeOnly(15, 0)).ToUniversalTime()
        };
        db.Negociacoes.Add(negocio);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return negocio.Id;
    }
}
