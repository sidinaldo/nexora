using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O resumo da Captação é agregado no banco (AUD-XX): leads de cada caminho, fatias que
/// somam 100, ativos e totais. A empresa vizinha não entra.</summary>
[Collection("banco")]
public class CaptacaoResumoDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset Marco = new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task O_RESUMO_SOMA_NO_BANCO_E_AS_FATIAS_FECHAM_100()
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(Marco);
        using var db = banco.NovoContexto(ctx, relogio);
        using var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, "captacao-resumo");
        var vizinha = await Semeador.TenantAsync(db, "captacao-resumo-vizinha");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        db.CanaisCaptacao.Add(new CanalCaptacao
        {
            EmpresaId = cenario.Id, Nome = "Balcão", Codigo = "aa11", ConexaoId = cenario.Conexao.Id,
            LeadsRecebidos = 1
        });
        db.CanaisCaptacao.Add(new CanalCaptacao
        {
            EmpresaId = cenario.Id, Nome = "Panfleto", Codigo = "bb22", ConexaoId = cenario.Conexao.Id,
            LeadsRecebidos = 1, Ativo = false
        });
        db.FormulariosCaptura.Add(new FormularioCaptura
        {
            EmpresaId = cenario.Id, Nome = "Landing", Chave = new string('c', 48), LeadsRecebidos = 1
        });
        db.CanaisCaptacao.Add(new CanalCaptacao
        {
            EmpresaId = vizinha.Id, Nome = "Da vizinha", Codigo = "zz99", ConexaoId = vizinha.Conexao.Id,
            LeadsRecebidos = 500
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var servico = new ServicoCaptacao(db, new ServicoConversoes(
            db, ctx, new ClienteMetaFalso(), relogio, PublicadorConversoesDeTeste.Novo(db, relogio)));

        var r = await servico.ResumoAsync(default);

        Assert.Equal(3, r.LeadsTotal);
        Assert.Equal(2, r.LeadsCanais);
        Assert.Equal(1, r.LeadsFormularios);
        // 2 de 3 e 1 de 3: 66,67 e 33,33 — e somam 100, sem o 99,99 do arredondamento solto.
        Assert.Equal(66.67m, r.PercentualCanais);
        Assert.Equal(33.33m, r.PercentualFormularios);
        Assert.Equal(1, r.CanaisAtivos);
        Assert.Equal(2, r.TotalCanais);
        Assert.Equal(1, r.FormulariosAtivos);
        Assert.Equal(1, r.TotalFormularios);
    }

    [Fact]
    public async Task SEM_LEAD_NENHUM_AS_FATIAS_SAO_NULAS()
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(Marco);
        using var db = banco.NovoContexto(ctx, relogio);
        using var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, "captacao-resumo-vazio");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var servico = new ServicoCaptacao(db, new ServicoConversoes(
            db, ctx, new ClienteMetaFalso(), relogio, PublicadorConversoesDeTeste.Novo(db, relogio)));

        var r = await servico.ResumoAsync(default);

        Assert.Equal(0, r.LeadsTotal);
        Assert.Null(r.PercentualCanais);
        Assert.Null(r.PercentualFormularios);
    }
}
