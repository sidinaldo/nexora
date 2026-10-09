using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.CloudApi;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>A CONEXAO DA API OFICIAL (INT-XX).
///
/// O que estes testes seguram: nada e gravado sem a Meta confirmar o numero; os segredos ficam
/// cifrados e nunca voltam para a tela; e o numero de uma conta nao entra pela conta de outra.</summary>
[Collection("banco")]
public class ConexaoCloudApiDbTests(BancoTeste banco)
{
    private const string Token = "EAAG-token-de-verdade";
    private const string AppSecret = "segredo-do-app-123";

    private sealed record Ambiente(
        Cenario Cenario, ServicoConexoes Servico, ClienteCloudApiFalso Meta, CifraSegredos Cifra);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"cloud-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        // A empresa ja tem a conexao da Evolution do cenario: a oficial e a segunda.
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LimiteConexoes, (short)5));

        var meta = new ClienteCloudApiFalso();
        var cifra = CifraDeTeste.Nova();
        var servico = new ServicoConexoes(db, new ClienteWhatsAppFalso(), ctx, TimeProvider.System, meta, cifra);
        return (db, tx, new Ambiente(cenario, servico, meta, cifra));
    }

    private static NovaConexao Oficial(string pnid = "1090000000001") =>
        new("Oficial", "cloud_api", pnid, "2090000000001", Token, AppSecret);

    /// <summary>⚠️ ENCONTRADO NO PRIMEIRO TESTE COM A META: o numero de teste e americano (+1 555 637
    /// 0179). A regra brasileira (11 digitos = sem o 55) o virava "5515556370179", que nao existe no
    /// WhatsApp — e quem mandava mensagem para ele recebia "convidar". A Meta manda o numero completo.</summary>
    [Fact]
    public async Task O_NUMERO_FICA_COMO_A_META_MANDA_MESMO_ESTRANGEIRO()
    {
        var (db, tx, amb) = await PrepararAsync("estrangeiro");
        using var _ = db; using var __ = tx;
        amb.Meta.Numero = new NumeroCloud("15556370179", "Test Number", "GREEN", "APPROVED", "VERIFIED");

        var id = await amb.Servico.CriarAsync(Oficial(), default);

        db.ChangeTracker.Clear();
        Assert.Equal("15556370179", (await db.Conexoes.IgnoreQueryFilters().SingleAsync(c => c.Id == id)).Numero);
        Assert.Equal("15556370179", (await amb.Servico.TestarAsync(id, default)).Numero);
    }

    [Fact]
    public async Task A_CONEXAO_OFICIAL_GUARDA_OS_SEGREDOS_CIFRADOS_E_NUNCA_OS_DEVOLVE()
    {
        var (db, tx, amb) = await PrepararAsync("cria");
        using var _ = db; using var __ = tx;

        var id = await amb.Servico.CriarAsync(Oficial(), default);

        db.ChangeTracker.Clear();
        var conexao = await db.Conexoes.IgnoreQueryFilters().SingleAsync(c => c.Id == id);

        Assert.Equal(CanalWhatsapp.CloudApi, conexao.Canal);
        Assert.Equal($"cloud-{amb.Cenario.Id}-{id}", conexao.InstanceName);
        Assert.Equal(StatusConexao.Conectado, conexao.Status);
        Assert.Equal("5584912345678", conexao.Numero);
        Assert.Equal("Loja Teste", conexao.PerfilNome);
        Assert.Matches("^[0-9a-f]{32}$", conexao.VerifyToken!);
        Assert.Contains("2090000000001", amb.Meta.WabasAssinadas);

        // Cifrados no banco, e abrem de volta com a cifra certa.
        Assert.DoesNotContain(Token, conexao.AccessTokenCifrado);
        Assert.DoesNotContain(AppSecret, conexao.AppSecretCifrado);
        Assert.Equal(Token, amb.Cifra.Decifrar(conexao.AccessTokenCifrado!, FinalidadeSegredo.AccessToken));
        Assert.Equal(AppSecret, amb.Cifra.Decifrar(conexao.AppSecretCifrado!, FinalidadeSegredo.AppSecret));

        // A tela sabe que estao configurados — e so isso.
        var lista = await amb.Servico.ListarAsync(default);
        var dto = lista.Itens.Single(c => c.Id == id);
        Assert.True(dto.Oficial);
        Assert.True(dto.TokenConfigurado);
        Assert.True(dto.AppSecretConfigurado);

        var json = JsonSerializer.Serialize(lista);
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain(AppSecret, json);
        Assert.DoesNotContain(conexao.AccessTokenCifrado!, json);
    }

    /// <summary>Sem canal no pedido, vale o padrao da empresa — e ele e trocado pela tela de
    /// conexoes.</summary>
    [Fact]
    public async Task SEM_CANAL_VALE_O_PADRAO_DA_EMPRESA()
    {
        var (db, tx, amb) = await PrepararAsync("padrao");
        using var _ = db; using var __ = tx;

        Assert.Equal("evolution", (await amb.Servico.ListarAsync(default)).CanalPadrao);

        await amb.Servico.DefinirCanalPadraoAsync("cloud_api", default);
        Assert.Equal("cloud_api", (await amb.Servico.ListarAsync(default)).CanalPadrao);

        var id = await amb.Servico.CriarAsync(Oficial() with { Canal = null }, default);
        db.ChangeTracker.Clear();
        Assert.Equal(CanalWhatsapp.CloudApi, (await db.Conexoes.IgnoreQueryFilters().SingleAsync(c => c.Id == id)).Canal);

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Servico.DefinirCanalPadraoAsync("whatsapp", default));
    }

    /// <summary>A Meta recusou o token: o erro dela sobe para o formulario, e nada fica gravado.</summary>
    [Fact]
    public async Task A_META_RECUSANDO_NADA_E_GRAVADO()
    {
        var (db, tx, amb) = await PrepararAsync("recusa");
        using var _ = db; using var __ = tx;
        amb.Meta.Recusa = "A Meta recusou o token: ele expirou ou não vale para esta conta.";

        var erro = await Assert.ThrowsAsync<IntegracaoWhatsAppException>(
            () => amb.Servico.CriarAsync(Oficial(), default));

        Assert.Contains("token", erro.Message);
        db.ChangeTracker.Clear();
        Assert.False(await db.Conexoes.IgnoreQueryFilters()
            .AnyAsync(c => c.EmpresaId == amb.Cenario.Id && c.Canal == CanalWhatsapp.CloudApi));
    }

    /// <summary>O token enxerga o numero, mas o numero e de outra WABA: e o caso de cadastrar o
    /// numero de outra conta. Recusado antes de gravar.</summary>
    [Fact]
    public async Task NUMERO_DE_OUTRA_WABA_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("waba");
        using var _ = db; using var __ = tx;
        amb.Meta.NaWaba = false;

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.CriarAsync(Oficial(), default));

        db.ChangeTracker.Clear();
        Assert.False(await db.Conexoes.IgnoreQueryFilters()
            .AnyAsync(c => c.EmpresaId == amb.Cenario.Id && c.Canal == CanalWhatsapp.CloudApi));
    }

    /// <summary>O mesmo numero da Meta em outra empresa: recusado, e a mensagem nao diz de quem e.</summary>
    [Fact]
    public async Task O_MESMO_NUMERO_NAO_ENTRA_DUAS_VEZES()
    {
        var (db, tx, amb) = await PrepararAsync("repetido");
        using var _ = db; using var __ = tx;

        await amb.Servico.CriarAsync(Oficial("1090000000777"), default);

        var outra = await Semeador.TenantAsync(db, "cloud-repetido-b");
        var ctxB = new ContextoMutavel { EmpresaId = outra.Id, UsuarioId = outra.Dono.Id, Papel = "dono" };
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == outra.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LimiteConexoes, (short)5));
        var servicoB = new ServicoConexoes(
            db, new ClienteWhatsAppFalso(), ctxB, TimeProvider.System, new ClienteCloudApiFalso(), amb.Cifra);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servicoB.CriarAsync(Oficial("1090000000777"), default));

        Assert.True(erro.Conflito);
        Assert.DoesNotContain(amb.Cenario.Empresa.Nome, erro.Message);
    }

    [Theory]
    [InlineData(null, "2090000000001", Token, AppSecret)]
    [InlineData("10900/00001", "2090000000001", Token, AppSecret)]
    [InlineData("1090000000001", "2090000000001", "", AppSecret)]
    [InlineData("1090000000001", "2090000000001", Token, "  ")]
    public async Task CAMPO_FALTANDO_OU_TORTO_E_RECUSADO_ANTES_DA_META(
        string? pnid, string waba, string token, string segredo)
    {
        var (db, tx, amb) = await PrepararAsync("campos");
        using var _ = db; using var __ = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.CriarAsync(
            new NovaConexao("Oficial", "cloud_api", pnid, waba, token, segredo), default));

        Assert.Empty(amb.Meta.TokensUsados);
    }

    /// <summary>A conexao oficial nao tem QR, pareamento nem "desconectar": o numero e conectado
    /// na conta da Meta.</summary>
    [Fact]
    public async Task A_CONEXAO_OFICIAL_NAO_USA_QR()
    {
        var (db, tx, amb) = await PrepararAsync("sem-qr");
        using var _ = db; using var __ = tx;
        var id = await amb.Servico.CriarAsync(Oficial(), default);

        Assert.True((await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Servico.ConectarAsync(id, default))).Conflito);
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.ParearAsync(id, "84988887777", default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => amb.Servico.DesconectarAsync(id, default));
    }

    /// <summary>Vazio mantem o guardado; o token novo e conferido na Meta ANTES de substituir — um
    /// token colado errado nao derruba a conexao que funcionava.</summary>
    [Fact]
    public async Task TROCAR_O_TOKEN_CONFERE_ANTES_E_VAZIO_MANTEM()
    {
        var (db, tx, amb) = await PrepararAsync("credenciais");
        using var _ = db; using var __ = tx;
        var id = await amb.Servico.CriarAsync(Oficial(), default);

        async Task<string> TokenGuardadoAsync()
        {
            db.ChangeTracker.Clear();
            var c = await db.Conexoes.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
            return amb.Cifra.Decifrar(c.AccessTokenCifrado!, FinalidadeSegredo.AccessToken);
        }

        await amb.Servico.AtualizarCredenciaisAsync(id, new CredenciaisCloud(null, null), default);
        Assert.Equal(Token, await TokenGuardadoAsync());

        amb.Meta.Recusa = "A Meta recusou o token.";
        await Assert.ThrowsAsync<IntegracaoWhatsAppException>(
            () => amb.Servico.AtualizarCredenciaisAsync(id, new CredenciaisCloud("EAAG-errado", null), default));
        Assert.Equal(Token, await TokenGuardadoAsync());

        amb.Meta.Recusa = null;
        await amb.Servico.AtualizarCredenciaisAsync(id, new CredenciaisCloud("EAAG-novo", null), default);
        Assert.Equal("EAAG-novo", await TokenGuardadoAsync());
    }

    /// <summary>"Testar conexao" diz o que falta, em portugues. Sem o webhook confirmado pela Meta,
    /// as mensagens recebidas nao chegam — e o teste tem de dizer isso antes do cliente descobrir.</summary>
    [Fact]
    public async Task O_TESTE_DIZ_O_QUE_FALTA()
    {
        var (db, tx, amb) = await PrepararAsync("teste");
        using var _ = db; using var __ = tx;
        var id = await amb.Servico.CriarAsync(Oficial(), default);

        var antes = await amb.Servico.TestarAsync(id, default);
        Assert.False(antes.Ok);
        Assert.False(antes.WebhookVerificado);
        Assert.Contains(antes.Problemas, p => p.Contains("webhook"));
        Assert.Equal("5584912345678", antes.Numero);

        await db.Conexoes.IgnoreQueryFilters().Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.WebhookVerificadoEm, DateTime.UtcNow));
        db.ChangeTracker.Clear();

        var depois = await amb.Servico.TestarAsync(id, default);
        Assert.True(depois.Ok);
        Assert.Empty(depois.Problemas);
    }
}
