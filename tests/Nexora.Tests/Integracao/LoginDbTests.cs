using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Api;
using Nexora.Api.Seguranca;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

[Collection("banco")]
public class LoginDbTests(BancoTeste banco)
{
    private const string Email = "ana@empresa-a.com";
    private const string Senha = "senha-forte-da-ana";

    [Fact]
    public async Task Credencial_correta_devolve_JWT_com_o_claim_empresa_id()
    {
        var (db, tx, ctx, relogio) = await PrepararAsync();
        using var _ = db; using var __ = tx;

        var empresaId = await SemearAsync(db);
        var servico = new ServicoAutenticacao(db, relogio, NullLogger<ServicoAutenticacao>.Instance);

        var autenticado = await servico.AutenticarAsync(Email, Senha, default);
        Assert.NotNull(autenticado);
        Assert.Equal(empresaId, autenticado!.EmpresaId);
        Assert.Equal("dono", autenticado.Papel);

        var gerador = new GeradorToken(new OpcoesJwt
        {
            Chave = "chave-de-teste-com-pelo-menos-32-caracteres",
            Emissor = "nexora",
            Audiencia = "nexora-painel"
        });
        var (token, expira) = gerador.Gerar(autenticado);

        var lido = new JwtSecurityTokenHandler().ReadJwtToken(token);

        // O claim que sustenta TODO o isolamento multi-tenant.
        var claimEmpresa = lido.Claims.FirstOrDefault(c => c.Type == ContextoEmpresaHttp.ClaimEmpresa);
        Assert.NotNull(claimEmpresa);
        Assert.Equal(empresaId.ToString(), claimEmpresa!.Value);

        Assert.Equal(autenticado.Id.ToString(), lido.Claims.First(c => c.Type == "sub").Value);
        Assert.Equal("nexora", lido.Issuer);
        Assert.True(expira > DateTime.UtcNow);
    }

    [Fact]
    public async Task Senha_errada_nao_autentica_e_nao_revela_nada()
    {
        var (db, tx, ctx, relogio) = await PrepararAsync();
        using var _ = db; using var __ = tx;
        await SemearAsync(db);

        var servico = new ServicoAutenticacao(db, relogio, NullLogger<ServicoAutenticacao>.Instance);

        Assert.Null(await servico.AutenticarAsync(Email, "senha-errada", default));
        Assert.Null(await servico.AutenticarAsync("nao-existe@lugar-nenhum.com", Senha, default));
    }

    [Fact]
    public async Task Bloqueia_na_decima_falha_e_libera_depois_da_janela()
    {
        var (db, tx, ctx, relogio) = await PrepararAsync();
        using var _ = db; using var __ = tx;
        await SemearAsync(db);

        var servico = new ServicoAutenticacao(db, relogio, NullLogger<ServicoAutenticacao>.Instance);

        // Nove falhas: conta ainda livre.
        for (var i = 1; i < PoliticaLogin.MaxFalhasConsecutivas; i++)
            Assert.Null(await servico.AutenticarAsync(Email, "errada", default));

        var antesDoBloqueio = await db.Usuarios.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync(u => u.Email == Email);
        Assert.Null(antesDoBloqueio.BloqueadoAte);
        Assert.Equal(PoliticaLogin.MaxFalhasConsecutivas - 1, antesDoBloqueio.FalhasLogin);

        // A decima tranca a conta.
        Assert.Null(await servico.AutenticarAsync(Email, "errada", default));

        db.ChangeTracker.Clear();
        var bloqueado = await db.Usuarios.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync(u => u.Email == Email);
        Assert.NotNull(bloqueado.BloqueadoAte);
        // Contador zerado para dar novas tentativas depois do bloqueio.
        Assert.Equal(0, bloqueado.FalhasLogin);

        // Trancada: nem a senha CERTA passa.
        Assert.Null(await servico.AutenticarAsync(Email, Senha, default));

        // Passada a janela, a senha certa volta a valer.
        relogio.Avancar(PoliticaLogin.Bloqueio + TimeSpan.FromSeconds(1));
        db.ChangeTracker.Clear();
        Assert.NotNull(await servico.AutenticarAsync(Email, Senha, default));

        db.ChangeTracker.Clear();
        var liberado = await db.Usuarios.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync(u => u.Email == Email);
        Assert.Null(liberado.BloqueadoAte);
        Assert.NotNull(liberado.UltimoAcessoEm);
    }

