using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.CloudApi;

/// <summary>Acorda o processamento da fila assim que uma entrega e gravada — sem isso a mensagem
/// esperaria a proxima volta do relogio. So um aviso pendente por vez: dez entregas seguidas
/// acordam UMA rodada, que leva todas.</summary>
public sealed class SinalWebhooksMeta
{
    private readonly SemaphoreSlim sinal = new(0, 1);

    public void Avisar()
    {
        try
        {
            sinal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Ja havia aviso pendente: a rodada que ele acorda leva esta entrega junto.
        }
    }

    /// <summary>Espera o aviso ou o teto, o que vier primeiro.</summary>
    public Task<bool> EsperarAsync(TimeSpan teto, CancellationToken ct) => sinal.WaitAsync(teto, ct);
}

/// <summary>===================== A PORTA DO WEBHOOK DA META (INT-XX) =====================
///
/// RODA SEM TENANT: quem chama e a Meta. A conexao — e com ela a empresa — sai do
/// `phone_number_id` de cada mudanca (ou da WABA, nos eventos de template), por consulta com
/// IgnoreQueryFilters, como o webhook da Evolution faz com o `instance_name`.
///
/// ⚠️ A ASSINATURA E CONFERIDA POR CONEXAO. Cada conexao tem o app secret dela; uma mudanca so
/// entra se a assinatura da entrega bate com o segredo do numero que ela cita. O numero de B
/// assinado com o segredo de A nao entra — nem se vier junto com uma mudanca legitima de A.
///
/// Aceitar e so gravar na fila: o processamento e em segundo plano, porque a Meta reenvia o que
/// demora a responder.
/// ====================================================================================</summary>
public class RecepcaoWebhookMeta(
    NexoraDbContext db,
    CifraSegredos cifra,
    SinalWebhooksMeta sinal,
    TimeProvider relogio,
    ILogger<RecepcaoWebhookMeta> log) : IRecepcaoWebhookMeta
{
    public async Task<ResultadoWebhookMeta> AceitarAsync(byte[] corpo, string? assinatura, CancellationToken ct)
    {
        var mudancas = LeitorEventoCloudApi.Mudancas(corpo);
        if (mudancas.Count == 0) return ResultadoWebhookMeta.NadaReconhecido;

        var numeros = mudancas.Where(m => m.PhoneNumberId != null).Select(m => m.PhoneNumberId!).Distinct().ToList();
        var contas = mudancas.Where(m => m.PhoneNumberId == null && m.WabaId != null).Select(m => m.WabaId!).Distinct().ToList();

        var conexoes = await db.Conexoes.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Canal == CanalWhatsapp.CloudApi
                     && ((c.PhoneNumberId != null && numeros.Contains(c.PhoneNumberId))
                      || (c.WabaId != null && contas.Contains(c.WabaId))))
            .Select(c => new { c.Id, c.EmpresaId, c.PhoneNumberId, c.WabaId, c.AppSecretCifrado })
            .ToListAsync(ct);

        if (conexoes.Count == 0)
        {
            log.LogInformation("Webhook da Meta sem numero conhecido ({Numeros}) — ignorado.", string.Join(", ", numeros));
            return ResultadoWebhookMeta.NadaReconhecido;
        }

        var agora = relogio.GetUtcNow().UtcDateTime;
        var assinaturaDaConexao = new Dictionary<long, bool>();
        var aceitas = 0;

        foreach (var mudanca in mudancas)
        {
            var conexao = mudanca.PhoneNumberId != null
                ? conexoes.FirstOrDefault(c => c.PhoneNumberId == mudanca.PhoneNumberId)
                : conexoes.FirstOrDefault(c => c.WabaId == mudanca.WabaId);
            if (conexao == null) continue;

            if (!assinaturaDaConexao.TryGetValue(conexao.Id, out var valida))
            {
                valida = AssinaturaBate(corpo, assinatura, conexao.AppSecretCifrado);
                assinaturaDaConexao[conexao.Id] = valida;
            }
            if (!valida) continue;

            db.WebhooksMetaRecebidos.Add(new WebhookMetaRecebido
            {
                EmpresaId = conexao.EmpresaId,
                ConexaoId = conexao.Id,
                Campo = mudanca.Campo,
                Payload = mudanca.Json,
                RecebidoEm = agora
            });
            aceitas++;
        }

        if (aceitas == 0)
        {
            log.LogWarning("Webhook da Meta recusado: a assinatura nao bate com o app secret do numero citado.");
            return ResultadoWebhookMeta.AssinaturaInvalida;
        }

        await db.SaveChangesAsync(ct);
        sinal.Avisar();
        return ResultadoWebhookMeta.Aceito;
    }

    public async Task<string?> VerificarAsync(string? modo, string? token, string? desafio, CancellationToken ct)
    {
        if (modo != "subscribe" || string.IsNullOrWhiteSpace(token) || desafio == null) return null;

        var conexao = await db.Conexoes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.VerifyToken == token && c.Canal == CanalWhatsapp.CloudApi, ct);
        if (conexao == null)
        {
            log.LogWarning("Verificacao do webhook da Meta com token desconhecido — recusada.");
            return null;
        }

        // E o que o "Testar conexao" mostra: sem esta data, as mensagens recebidas nao chegam.
        conexao.WebhookVerificadoEm = relogio.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return desafio;
    }

    private bool AssinaturaBate(byte[] corpo, string? assinatura, string? segredoCifrado)
    {
        if (segredoCifrado == null) return false;
        try
        {
            var segredo = cifra.Decifrar(segredoCifrado, FinalidadeSegredo.AppSecret);
            return AssinaturaMeta.Valida(corpo, assinatura, segredo);
        }
        catch (CryptographicException ex)
        {
            log.LogError(ex, "App secret de uma conexao da Cloud API nao abre com a chave atual.");
            return false;
        }
    }
}
