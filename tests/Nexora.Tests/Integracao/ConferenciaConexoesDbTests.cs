using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>A CONFERÊNCIA DOS NÚMEROS NA EVOLUTION.
///
/// O defeito: o status gravado só mudava pelo webhook `connection.update`. Em desenvolvimento, o
/// número caiu em 01/10 com a API desligada, o aviso se perdeu, e a tela e o banner disseram
/// "Conectado" por seis dias. Quase todo teste aqui começa do mesmo jeito — o banco dizendo
/// conectado e a Evolution dizendo outra coisa — e prova um dos dois caminhos que agora
/// conferem: o verificador periódico e a abertura da tela.</summary>
[Collection("banco")]
public class ConferenciaConexoesDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 7, 21, 0, 0, TimeSpan.Zero);

    // ============================================================ o verificador
    [Fact]
    public async Task A_QUEDA_QUE_O_WEBHOOK_PERDEU_E_CORRIGIDA_PELO_VERIFICADOR()
    {
        var (db, tx, amb) = await PrepararAsync("conf-caiu");
        using var _ = db; using var __ = tx;

        amb.Cliente.EstadoPorInstancia[amb.Cenario.Conexao.InstanceName] = "close";

        Assert.Equal(1, await amb.Verificador.ExecutarAsync());

        db.ChangeTracker.Clear();
        var conexao = await LerAsync(db, amb.Cenario.Conexao.Id);
        Assert.Equal(StatusConexao.Desconectado, conexao.Status);
        Assert.Equal(Agora.UtcDateTime, conexao.DesconectadoEm);

        // O banner de quem está com o painel aberto acende na hora, pelo mesmo evento do webhook.
        var aviso = Assert.Single(amb.Painel.Conexoes);
        Assert.Equal("desconectado", aviso.Status);
    }

    /// <summary>De cinco em cinco minutos, para todo número de toda empresa: gravar sem mudança
    /// seria uma escrita por número por rodada, e avisar o painel sem mudança faria o banner piscar
    /// à toa.</summary>
    [Fact]
    public async Task NUMERO_CERTO_NAO_E_REGRAVADO_NEM_AVISADO()
    {
        var (db, tx, amb) = await PrepararAsync("conf-igual");
        using var _ = db; using var __ = tx;

        var antes = (await LerAsync(db, amb.Cenario.Conexao.Id)).StatusEm;

        Assert.Equal(0, await amb.Verificador.ExecutarAsync());

        db.ChangeTracker.Clear();
        Assert.Equal(antes, (await LerAsync(db, amb.Cenario.Conexao.Id)).StatusEm);
        Assert.Empty(amb.Painel.Conexoes);
    }

    [Fact]
    public async Task UM_NUMERO_QUEBRADO_NAO_SEGURA_A_CORRECAO_DOS_OUTROS()
    {
        var (db, tx, amb) = await PrepararAsync("conf-quebra-a");
        using var _ = db; using var __ = tx;

        // A conexão desta empresa vem primeiro na ordem, e é ela que explode.
        var outra = await Semeador.TenantAsync(db, "conf-quebra-b");
        amb.Cliente.InstanciasQueQuebram.Add(amb.Cenario.Conexao.InstanceName);
        amb.Cliente.EstadoPorInstancia[outra.Conexao.InstanceName] = "close";

        Assert.Equal(1, await amb.Verificador.ExecutarAsync());

        db.ChangeTracker.Clear();
        Assert.Equal(StatusConexao.Desconectado, (await LerAsync(db, outra.Conexao.Id)).Status);
    }

    /// <summary>Mesmo recorte dos motores de follow-up e de NPS: empresa desativada não tem quem
    /// olhe o banner, e cada número dela seria um GET na Evolution a cada cinco minutos.</summary>
    [Fact]
    public async Task EMPRESA_INATIVA_NAO_E_CONFERIDA()
    {
        var (db, tx, amb) = await PrepararAsync("conf-inativa");
        using var _ = db; using var __ = tx;

        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Ativo, false));
        amb.Cliente.EstadoPorInstancia[amb.Cenario.Conexao.InstanceName] = "close";

        Assert.Equal(0, await amb.Verificador.ExecutarAsync());

        db.ChangeTracker.Clear();
        Assert.Equal(StatusConexao.Conectado, (await LerAsync(db, amb.Cenario.Conexao.Id)).Status);
    }

    // ============================================================ a tela
    [Fact]
    public async Task ABRIR_A_TELA_CONFERE_E_DEVOLVE_A_LISTA_CORRIGIDA()
    {
        var (db, tx, amb) = await PrepararAsync("conf-tela");
        using var _ = db; using var __ = tx;

        amb.Cliente.EstadoPorInstancia[amb.Cenario.Conexao.InstanceName] = "close";

        var lista = await TelaDe(db, amb).ConferirAsync(default);

        Assert.Equal("desconectado", Assert.Single(lista.Itens).Status);

        db.ChangeTracker.Clear();
        Assert.Equal(StatusConexao.Desconectado, (await LerAsync(db, amb.Cenario.Conexao.Id)).Status);
    }

    /// <summary>A tela roda com o tenant do login: o filtro global é o que a impede de conferir — e
    /// de gravar — o número de outra empresa.</summary>
    [Fact]
    public async Task A_TELA_SO_CONFERE_OS_NUMEROS_DA_PROPRIA_EMPRESA()
    {
        var (db, tx, amb) = await PrepararAsync("conf-tela-a");
        using var _ = db; using var __ = tx;

        var outra = await Semeador.TenantAsync(db, "conf-tela-b");
        amb.Cliente.EstadoPorInstancia[amb.Cenario.Conexao.InstanceName] = "close";
        amb.Cliente.EstadoPorInstancia[outra.Conexao.InstanceName] = "close";

        await TelaDe(db, amb).ConferirAsync(default);

        db.ChangeTracker.Clear();
        Assert.Equal(StatusConexao.Conectado, (await LerAsync(db, outra.Conexao.Id)).Status);
    }

    // ============================================================ apoio
    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto, ClienteWhatsAppFalso Cliente,
        NotificadorFalso Painel, VerificadorConexoes Verificador, RelogioFalso Relogio);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        // O verificador roda SEM tenant no contexto — é job, e é isso que prova que o
        // `IgnoreQueryFilters` + o recorte explícito estão no lugar.
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);

        // A transação isola as linhas dos outros testes, mas o verificador varre `conexoes`
        // inteira: sem isto, um número de dado de desenvolvimento entraria na conta.
        await db.Empresas.IgnoreQueryFilters()
            .Where(e => e.Id != cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Ativo, false));
        db.ChangeTracker.Clear();

        var relogio = new RelogioFalso(Agora);
        var cliente = new ClienteWhatsAppFalso();
        var painel = new NotificadorFalso();

        return (db, tx, new Ambiente(cenario, ctx, cliente, painel,
            new VerificadorConexoes(db, cliente, painel, relogio, NullLogger<VerificadorConexoes>.Instance),
            relogio));
    }

    private static ServicoConexoes TelaDe(NexoraDbContext db, Ambiente amb)
    {
        amb.Contexto.EmpresaId = amb.Cenario.Id;
        amb.Contexto.UsuarioId = amb.Cenario.Dono.Id;
        amb.Contexto.Papel = "dono";
        return new ServicoConexoes(db, amb.Cliente, amb.Contexto, amb.Relogio);
    }

    private static Task<Conexao> LerAsync(NexoraDbContext db, long id) =>
        db.Conexoes.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == id);
}
