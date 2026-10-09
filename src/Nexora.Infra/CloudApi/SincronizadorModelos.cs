using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.CloudApi;

/// <summary>===================== A REVISAO QUE O WEBHOOK NAO TROUXE (INT-XX) =====================
///
/// A decisao da Meta sobre um template chega pelo webhook (`message_template_status_update`). Mas o
/// webhook so chega se o app do cliente estiver inscrito nesse campo — e um template que fica
/// "em revisao" para sempre na tela, ja aprovado na Meta, e o tipo de defeito que ninguem reporta:
/// so desiste de usar.
///
/// Esta rodada pergunta a Meta por todo template ainda EM REVISAO, junto com a conferencia dos
/// numeros (a cada 5 minutos). Sem tenant no contexto, como o verificador.
///
/// ⚠️ UM TEMPLATE POR VEZ, CADA UM COM O SEU SAVE: token vencido de uma empresa nao impede as outras.
/// ==========================================================================================</summary>
public class SincronizadorModelos(
    NexoraDbContext db,
    IClienteCloudApi cloud,
    CifraSegredos cifra,
    ILogger<SincronizadorModelos> log)
{
    /// <summary>Quantos templates mudaram de status.</summary>
    public async Task<int> ExecutarAsync(CancellationToken ct)
    {
        var ids = await db.ModelosMensagem.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Status == StatusModelo.Enviado && m.IdMeta != null && m.Empresa.Ativo)
            .OrderBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync(ct);

        var mudaram = 0;

        foreach (var id in ids)
        {
            try
            {
                var modelo = await db.ModelosMensagem.IgnoreQueryFilters()
                    .Include(m => m.Conexao)
                    .SingleAsync(m => m.Id == id, ct);

                var token = cifra.Decifrar(modelo.Conexao.AccessTokenCifrado!, FinalidadeSegredo.AccessToken);
                var lido = await cloud.LerModeloAsync(modelo.IdMeta!, token, ct);

                if (RevisaoModelo.Aplicar(modelo, lido.Status, lido.MotivoRejeicao))
                {
                    await db.SaveChangesAsync(ct);
                    mudaram++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                log.LogWarning(ex, "A revisao do template {Id} nao foi consultada na Meta. Os outros seguem.", id);
            }
        }

        return mudaram;
    }
}
