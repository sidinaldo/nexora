using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Servicos;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>"Hoje", nos feriados, é o dia da EMPRESA (AUD-XX, B13). Às 22h30 de Brasília de quinta
/// já é sexta em UTC: o feriado de quinta sumia dos próximos, e cadastrá-lo era recusado como
/// "data no passado".</summary>
[Collection("banco")]
public class FeriadosFusoDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset QuintaANoite = new(2026, 8, 6, 22, 30, 0, TimeSpan.FromHours(-3));

    [Fact]
    public async Task O_FERIADO_DE_HOJE_A_NOITE_AINDA_E_DE_HOJE()
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaANoite);
        using var db = banco.NovoContexto(ctx, relogio);
        using var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, "feriados-fuso");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var servico = new ServicoFeriados(db, ctx, relogio, NullLogger<ServicoFeriados>.Instance);
        var quinta = new DateOnly(2026, 8, 6);

        // Cadastrar para hoje é aceito: o dia da empresa ainda é quinta.
        await servico.CriarManualAsync(new NovoFeriado(quinta, "Aniversário da loja"), default);
        db.ChangeTracker.Clear();

        var proximos = await servico.ProximosAsync(default);
        Assert.Contains(proximos, f => f.Data == quinta && f.Nome == "Aniversário da loja");
    }
}
