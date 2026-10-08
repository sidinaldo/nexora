using Microsoft.EntityFrameworkCore;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Os tetos e o uso, contados no banco (AUD-XX). Ver `TetosDaEmpresa`.</summary>
public class ServicoLimites(NexoraDbContext db) : IServicoLimites
{
    public async Task<TetosDaEmpresa> ObterAsync(long? pipelineId, CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking().CountAsync(ct);
        var etiquetas = await db.Etiquetas.AsNoTracking().CountAsync(ct);

        UsoDoLimite? etapasDoFunil = null;
        if (pipelineId != null)
        {
            var etapas = await db.EtapasFunil.AsNoTracking()
                .CountAsync(e => e.PipelineId == pipelineId.Value, ct);
            etapasDoFunil = Uso(etapas, ServicoEtapas.MaximoEtapas);
        }

        return new TetosDaEmpresa(
            Uso(pipelines, ServicoPipelines.MaximoPipelines),
            Uso(etiquetas, ServicoEtiquetas.MaximoEtiquetas),
            etapasDoFunil,
            ServicoEtiquetas.MaximoPorNegociacao);
    }

    private static UsoDoLimite Uso(int emUso, int limite) => new(emUso, limite, emUso >= limite);
}
