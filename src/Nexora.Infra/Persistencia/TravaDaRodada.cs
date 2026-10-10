using Microsoft.EntityFrameworkCore;

namespace Nexora.Infra.Persistencia;

/// <summary>===================== UMA RODADA DE AUTOMACOES POR VEZ (BUG-XX) =====================
///
/// A rodada de follow-up e pesquisa passa de hora em hora. Duas ao mesmo tempo — uma que demorou
/// mais de uma hora, ou duas instancias da API — fariam o trabalho em dobro, e o espacamento entre
/// envios deixaria de valer entre elas. As invariantes do banco ja barram a mensagem duplicada; a
/// trava tira a corrida do caminho.
///
/// Advisory lock DE SESSAO do Postgres: fica preso a conexao aberta aqui, e cai sozinho se o
/// processo morrer (a conexao fecha). Quem nao pega, pula a rodada — a proxima hora tenta de novo.
/// ==========================================================================================</summary>
public sealed class TravaDaRodada : IAsyncDisposable
{
    /// <summary>Chave fixa da trava ("NXFU"). Outra trava do sistema usaria outra chave.</summary>
    public const long Chave = 0x4E58_4655;

    private readonly NexoraDbContext db;

    private TravaDaRodada(NexoraDbContext db) => this.db = db;

    /// <summary>Nulo = outra rodada esta com a trava.</summary>
    public static async Task<TravaDaRodada?> TentarAsync(NexoraDbContext db, CancellationToken ct)
    {
        // A conexao ABERTA A MAO e o que segura a trava: sem isso o EF fecharia a conexao ao fim da
        // consulta, e a trava iria junto para o pool.
        await db.Database.OpenConnectionAsync(ct);

        var pegou = await db.Database
            .SqlQueryRaw<bool>("SELECT pg_try_advisory_lock({0}) AS \"Value\"", Chave)
            .ToListAsync(ct);

        if (pegou.Count > 0 && pegou[0]) return new TravaDaRodada(db);

        await db.Database.CloseConnectionAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", Chave);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
