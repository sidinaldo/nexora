using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.CloudApi;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>OS TEMPLATES DA API OFICIAL (INT-XX), contra Postgres real. So a Graph API e duble.
///
/// O que estes testes seguram: so o rascunho muda de texto; a Meta recebe o corpo NUMERADO, com
/// exemplo; a decisao dela chega pelos tres caminhos (o "Atualizar", o webhook e o verificador) e
/// e traduzida igual; e o template de uma empresa nao e visto nem mexido pela outra.</summary>
[Collection("banco")]
public class ModelosDbTests(BancoTeste banco)
{
    private const string Token = "EAAG-token-modelos";
    private const string Corpo = "Olá {{nome}}, aqui é da {{empresa}}. Podemos continuar?";

    private sealed record Ambiente(
        Cenario Cenario, Conexao Oficial, ContextoMutavel Ctx, ServicoModelos Servico,
        ClienteCloudApiFalso Meta, CifraSegredos Cifra);

    // ==================================================================== o rascunho
    [Fact]
    public async Task O_RASCUNHO_GUARDA_O_NOME_DA_META_E_AS_VARIAVEIS_SEM_FALAR_COM_ELA()
    {
        var (db, tx, amb) = await PrepararAsync("rascunho");
        using var _ = db; using var __ = tx;

        await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("Boas-vindas à loja", "utility", Corpo), default);

