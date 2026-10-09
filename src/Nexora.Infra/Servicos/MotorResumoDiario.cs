using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core;
using Nexora.Core.Email;
using Nexora.Core.Entidades;
using Nexora.Core.Resumo;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== O RESUMO DE ONTEM, PARA O DONO (RES-XX) =====================
///
/// Na rodada diaria das 8h, uma empresa por vez, cada uma no seu escopo (ver `AgendadorFollowUp`).
///
/// Tres passos, nesta ordem:
///   1. RESERVA o dia — um INSERT unico por (empresa, dia). Um reinicio da API perto das 8h nao
///      manda o mesmo e-mail duas vezes;
///   2. ASSUME a empresa como dono, e monta os numeros com o mesmo codigo das telas;
///   3. ENTREGA a cada dono ativo. O envio nunca lanca: o que deu errado fica em `emails_enviados`.
///
/// Empresa de DEMONSTRACAO nao recebe: os numeros sao de mentira, e o e-mail iria para alguem de
/// verdade.
/// ======================================================================================</summary>
public class MotorResumoDiario(
    NexoraDbContext db,
    ContextoDeFundo fundo,
    IServicoResumoDiario servico,
    INotificadorEmail email,
    TimeProvider relogio,
    ILogger<MotorResumoDiario> log)
{
    /// <summary>As empresas que pediram o resumo. SEM TENANT: o job ainda nao e de ninguem.</summary>
    public async Task<IReadOnlyList<long>> EmpresasAsync(CancellationToken ct) =>
        await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Ativo && e.ResumoDiarioAtivo && !e.Demonstracao)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync(ct);

    /// <summary>Manda o resumo de ontem de UMA empresa. Devolve se mandou.
    ///
    /// ⚠️ UM ESCOPO POR EMPRESA: daqui para baixo o job E aquela empresa, e o escopo seguinte tem de
    /// comecar limpo.</summary>
    public async Task<bool> EnviarAsync(long empresaId, CancellationToken ct)
    {
        var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == empresaId && e.Ativo && e.ResumoDiarioAtivo && !e.Demonstracao)
            .Select(e => new { e.FusoHorario })
            .FirstOrDefaultAsync(ct);
        if (empresa == null) return false;

        var donos = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.EmpresaId == empresaId
                     && u.Papel == PapelUsuario.Dono
                     && u.Status == StatusUsuario.Ativo)
            .OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Nome, u.Email })
            .ToListAsync(ct);
        if (donos.Count == 0)
        {
            log.LogWarning("Empresa {Id} pediu o resumo diario e nao tem dono ativo — pulando.", empresaId);
            return false;
        }

        // "Ontem" no fuso da EMPRESA: a de Manaus fecha o dia uma hora depois da de Sao Paulo.
        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);
        var ontem = DateOnly.FromDateTime(FusoDeNegocio.AgoraNo(relogio, fuso)).AddDays(-1);

        if (!await ReservarAsync(empresaId, ontem, ct)) return false;

        fundo.Assumir(empresaId, donos[0].Id, "dono");
        db.ChangeTracker.Clear();

        var resumo = await servico.MontarAsync(ontem, ct);

        foreach (var dono in donos)
            await email.ResumoDiarioAsync(empresaId, dono.Email, dono.Nome, resumo, ct);

        log.LogInformation("Resumo de {Dia} da empresa {Id} enviado a {N} dono(s).", ontem, empresaId, donos.Count);
        return true;
    }

    /// <summary>⚠️ A RESERVA, NUM COMANDO SO: o banco decide quem ganha, e o perdedor recebe "0
    /// linhas". Metodo proprio para o teste chama-lo duas vezes e ver a segunda perder.</summary>
    public async Task<bool> ReservarAsync(long empresaId, DateOnly dia, CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO resumos_diarios (empresa_id, dia, criado_em)
            VALUES ({0}, {1}, {2})
            ON CONFLICT (empresa_id, dia) DO NOTHING
            """, [empresaId, dia, relogio.GetUtcNow().UtcDateTime], ct) == 1;
}
