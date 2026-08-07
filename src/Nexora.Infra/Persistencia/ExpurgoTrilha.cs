using Microsoft.EntityFrameworkCore;

namespace Nexora.Infra.Persistencia;

/// <summary>===================== RETENÇÃO DA TRILHA: 12 MESES (AUD-1) =====================
///
/// A trilha cresce rápido — todo clique de edição vira linha — e sem teto ela vira a maior tabela
/// do banco antes de qualquer outra coisa.
///
/// DOZE MESES, e o número vem da pergunta que a trilha responde: "quem mexeu nisso e quando".
/// Ela é feita dias ou semanas depois do fato, no máximo na conferência de fechamento do ano.
/// Um ano cobre isso com folga; guardar mais é pagar armazenamento por uma pergunta que ninguém
/// faz — e, tratando-se de dado que inclui histórico de pessoa, guardar além do necessário é
/// exposição, não zelo.
///
/// SEM TENANT: roda como job, com `IgnoreQueryFilters`. Filtrar por empresa aqui obrigaria a
/// varrer empresa a empresa para chegar ao mesmo conjunto.
///
/// `ix_auditoria_empresa` cobre a varredura por data. `ExecuteDelete` faz um DELETE único no
/// banco — nada é materializado no processo.</summary>
public static class ExpurgoTrilha
{
    public static readonly TimeSpan Retencao = TimeSpan.FromDays(365);

    public static Task<int> ExpurgarAsync(NexoraDbContext db, TimeProvider relogio, CancellationToken ct)
    {
        var limite = relogio.GetUtcNow().UtcDateTime - Retencao;

        return db.Auditoria.IgnoreQueryFilters()
            .Where(a => a.Quando < limite)
            .ExecuteDeleteAsync(ct);
    }
}
