using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;
using Nexora.Core.Webhooks;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Webhooks;

/// <summary>Grava o evento na fila de saída. Um INSERT, e nada mais.
///
/// ===================== IgnoreQueryFilters EM TUDO =====================
/// Metade dos caminhos que publicam eventos roda SEM tenant no contexto: o processador do webhook
/// da Evolution e a captação pública. Se a busca do `webhooks_saida` usasse o query filter, ela
/// voltaria vazia nesses caminhos — e o resultado seria "o lead que entra pelo WhatsApp nunca
/// dispara webhook", em silêncio, enquanto o criado à mão na tela dispara.
///
/// É a mesma armadilha do INT-2, e ela custa o mesmo: nada estoura, e o cliente descobre que
/// metade dos leads não chega no ERP dele.
/// ======================================================================</summary>
/// <summary>Publicar NÃO toca na rede: é um INSERT. Quem valida a URL contra SSRF é o motor, na
/// hora da entrega, com DNS fresco — resolver nome no caminho do usuário seria exatamente a
/// lentidão que este componente existe para evitar.</summary>
public class PublicadorEventos(
    NexoraDbContext db,
    TimeProvider relogio,
    ILogger<PublicadorEventos> log) : IPublicadorEventos
{
    public async Task PublicarContatoAsync(
        EventoWebhook evento, Contato contato, long? etapaAnteriorId = null,
        CancellationToken ct = default)
    {
        try
        {
            var webhook = await AssinanteAsync(contato.EmpresaId, evento, ct);
            if (webhook is null) return;

            // O nome da etapa vale o SELECT: sem ele o receptor recebe `etapaId: 7` e precisa de
            // uma segunda chamada só para saber o que aconteceu.
            var etapaNome = webhook.SomenteIds
                ? null
                : await db.EtapasFunil.IgnoreQueryFilters().AsNoTracking()
                    .Where(x => x.Id == contato.EtapaId).Select(x => x.Nome).FirstOrDefaultAsync(ct);

            await EnfileirarAsync(
                webhook, evento,
                PayloadWebhook.Lead(contato, etapaNome, webhook.SomenteIds, etapaAnteriorId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // O lead continua criado e a venda continua fechada. Um webhook que derruba a operação
            // do cliente é pior que um webhook que não sai.
            log.LogError(ex, "Falha ao publicar {Evento} do contato {Id}.", evento, contato.Id);
        }
    }

    public async Task PublicarMensagemAsync(
        long empresaId, long mensagemId, long contatoId, long conversaId,
        string? texto, string contatoNome, string contatoTelefone, DateTime recebidaEm,
        CancellationToken ct = default)
    {
        try
        {
            var webhook = await AssinanteAsync(empresaId, EventoWebhook.MensagemRecebida, ct);
            if (webhook is null) return;

            await EnfileirarAsync(
                webhook, EventoWebhook.MensagemRecebida,
                PayloadWebhook.Mensagem(
                    mensagemId, contatoId, conversaId, texto,
                    contatoNome, contatoTelefone, recebidaEm, webhook.SomenteIds), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falha ao publicar mensagem.recebida da mensagem {Id}.", mensagemId);
        }
    }

    /// <summary>O webhook da empresa, se existir, estiver ATIVO e assinar este evento.
    ///
    /// As três checagens antes de qualquer trabalho: montar payload e gravar linha para um evento
    /// que ninguém assinou seria escrever na tabela de maior volume do sistema à toa.</summary>
    private async Task<WebhookSaida?> AssinanteAsync(
        long empresaId, EventoWebhook evento, CancellationToken ct)
    {
        var webhook = await db.WebhooksSaida.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.EmpresaId == empresaId && w.Ativo, ct);

        return webhook is not null && webhook.Assina(evento) ? webhook : null;
    }

    private async Task EnfileirarAsync(
        WebhookSaida webhook, EventoWebhook evento, object dados, CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;
        var eventoId = Guid.NewGuid();

        db.EntregasWebhook.Add(new EntregaWebhook
        {
            EmpresaId = webhook.EmpresaId,
            EventoId = eventoId,
            Evento = evento,
            Payload = PayloadWebhook.Montar(eventoId, evento, webhook.EmpresaId, agora, dados),
            Url = webhook.Url,
            Status = StatusEntregaWebhook.Pendente,
            // Vence AGORA: a primeira tentativa sai na próxima passada da rodada, não daqui a um
            // minuto. O espaçamento é só entre RETENTATIVAS.
            ProximaTentativaEm = agora
        });

        await db.SaveChangesAsync(ct);
    }
}
