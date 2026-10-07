using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== A CONFERÊNCIA PERIÓDICA DOS NÚMEROS =====================
///
/// Todos os números de todas as empresas ativas, conferidos na Evolution. Existe porque o banner
/// "WhatsApp fora do ar" e o status da tela liam SÓ o que o webhook `connection.update` gravou — e
/// o aviso perdido deixava um número caído aparecendo como conectado até alguém reparear. Ver
/// `ConferenciaConexao`.
///
/// Roda como JOB, sem tenant no contexto: `IgnoreQueryFilters` e o recorte explícito por empresa
/// ativa, como os motores de follow-up e de NPS.
///
/// ⚠️ UMA CONEXÃO POR VEZ, CADA UMA COM O SEU SAVE. Um número com dado ruim não pode impedir a
/// correção dos outros — o mesmo isolamento por empresa dos motores, um nível abaixo.
/// ===================================================================================</summary>
public class VerificadorConexoes(
    NexoraDbContext db,
    IClienteWhatsApp cliente,
    INotificadorPainel painel,
    TimeProvider relogio,
    ILogger<VerificadorConexoes> log)
{
    /// <summary>Quantas conexões estavam erradas no banco e foram corrigidas.</summary>
    public async Task<int> ExecutarAsync(CancellationToken ct = default)
    {
        var ids = await db.Conexoes.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Empresa.Ativo)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var corrigidas = 0;

        foreach (var id in ids)
        {
            try
            {
                if (await ConferirAsync(id, ct)) corrigidas++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A linha que falhou sai do ChangeTracker: senão o próximo SaveChanges tentaria
                // gravá-la de novo junto com a conexão seguinte.
                db.ChangeTracker.Clear();
                log.LogError(ex, "Conferência da conexão {Id} falhou. As outras seguem.", id);
            }
        }

        if (corrigidas > 0)
            log.LogInformation("Conferência dos números: {N} corrigidas.", corrigidas);

        return corrigidas;
    }

    private async Task<bool> ConferirAsync(long id, CancellationToken ct)
    {
        var conexao = await db.Conexoes.IgnoreQueryFilters().SingleAsync(c => c.Id == id, ct);
        var antes = conexao.Status;

        var (estado, mudou) = await ConferenciaConexao.ConferirAsync(
            conexao, cliente, relogio.GetUtcNow().UtcDateTime, ct);

        if (!mudou) return false;

        await db.SaveChangesAsync(ct);

        // O MESMO EVENTO DO WEBHOOK: quem está com o painel aberto vê o banner acender (ou
        // apagar) na hora, e não só no próximo carregamento.
        await painel.ConexaoMudouAsync(conexao.EmpresaId, new ConexaoPainel(
            conexao.Id, conexao.Status.ParaApi(), conexao.Numero, conexao.NumeroAnterior), ct);

        // WARNING e não INFORMATION: banco e Evolution discordando quer dizer que um aviso de
        // conexão se perdeu. Se isto aparecer com frequência, o webhook tem um problema.
        if (antes != conexao.Status)
            log.LogWarning(
                "Conexão {Id} ({Instancia}) estava {Antes} no banco e a Evolution diz '{Estado}'. Corrigida.",
                conexao.Id, conexao.InstanceName, antes.ParaApi(), estado);

        return true;
    }
}
