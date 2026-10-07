using Microsoft.EntityFrameworkCore;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A data de hoje no fuso da empresa logada. Ver `IHojeDaEmpresa`.
///
/// O filtro global de tenant escolhe a empresa: roda dentro de requisição autenticada, e não há
/// `IgnoreQueryFilters` aqui.</summary>
public class HojeDaEmpresa(NexoraDbContext db, TimeProvider relogio) : IHojeDaEmpresa
{
    public async Task<DateOnly> HojeAsync(CancellationToken ct)
    {
        var fusoHorario = await db.Empresas.AsNoTracking()
            .Select(e => e.FusoHorario)
            .FirstOrDefaultAsync(ct);

        var fuso = FusoDeNegocio.Resolver(fusoHorario);

        return DateOnly.FromDateTime(FusoDeNegocio.AgoraNo(relogio, fuso));
    }
}
