using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;

namespace Nexora.Infra.Servicos;

/// <summary>O número do badge do menu: a soma de `nao_lidas` das conversas ABERTAS da empresa.
///
/// ===================== UMA CÓPIA, TRÊS LUGARES (AUD-XX) =====================
/// O status do painel, o evento de tempo real de mensagem recebida e o "marcar como lida" dão este
/// número. Antes só o status contava; a tela somava +1 a cada mensagem e não descia ao ler, então
/// o badge só voltava a ficar certo no próximo poll, 45 segundos depois.
///
/// Quem chama passa a consulta já recortada pela empresa — com o filtro global, ou com
/// `IgnoreQueryFilters` e `empresa_id` explícito, como o processador de webhook.
/// ============================================================================</summary>
public static class NaoLidasDaEmpresa
{
    public static async Task<int> ContarAsync(IQueryable<Conversa> conversasDaEmpresa, CancellationToken ct)
    {
        var soma = await conversasDaEmpresa
            .Where(c => c.Status == StatusConversa.Aberta)
            .SumAsync(c => (int?)c.NaoLidas, ct);

        if (soma == null)
        {
            return 0;
        }

        return soma.Value;
    }
}
