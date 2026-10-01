using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Email;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Email;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>OPE-1 — a cota de pessoas por empresa.
///
/// ===================== É A ÚNICA PARTE DO BLOCO QUE MUDA O QUE O CLIENTE SENTE =====================
/// Tudo o mais do OPE-1 é tela de operador e coluna nova. Isto aqui recusa um convite que ontem
/// funcionava, no painel de alguém que paga. Por isso vai sozinho, e por isso tem teste para cada
/// caminho que NÃO deve cobrar.
///
/// As três coisas que falham em silêncio se alguém mexer:
///
///   1. a cota cobrada em UMA porta só. Checar só o convite deixa o teto burlável em três cliques:
///      desativa três, convida três, reativa três;
///   2. o ACEITE passar a falhar. A vaga é reservada no convite exatamente para o aceite nunca
///      poder falhar — alguém com um link válido na mão não pode ser barrado por um número que
///      mudou atrás dele;
///   3. estar acima do limite virar punição. É estado LEGAL: ninguém perde acesso, ninguém é
///      desativado. Só o próximo convite falha.
/// ================================================================================================</summary>
[Collection("banco")]
public class CotaDeUsuariosDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ==================================================================== as duas portas

    [Fact]
    public async Task O_CONVITE_E_BARRADO_QUANDO_AS_VAGAS_ACABAM()
    {
        var (db, tx, amb) = await PrepararAsync("cota-convite");
        using var _ = db; using var __ = tx;

        // O Semeador deixa o dono: 1 vaga de 2 usada.
        await DefinirLimiteAsync(db, amb.EmpresaId, 2);
        await amb.Equipe.ConvidarAsync(new NovoConvite("Segunda", "cota1@x.com", "vendedor"), default);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Equipe.ConvidarAsync(new NovoConvite("Terceira", "cota2@x.com", "vendedor"), default));

        Assert.True(erro.Conflito);   // 409, igual ao limite de conexões
        // ⚠️ A FRASE SOBRE CONVITES PENDENTES É LOAD-BEARING: sem ela, o dono lê "2 usuários", conta
        // 1 pessoa na tela e conclui que o software está quebrado.
        Assert.Contains("convites pendentes", erro.Message);
    }

    [Fact]
    public async Task REATIVAR_ALGUEM_TAMBEM_GASTA_VAGA_E_FECHA_O_DESVIO_DE_TRES_CLIQUES()
    {
        // ===================== O DESVIO QUE ESTE TESTE FECHA =====================
        // Se a cota só fosse cobrada no convite: desativa três, convida três, reativa três. Teto
        // de 2 vira 5 sem nenhum erro, e sem nada na trilha que pareça errado.
        // ========================================================================
        var (db, tx, amb) = await PrepararAsync("cota-reativa");
        using var _ = db; using var __ = tx;

        var inativo = await CriarAsync(db, amb.EmpresaId, "reativavel", StatusUsuario.Inativo);
        await DefinirLimiteAsync(db, amb.EmpresaId, 2);

        // Teto 2: o dono (1) + um convidado (2). O inativo não conta.
        await amb.Equipe.ConvidarAsync(new NovoConvite("Segunda", "reat1@x.com", "vendedor"), default);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Equipe.AtualizarAsync(
                inativo, new EditarUsuario("Reativavel", "vendedor", "ativo"), default));

        Assert.True(erro.Conflito);
    }

    // ==================================================================== o que NÃO cobra

    [Fact]
    public async Task O_ACEITE_DO_CONVITE_NUNCA_FALHA_POR_COTA_NEM_COM_O_LIMITE_BAIXADO_DEPOIS()
    {
        // ===================== POR QUE A VAGA É RESERVADA NO CONVITE =====================
        // Alguém convidado na segunda, com o limite baixado na terça, clica o link na quarta. Se o
        // aceite cobrasse, essa pessoa seria barrada com um link válido na mão e NENHUMA ação
        // possível do lado dela. Reservar no convite é o que garante que isso não aconteça.
        // ================================================================================
        var (db, tx, amb) = await PrepararAsync("cota-aceite");
        using var _ = db; using var __ = tx;

        await DefinirLimiteAsync(db, amb.EmpresaId, 3);
        var token = await amb.Equipe.ConvidarAsync(
            new NovoConvite("Convidada", "aceite@x.com", "vendedor"), default);

        // O operador baixa o teto DEPOIS do convite e ANTES do aceite.
        await DefinirLimiteAsync(db, amb.EmpresaId, 1);

        var usuario = await amb.Equipe.AceitarConviteAsync(token.Token, "senhaforte1", default);

        Assert.NotNull(usuario);
    }

    [Fact]
    public async Task REENVIAR_CONVITE_NAO_COBRA_A_VAGA_DE_NOVO()
    {
        // O convite JÁ ocupa a vaga. Cobrar de novo recusaria reenviar um convite que já está pago.
        var (db, tx, amb) = await PrepararAsync("cota-reenvio");
        using var _ = db; using var __ = tx;

        await DefinirLimiteAsync(db, amb.EmpresaId, 2);
        await amb.Equipe.ConvidarAsync(new NovoConvite("Convidada", "reenvio@x.com", "vendedor"), default);

        var id = await db.Usuarios.Where(u => u.Email == "reenvio@x.com").Select(u => u.Id).FirstAsync();
        db.ChangeTracker.Clear();

        // Teto cheio (dono + convidada = 2 de 2), e mesmo assim o reenvio passa.
        var novo = await amb.Equipe.ReenviarConviteAsync(id, default);
        Assert.False(string.IsNullOrWhiteSpace(novo.Token));
    }

    // ==================================================================== o que conta

    [Fact]
    public async Task INATIVO_NAO_OCUPA_VAGA_E_DESATIVAR_LIBERA()
    {
        // Desativar é a única saída que o desenho oferece — não há delete de usuário. Saída que não
        // libera vaga não é saída.
        var (db, tx, amb) = await PrepararAsync("cota-inativo");
        using var _ = db; using var __ = tx;

        var ocupante = await CriarAsync(db, amb.EmpresaId, "ocupante", StatusUsuario.Ativo);
        await DefinirLimiteAsync(db, amb.EmpresaId, 2);

        // Cheio: dono + ocupante.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Equipe.ConvidarAsync(new NovoConvite("Terceira", "inat1@x.com", "vendedor"), default));

        await amb.Equipe.AtualizarAsync(ocupante, new EditarUsuario("Ocupante", "vendedor", "inativo"), default);

        // Vaga liberada: agora passa.
        var token = await amb.Equipe.ConvidarAsync(
            new NovoConvite("Terceira", "inat1@x.com", "vendedor"), default);
        Assert.False(string.IsNullOrWhiteSpace(token.Token));
    }

    // ==================================================================== acima do limite

    [Fact]
    public async Task ACIMA_DO_LIMITE_NINGUEM_PERDE_ACESSO_SO_O_PROXIMO_CONVITE_FALHA()
    {
        // ===================== O LIMITE É PORTEIRO, NUNCA EXECUTOR =====================
        // O operador pode baixar o teto abaixo do uso — é como se registra um downgrade. Quando
        // isso acontece, ninguém é deslogado e ninguém é desativado. A alternativa seria o software
        // escolher quais 2 de 5 funcionários perdem acesso, e ninguém desenhou essa escolha nem
        // escreveu a mensagem para quem fosse sorteado.
        // ==============================================================================
        var (db, tx, amb) = await PrepararAsync("cota-excedente");
        using var _ = db; using var __ = tx;

        await CriarAsync(db, amb.EmpresaId, "sobra-a", StatusUsuario.Ativo);
        await CriarAsync(db, amb.EmpresaId, "sobra-b", StatusUsuario.Ativo);

        // Downgrade: 3 vagas usadas, teto 1.
        await DefinirLimiteAsync(db, amb.EmpresaId, 1);

        // Ninguém foi desativado.
        var ativos = await db.Usuarios.CountAsync(u => u.Status == StatusUsuario.Ativo);
        Assert.Equal(3, ativos);

        // E só o crescimento é barrado.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Equipe.ConvidarAsync(new NovoConvite("Quarta", "exc@x.com", "vendedor"), default));
        Assert.Contains("um usuário", erro.Message);
    }

    // ==================================================================== auxiliares

    private static async Task DefinirLimiteAsync(NexoraDbContext db, long empresaId, short limite)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == empresaId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LimiteUsuarios, limite));
        db.ChangeTracker.Clear();
    }

    private static async Task<long> CriarAsync(
        NexoraDbContext db, long empresaId, string marca, StatusUsuario status)
    {
        var u = new Usuario
        {
            EmpresaId = empresaId,
            Nome = marca,
            Email = $"{marca}-{empresaId}@teste.local",
            Papel = PapelUsuario.Vendedor,
            Status = status,
            SenhaHash = status == StatusUsuario.Convidado ? null : "pbkdf2$1$x$y"
        };
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return u.Id;
    }

    private sealed record Ambiente(long EmpresaId, ContextoMutavel Contexto, IServicoEquipe Equipe);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var notificador = new NotificadorEmail(
            new RemetenteFalso(), db, new OpcoesEmail { BaseUrlPainel = "http://localhost:4200" },
            relogio, NullLogger<NotificadorEmail>.Instance);

        return (db, tx, new Ambiente(
            cenario.Id, ctx,
            new ServicoEquipe(db, ctx, relogio, notificador, new FilaSegundoPlanoFalsa())));
    }
}