    [Fact]
    public async Task E_mail_inexistente_e_senha_errada_gastam_tempo_comparavel()
    {
        // O HashDummy existe para isso: sem ele, o caminho "e-mail nao existe" sai sem rodar
        // PBKDF2 e responde ordens de grandeza mais rapido — um timing oracle que enumera
        // quais e-mails estao cadastrados. O teste falha ruidosamente se alguem remove o dummy.
        var (db, tx, ctx, relogio) = await PrepararAsync();
        using var _ = db; using var __ = tx;
        await SemearAsync(db);

        var servico = new ServicoAutenticacao(db, relogio, NullLogger<ServicoAutenticacao>.Instance);

        // Aquece (JIT do PBKDF2 e cache do plano da consulta).
        await servico.AutenticarAsync(Email, "errada", default);
        await servico.AutenticarAsync("aquecimento@x.com", "errada", default);

        var senhaErrada = await MedianaAsync(() => servico.AutenticarAsync(Email, "errada", default));
        var inexistente = await MedianaAsync(() => servico.AutenticarAsync("fantasma@x.com", "errada", default));

        var razao = inexistente / senhaErrada;

        // Faixa larga de proposito: o objetivo nao e cravar constant-time, e detectar a
        // AUSENCIA do dummy — sem ele a razao cai para ~0,01. O caminho da senha errada ainda
        // grava a falha no banco, entao um pouco mais lento e esperado.
        Assert.True(razao > 0.4,
            $"E-mail inexistente respondeu rapido demais (razao {razao:F2}): o HashDummy sumiu? " +
            $"senha errada={senhaErrada:F1}ms, inexistente={inexistente:F1}ms");
    }

    [Fact]
    public async Task Usuario_inativo_e_empresa_inativa_barram_o_login()
    {
        var (db, tx, ctx, relogio) = await PrepararAsync();
        using var _ = db; using var __ = tx;
        var empresaId = await SemearAsync(db);
        var servico = new ServicoAutenticacao(db, relogio, NullLogger<ServicoAutenticacao>.Instance);

        var usuario = await db.Usuarios.IgnoreQueryFilters().FirstAsync(u => u.Email == Email);
        usuario.Status = StatusUsuario.Inativo;
        await db.SaveChangesAsync();

        var eInativo = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.AutenticarAsync(Email, Senha, default));
        Assert.Contains("desativado", eInativo.Message, StringComparison.OrdinalIgnoreCase);

        usuario.Status = StatusUsuario.Ativo;
        var empresa = await db.Empresas.IgnoreQueryFilters().FirstAsync(e => e.Id == empresaId);
        empresa.Ativo = false;
        await db.SaveChangesAsync();

        var eEmpresa = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.AutenticarAsync(Email, Senha, default));
        Assert.Contains("empresa inativa", eEmpresa.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Convidado_sem_senha_nao_autentica_mas_pode_existir_no_banco()
    {
        // Prova as duas metades da decisao de modelagem: senha_hash anulavel (o convidado so
        // define no aceite) + o check ck_usuarios_senha, que so libera NULL para 'convidado'.
        var (db, tx, ctx, relogio) = await PrepararAsync();
        using var _ = db; using var __ = tx;
        var empresaId = await SemearAsync(db);

        db.Usuarios.Add(new Usuario
        {
            EmpresaId = empresaId, Nome = "Convidado", Email = "convidado@empresa-a.com",
            SenhaHash = null, Papel = PapelUsuario.Vendedor, Status = StatusUsuario.Convidado,
            TokenConvite = "tok-" + Guid.NewGuid().ToString("N"),
            ConviteExpira = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync();

        var servico = new ServicoAutenticacao(db, relogio, NullLogger<ServicoAutenticacao>.Instance);
        Assert.Null(await servico.AutenticarAsync("convidado@empresa-a.com", "qualquer", default));

        // E o banco recusa um usuario ATIVO sem senha.
        db.Usuarios.Add(new Usuario
        {
            EmpresaId = empresaId, Nome = "Invalido", Email = "invalido@empresa-a.com",
            SenhaHash = null, Papel = PapelUsuario.Vendedor, Status = StatusUsuario.Ativo
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---------------------------------------------------------------- apoio
    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, ContextoMutavel Ctx, RelogioFalso Relogio)>
        PrepararAsync()
    {
        var relogio = new RelogioFalso(new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero));
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();   // nunca commitada
        return (db, tx, ctx, relogio);
    }

    private static async Task<long> SemearAsync(NexoraDbContext db)
    {
        var empresa = new Empresa { Nome = "Empresa A" };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        db.Usuarios.Add(new Usuario
        {
            EmpresaId = empresa.Id, Nome = "Ana", Email = Email,
            SenhaHash = HashSenha.Gerar(Senha),
            Papel = PapelUsuario.Dono, Status = StatusUsuario.Ativo
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return empresa.Id;
    }

    /// <summary>Mediana de 5 execucoes, em milissegundos. Mediana e nao media porque uma pausa
    /// de GC no meio da amostra distorce a media e nao a mediana.</summary>
    private static async Task<double> MedianaAsync(Func<Task> acao)
    {
        var amostras = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var relogio = Stopwatch.StartNew();
            await acao();
            relogio.Stop();
            amostras.Add(relogio.Elapsed.TotalMilliseconds);
        }
        amostras.Sort();
        return amostras[amostras.Count / 2];
    }
}
