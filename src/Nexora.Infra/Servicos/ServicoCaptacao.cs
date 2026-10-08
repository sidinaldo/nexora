using Microsoft.EntityFrameworkCore;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O resumo da tela de Captação, agregado no banco (AUD-XX). Ver `ResumoCaptacao`.</summary>
public class ServicoCaptacao(NexoraDbContext db, IServicoConversoes conversoes) : IServicoCaptacao
{
    public async Task<ResumoCaptacao> ResumoAsync(CancellationToken ct)
    {
        var canais = db.CanaisCaptacao.AsNoTracking();
        var totalCanais = await canais.CountAsync(ct);
        var canaisAtivos = await canais.CountAsync(c => c.Ativo, ct);
        var leadsCanais = await canais.SumAsync(c => c.LeadsRecebidos, ct);

        var formularios = db.FormulariosCaptura.AsNoTracking();
        var totalFormularios = await formularios.CountAsync(ct);
        var formulariosAtivos = await formularios.CountAsync(f => f.Ativo, ct);
        var leadsFormularios = await formularios.SumAsync(f => f.LeadsRecebidos, ct);

        var total = leadsCanais + leadsFormularios;

        // As duas fatias somam 100, pelo maior resto. Sem lead nenhum não há fatia: nulo, e a tela
        // não escreve "0%" de um todo que não existe.
        decimal? percentualCanais = null;
        decimal? percentualFormularios = null;
        if (total > 0)
        {
            var fatias = Percentual.Fatias([leadsCanais, leadsFormularios]);
            percentualCanais = fatias[0];
            percentualFormularios = fatias[1];
        }

        // O aviso só vale para quem NÃO está enviando: a regra é a do resumo de anúncios.
        var anuncios = await conversoes.ResumoAsync(ct);
        var leadsDeAnuncioSemEnvio = 0;
        if (!anuncios.Enviando)
        {
            leadsDeAnuncioSemEnvio = anuncios.LeadsComAnuncio30Dias;
        }

        return new ResumoCaptacao(
            total, leadsCanais, leadsFormularios, percentualCanais, percentualFormularios,
            canaisAtivos, totalCanais, formulariosAtivos, totalFormularios, leadsDeAnuncioSemEnvio);
    }
}
