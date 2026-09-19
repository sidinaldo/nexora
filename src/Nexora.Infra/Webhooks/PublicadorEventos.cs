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
    public Task PublicarContatoAsync(
        EventoWebhook evento, Contato contato, long? etapaAnteriorId = null,
        CancellationToken ct = default) =>
        PublicarAsync(evento, [contato], etapaAnteriorId, emMassa: false, ct);

    public Task PublicarContatosEmMassaAsync(
        EventoWebhook evento, IReadOnlyCollection<Contato> contatos,
        CancellationToken ct = default) =>
        PublicarAsync(evento, contatos, etapaAnteriorId: null, emMassa: true, ct);

    public async Task<bool> AlguemAssinaAsync(
        long empresaId, EventoWebhook evento, CancellationToken ct = default) =>
        await AssinanteAsync(empresaId, evento, ct) is not null;

    /// <summary>⚠️ UM CAMINHO SÓ, para um contato e para dois mil. O de um contato é o lote de
    /// tamanho um — e é isso que mantém a regra da "negociação vigente" numa cópia só. Um laço de
    /// `PublicarContatoAsync` no import seriam três consultas e um `SaveChanges` POR LINHA: o import
    /// de três segundos virando um de três minutos, pelo aviso.</summary>
    private async Task PublicarAsync(
        EventoWebhook evento, IReadOnlyCollection<Contato> contatos, long? etapaAnteriorId,
        bool emMassa, CancellationToken ct)
    {
        if (contatos.Count == 0) return;

        try
        {
            var agora = relogio.GetUtcNow().UtcDateTime;
            var enfileirou = false;

            foreach (var daEmpresa in contatos.GroupBy(c => c.EmpresaId))
            {
                var webhook = await AssinanteAsync(daEmpresa.Key, evento, ct);
                if (webhook is null) continue;

                var ids = daEmpresa.Select(c => c.Id).ToList();

                // ===================== A POSICAO VEM DO NEGOCIO (E4e) =====================
                // O contato nao tem mais etapa nem valor. O payload continua com o mesmo formato —
                // o receptor do cliente nao muda uma linha —, mas os numeros saem da negociacao
                // VIGENTE: a aberta, ou a mais recente quando nao ha nenhuma aberta.
                //
                // ⚠️ Ordena por STATUS e nao por id, pelo mesmo motivo de todo o resto do bloco: a
                // migracao do elo deixou os ids das ganhas maiores que os das abertas.
                //
                // A escolha e feita AQUI, na memoria, e nao com `FirstOrDefault` no SQL: uma
                // consulta para o lote inteiro, e nao uma por contato. Um contato tem um punhado de
                // negociacoes — uma por funil, mais o historico —, entao trazer todas custa nada.
                // ======================================================================
                var vigentes = (await db.Negociacoes.IgnoreQueryFilters().AsNoTracking()
                        .Where(n => ids.Contains(n.ContatoId))
                        .Select(n => new { n.ContatoId, n.Id, n.Status, n.EtapaId, n.Valor, n.MotivoPerda })
                        .ToListAsync(ct))
                    .GroupBy(n => n.ContatoId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                              .ThenByDescending(n => n.Id)
                              .First());

                // O nome da etapa, tambem numa consulta so — e nenhuma no modo "so ids", que nao
                // manda nome de nada.
                var etapaIds = webhook.SomenteIds
                    ? []
                    : vigentes.Values.Select(n => n.EtapaId).Distinct().ToList();

                var nomes = etapaIds.Count == 0
                    ? new Dictionary<long, string>()
                    : await db.EtapasFunil.IgnoreQueryFilters().AsNoTracking()
                        .Where(x => etapaIds.Contains(x.Id))
                        .ToDictionaryAsync(x => x.Id, x => x.Nome, ct);

                foreach (var contato in daEmpresa)
                {
                    // ⚠️ AQUI HAVIA UM `if (negocio is null) return;`, ESCRITO NO E4e PREVENDO ESTE
                    // BLOCO — e ele estava errado para o mundo que o E6 cria.
                    //
                    // Naquele momento "contato sem negociacao" era impossivel, e sair calado
                    // parecia conservador. Com o E6 e o caso COMUM: todo lead do WhatsApp e do
                    // formulario chega sem negocio. O `return` faria `lead.criado` — o evento mais
                    // importante do INT-3 — nunca mais disparar, e o ERP do cliente pararia de
                    // receber lead SEM NENHUM SINAL de que parou. Integracao que emudece e mais
                    // cara de descobrir que campo nulo.
                    //
                    // Entao publica com `etapaId` nulo, e o contrato assumiu isso (ver `LeadWebhook`).
                    var negocio = vigentes.GetValueOrDefault(contato.Id);
                    var etapaNome = negocio is null ? null : nomes.GetValueOrDefault(negocio.EtapaId);

                    Enfileirar(
                        webhook, evento,
                        PayloadWebhook.Lead(
                            contato, negocio?.EtapaId, negocio?.Valor, negocio?.MotivoPerda,
                            etapaNome, webhook.SomenteIds, etapaAnteriorId),
                        emMassa, agora);

                    enfileirou = true;
                }
            }

            if (enfileirou) await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // O lead continua criado e a venda continua fechada. Um webhook que derruba a operação
            // do cliente é pior que um webhook que não sai.
            log.LogError(ex, "Falha ao publicar {Evento} de {Quantos} contato(s), a partir do {Id}.",
                evento, contatos.Count, contatos.First().Id);
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

            Enfileirar(
                webhook, EventoWebhook.MensagemRecebida,
                PayloadWebhook.Mensagem(
                    mensagemId, contatoId, conversaId, texto,
                    contatoNome, contatoTelefone, recebidaEm, webhook.SomenteIds),
                emMassa: false, relogio.GetUtcNow().UtcDateTime);

            await db.SaveChangesAsync(ct);
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

    private void Enfileirar(
        WebhookSaida webhook, EventoWebhook evento, object dados, bool emMassa, DateTime agora)
    {
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
            ProximaTentativaEm = agora,
            EmMassa = emMassa
        });
    }
}