        var dto = Assert.Single(await amb.Servico.ListarAsync(amb.Oficial.Id, default));
        Assert.Equal("boas_vindas_a_loja", dto.Nome);
        Assert.Equal("utility", dto.Categoria);
        Assert.Equal("pt_BR", dto.Idioma);
        Assert.Equal(["nome", "empresa"], dto.Variaveis);
        Assert.Equal("rascunho", dto.Status);
        Assert.Empty(amb.Meta.ModelosCriados);
    }

    [Fact]
    public async Task CONEXAO_POR_QR_CODE_NAO_TEM_TEMPLATE()
    {
        var (db, tx, amb) = await PrepararAsync("qr");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.CriarAsync(
            amb.Cenario.Conexao.Id, new NovoModelo("aviso", "utility", Corpo), default));

        Assert.Contains("API oficial", erro.Message);
    }

    /// <summary>A Meta identifica o template por nome + idioma na conta. O repetido e recusado aqui,
    /// antes de ir para a revisao.</summary>
    [Fact]
    public async Task NOME_REPETIDO_NO_MESMO_IDIOMA_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("repetido");
        using var _ = db; using var __ = tx;

        await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("retomada", "utility", Corpo), default);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.CriarAsync(
            amb.Oficial.Id, new NovoModelo("Retomada", "marketing", Corpo), default));
        Assert.True(erro.Conflito);

        // Outro idioma e outro template, para a Meta tambem.
        await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("retomada", "utility", Corpo, "en_US"), default);
        Assert.Equal(2, (await amb.Servico.ListarAsync(amb.Oficial.Id, default)).Count);
    }

    [Fact]
    public async Task AUTENTICACAO_NAO_E_CRIADO_POR_AQUI()
    {
        var (db, tx, amb) = await PrepararAsync("autenticacao");
        using var _ = db; using var __ = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.CriarAsync(
            amb.Oficial.Id, new NovoModelo("codigo", "authentication", Corpo), default));

        Assert.Contains("autenticação", erro.Message);
    }

    // ==================================================================== a revisao da Meta
    /// <summary>A Meta recebe o corpo NUMERADO, com um exemplo por variavel, pelo token da conexao.
    /// Depois de enviado, o texto nao muda mais — nem some.</summary>
    [Fact]
    public async Task ENVIAR_A_META_MANDA_O_CORPO_NUMERADO_E_TRAVA_O_TEXTO()
    {
        var (db, tx, amb) = await PrepararAsync("submete");
        using var _ = db; using var __ = tx;
        var id = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("retomada", "utility", Corpo), default);

        var dto = await amb.Servico.SubmeterAsync(id, default);

        Assert.Equal("enviado", dto.Status);
        var (waba, nome, categoria, idioma, corpo, exemplos) = Assert.Single(amb.Meta.ModelosCriados);
        Assert.Equal(amb.Oficial.WabaId, waba);
        Assert.Equal("retomada", nome);
        Assert.Equal("utility", categoria);
        Assert.Equal("pt_BR", idioma);
        Assert.Equal("Olá {{1}}, aqui é da {{2}}. Podemos continuar?", corpo);
        Assert.Equal(2, exemplos.Count);
        Assert.Contains(Token, amb.Meta.TokensUsados);

        db.ChangeTracker.Clear();
        Assert.Equal("594425479261591", (await db.ModelosMensagem.IgnoreQueryFilters().SingleAsync(m => m.Id == id)).IdMeta);

        var editar = await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Servico.EditarAsync(id, new NovoModelo("retomada", "utility", "Outro texto, {{nome}}."), default));
        Assert.True(editar.Conflito);
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.ExcluirAsync(id, default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.SubmeterAsync(id, default));
    }

    /// <summary>Template de utilidade as vezes sai aprovado na hora: a resposta da criacao ja vale.</summary>
    [Fact]
    public async Task APROVADO_NA_HORA_JA_SAI_APROVADO()
    {
        var (db, tx, amb) = await PrepararAsync("na-hora");
        using var _ = db; using var __ = tx;
        amb.Meta.StatusAoCriar = "APPROVED";
        var id = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("retomada", "utility", Corpo), default);

        Assert.Equal("aprovado", (await amb.Servico.SubmeterAsync(id, default)).Status);
    }

    /// <summary>A Meta recusou o PEDIDO: o erro dela sobe, e o template continua rascunho para corrigir.</summary>
    [Fact]
    public async Task A_META_RECUSANDO_O_PEDIDO_O_RASCUNHO_FICA()
    {
        var (db, tx, amb) = await PrepararAsync("recusa-pedido");
        using var _ = db; using var __ = tx;
        amb.Meta.RecusaDoModelo = "A Meta recusou o template: Já existe conteúdo neste idioma.";
        var id = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("retomada", "utility", Corpo), default);

        await Assert.ThrowsAsync<IntegracaoWhatsAppException>(() => amb.Servico.SubmeterAsync(id, default));

        db.ChangeTracker.Clear();
        var modelo = await db.ModelosMensagem.IgnoreQueryFilters().SingleAsync(m => m.Id == id);
        Assert.Equal(StatusModelo.Rascunho, modelo.Status);
        Assert.Null(modelo.IdMeta);
    }

    /// <summary>O "Atualizar" da tela pergunta a Meta. Recusado, ele mostra o motivo — e ai pode ser
    /// apagado, porque nao vai sair nunca.</summary>
    [Fact]
    public async Task ATUALIZAR_TRAZ_A_RECUSA_COM_O_MOTIVO_E_LIBERA_APAGAR()
    {
        var (db, tx, amb) = await PrepararAsync("atualizar");
        using var _ = db; using var __ = tx;
        var id = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("retomada", "utility", Corpo), default);
        var idMeta = (await SubmeterAsync(db, amb, id));
        amb.Meta.Revisoes[idMeta] = new ModeloNaMeta(idMeta, "REJECTED", "INVALID_FORMAT");

        var dto = await amb.Servico.SincronizarAsync(id, default);

        Assert.Equal("rejeitado", dto.Status);
        Assert.Contains("Formato inválido", dto.MotivoRejeicao);

        await amb.Servico.ExcluirAsync(id, default);
        Assert.Empty(await amb.Servico.ListarAsync(amb.Oficial.Id, default));
    }

    /// <summary>A decisao chega pelo WEBHOOK da Meta, com o id como numero. Sem tenant no contexto,
    /// como a fila roda — e so mexe no template da empresa que recebeu.</summary>
    [Fact]
    public async Task O_WEBHOOK_APROVA_SO_O_TEMPLATE_DA_EMPRESA_QUE_RECEBEU()
    {
        var (db, tx, amb) = await PrepararAsync("webhook");
        using var _ = db; using var __ = tx;
        var id = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("retomada", "utility", Corpo), default);
        var idMeta = await SubmeterAsync(db, amb, id);
        var outra = await Semeador.TenantAsync(db, "modelos-webhook-outra");

        var processador = Processador(db, amb);
        amb.Ctx.EmpresaId = 0;

        // A mesma decisao, entregue para OUTRA empresa: nao e o template dela.
        await processador.ProcessarAsync(Evento(outra.Id, outra.Conexao.Id, idMeta, "APPROVED"), default);
        Assert.Equal(StatusModelo.Enviado, await StatusAsync(db, id));

        await processador.ProcessarAsync(Evento(amb.Cenario.Id, amb.Oficial.Id, idMeta, "APPROVED"), default);
        Assert.Equal(StatusModelo.Aprovado, await StatusAsync(db, id));

        await processador.ProcessarAsync(Evento(amb.Cenario.Id, amb.Oficial.Id, idMeta, "PAUSED"), default);
        Assert.Equal(StatusModelo.Rejeitado, await StatusAsync(db, id));
    }

    /// <summary>O verificador de 5 minutos pergunta por todo template EM REVISAO — e so por ele. Um que
    /// a Meta nao acha nao impede o outro de ser atualizado.</summary>
    [Fact]
    public async Task O_VERIFICADOR_PERGUNTA_SO_PELOS_EM_REVISAO_E_UM_ERRO_NAO_PARA_OS_OUTROS()
    {
        var (db, tx, amb) = await PrepararAsync("verificador");
        using var _ = db; using var __ = tx;
        var perdido = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("perdido", "utility", Corpo), default);
        var aprovado = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("aprovado", "utility", Corpo), default);
        var rascunho = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("rascunho", "utility", Corpo), default);
        var idPerdido = await SubmeterAsync(db, amb, perdido);
        var idAprovado = await SubmeterAsync(db, amb, aprovado);
        amb.Meta.Revisoes[idAprovado] = new ModeloNaMeta(idAprovado, "APPROVED", null);
        amb.Ctx.EmpresaId = 0;

        var sincronizador = new SincronizadorModelos(
            db, amb.Meta, amb.Cifra, NullLogger<SincronizadorModelos>.Instance);

        Assert.Equal(1, await sincronizador.ExecutarAsync(default));
        Assert.Equal(StatusModelo.Aprovado, await StatusAsync(db, aprovado));
        Assert.Equal(StatusModelo.Enviado, await StatusAsync(db, perdido));
        Assert.Equal(StatusModelo.Rascunho, await StatusAsync(db, rascunho));
        Assert.Equal([idPerdido, idAprovado], amb.Meta.ModelosLidos);
    }

    // ==================================================================== as automacoes (etapa 8)
    /// <summary>A automacao so aceita template APROVADO desta empresa. A FK e simples: e esta
    /// checagem — e o query filter — que segura o template de outra empresa do lado de fora.</summary>
    [Fact]
    public async Task A_AUTOMACAO_SO_ACEITA_TEMPLATE_APROVADO_DESTA_EMPRESA()
    {
        var (db, tx, amb) = await PrepararAsync("automacao");
        using var _ = db; using var __ = tx;
        var rascunho = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("rascunho", "utility", Corpo), default);
        var aprovado = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("aprovado", "utility", Corpo), default);
        await db.ModelosMensagem.IgnoreQueryFilters().Where(m => m.Id == aprovado)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, StatusModelo.Aprovado));

        var naoAprovado = await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Servico.DefinirAutomacoesAsync(new EscolhaDasAutomacoes(rascunho, null, null), default));
        Assert.Contains("aprovado", naoAprovado.Message);

        await amb.Servico.DefinirAutomacoesAsync(new EscolhaDasAutomacoes(aprovado, null, aprovado), default);

        var escolha = await amb.Servico.AutomacoesAsync(default);
        Assert.Equal(aprovado, escolha.FollowUp);
        Assert.Null(escolha.Lembrete);
        Assert.Equal(aprovado, escolha.Nps);
        var opcao = Assert.Single(escolha.Aprovados);
        Assert.Equal("aprovado", opcao.Nome);
        Assert.Equal("Oficial", opcao.Conexao);

        // O template aprovado de OUTRA empresa nao e achado.
        var outra = await Semeador.TenantAsync(db, "modelos-automacao-outra");
        amb.Ctx.EmpresaId = outra.Id;
        var deOutra = await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Servico.DefinirAutomacoesAsync(new EscolhaDasAutomacoes(aprovado, null, null), default));
        Assert.Equal(404, deOutra.StatusHttp);
    }

    /// <summary>Apagar o template (o recusado) so desfaz a escolha: a FK e SET NULL, e a automacao
    /// passa a nao sair com a janela fechada, dizendo por que.</summary>
    [Fact]
    public async Task APAGAR_O_TEMPLATE_RECUSADO_DESFAZ_A_ESCOLHA()
    {
        var (db, tx, amb) = await PrepararAsync("apaga-escolhido");
        using var _ = db; using var __ = tx;
        var id = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("retomada", "utility", Corpo), default);
        await db.ModelosMensagem.IgnoreQueryFilters().Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, StatusModelo.Aprovado));
        await amb.Servico.DefinirAutomacoesAsync(new EscolhaDasAutomacoes(id, id, id), default);
        await db.ModelosMensagem.IgnoreQueryFilters().Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, StatusModelo.Rejeitado));
        db.ChangeTracker.Clear();

        await amb.Servico.ExcluirAsync(id, default);

        var escolha = await amb.Servico.AutomacoesAsync(default);
        Assert.Null(escolha.FollowUp);
        Assert.Null(escolha.Lembrete);
        Assert.Null(escolha.Nps);
    }

    // ==================================================================== isolamento
    [Fact]
    public async Task TEMPLATE_DE_OUTRA_EMPRESA_NAO_E_VISTO_NEM_MEXIDO()
    {
        var (db, tx, amb) = await PrepararAsync("isolamento");
        using var _ = db; using var __ = tx;
        var id = await amb.Servico.CriarAsync(amb.Oficial.Id, new NovoModelo("retomada", "utility", Corpo), default);

        var outra = await Semeador.TenantAsync(db, "modelos-isolamento-outra");
        amb.Ctx.EmpresaId = outra.Id;
        amb.Ctx.UsuarioId = outra.Dono.Id;

        var listar = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Servico.ListarAsync(amb.Oficial.Id, default));
        Assert.Equal(404, listar.StatusHttp);

        var editar = await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Servico.EditarAsync(id, new NovoModelo("roubado", "utility", Corpo), default));
        Assert.Equal(404, editar.StatusHttp);

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.ExcluirAsync(id, default));
        Assert.Equal(StatusModelo.Rascunho, await StatusAsync(db, id));
    }

    // ==================================================================== apoio
    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"modelos-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var cifra = CifraDeTeste.Nova();
        var oficial = new Conexao
        {
            EmpresaId = cenario.Id, Nome = "Oficial", InstanceName = $"cloud-modelos-{sufixo}",
            Canal = CanalWhatsapp.CloudApi, PhoneNumberId = "1090000000701", WabaId = "2090000000701",
            AccessTokenCifrado = cifra.Cifrar(Token, FinalidadeSegredo.AccessToken),
            AppSecretCifrado = cifra.Cifrar("seg", FinalidadeSegredo.AppSecret),
            Status = StatusConexao.Conectado
        };
        db.Conexoes.Add(oficial);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var meta = new ClienteCloudApiFalso();
        var servico = new ServicoModelos(db, meta, cifra);
        return (db, tx, new Ambiente(cenario, oficial, ctx, servico, meta, cifra));
    }

    /// <summary>Envia a revisao e devolve o id que a "Meta" deu.</summary>
    private static async Task<string> SubmeterAsync(NexoraDbContext db, Ambiente amb, long id)
    {
        await amb.Servico.SubmeterAsync(id, default);
        db.ChangeTracker.Clear();
        return (await db.ModelosMensagem.IgnoreQueryFilters().SingleAsync(m => m.Id == id)).IdMeta!;
    }

    private static async Task<StatusModelo> StatusAsync(NexoraDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return await db.ModelosMensagem.IgnoreQueryFilters().Where(m => m.Id == id).Select(m => m.Status).SingleAsync();
    }

    private static ProcessadorWebhookCloudApi Processador(NexoraDbContext db, Ambiente amb) =>
        new(db, new ArmazenamentoFalso(), new NotificadorFalso(), PublicadorDeTeste.Novo(db),
            PublicadorConversoesDeTeste.Novo(db), LeituraNpsDeTeste.Novo(db, TimeProvider.System),
            amb.Meta, amb.Cifra, TimeProvider.System, NullLogger<ProcessadorWebhookCloudApi>.Instance);

    /// <summary>Uma linha da fila com a decisao da Meta, como o webhook a grava. O id vai como NUMERO.</summary>
    private static WebhookMetaRecebido Evento(long empresaId, long conexaoId, string idMeta, string evento) => new()
    {
        EmpresaId = empresaId,
        ConexaoId = conexaoId,
        Campo = "message_template_status_update",
        Payload = $$"""
            {"field":"message_template_status_update","value":{"event":"{{evento}}",
             "message_template_id":{{idMeta}},"message_template_name":"retomada",
             "message_template_language":"pt_BR","reason":"NONE"} }
            """,
        RecebidoEm = DateTime.UtcNow
    };
}
