using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>Os tetos vêm das constantes que os serviços usam para recusar, e o uso é contado no
/// banco, por empresa (AUD-XX).</summary>
[Collection("banco")]
public class LimitesDbTests(BancoTeste banco)
{
    [Fact]
    public async Task OS_TETOS_SAO_OS_DO_SERVICO_E_O_USO_E_DA_EMPRESA()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, "limites");
        var vizinha = await Semeador.TenantAsync(db, "limites-vizinha");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        db.Etiquetas.Add(new Etiqueta { EmpresaId = cenario.Id, Nome = "VIP", Cor = "#C0392B" });
        db.Etiquetas.Add(new Etiqueta { EmpresaId = vizinha.Id, Nome = "Da vizinha", Cor = "#C0392B" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var esperadoPipelines = await db.Pipelines.IgnoreQueryFilters().CountAsync(p => p.EmpresaId == cenario.Id);
        var esperadoEtapas = await db.EtapasFunil.IgnoreQueryFilters()
            .CountAsync(e => e.PipelineId == cenario.Pipeline.Id);

        var l = await new ServicoLimites(db).ObterAsync(cenario.Pipeline.Id, default);

        Assert.Equal(ServicoPipelines.MaximoPipelines, l.LimitePipelines.Limite);
        Assert.Equal(esperadoPipelines, l.LimitePipelines.EmUso);
        Assert.Equal(ServicoEtiquetas.MaximoEtiquetas, l.LimiteEtiquetas.Limite);
        Assert.Equal(1, l.LimiteEtiquetas.EmUso);
        Assert.NotNull(l.LimiteEtapasDoFunil);
        Assert.Equal(ServicoEtapas.MaximoEtapas, l.LimiteEtapasDoFunil.Limite);
        Assert.Equal(esperadoEtapas, l.LimiteEtapasDoFunil.EmUso);
        Assert.Equal(ServicoEtiquetas.MaximoPorNegociacao, l.EtiquetasPorNegocio);

        // Sem funil pedido, a parte do funil não vem.
        Assert.Null((await new ServicoLimites(db).ObterAsync(null, default)).LimiteEtapasDoFunil);
    }

    /// <summary>`Cheio` é a mesma pergunta que o serviço faz antes de recusar: com o teto atingido, a
    /// tela desliga o botão, e a criação de fato é recusada.</summary>
    [Fact]
    public async Task CHEIO_E_A_MESMA_CONTA_DA_RECUSA()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "limites-cheio");
        using var _ = db; using var __ = tx;

        var servicoLimites = new ServicoLimites(db);
        var uso = (await servicoLimites.ObterAsync(null, default)).LimitePipelines;
        var pipelines = new ServicoPipelines(db, amb.Contexto);
        for (var i = uso.EmUso; i < uso.Limite; i++)
        {
            await pipelines.CriarAsync(new Nexora.Core.Servicos.NovaPipeline($"Funil {i}", "#5C8F6E"), default);
        }
        db.ChangeTracker.Clear();

        Assert.True((await servicoLimites.ObterAsync(null, default)).LimitePipelines.Cheio);
        await Assert.ThrowsAsync<Nexora.Core.Servicos.RegraDeNegocioException>(
            () => pipelines.CriarAsync(new Nexora.Core.Servicos.NovaPipeline("Um a mais", "#5C8F6E"), default));
    }
}
