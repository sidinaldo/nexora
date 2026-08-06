using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Webhooks;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Webhooks;

namespace Nexora.Infra.Servicos;

/// <summary>A configuração do webhook de saída, na área logada.
///
/// Query filter global vale — é caminho autenticado. O oposto do `PublicadorEventos` e do
/// `MotorWebhooks`, que rodam como job e usam `IgnoreQueryFilters` com filtro explícito.
///
/// O enforcement de PAPEL (só dono) é do controller, como no resto do sistema.</summary>
public class ServicoWebhooks(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    IResolvedorDns dns,
    IClienteWebhook cliente,
    TimeProvider relogio) : IServicoWebhooks
{
    /// <summary>Quantas entregas a tela mostra. Cinquenta responde "está chegando?" e "o que
    /// falhou hoje?"; mais que isso é trabalho para uma consulta, não para uma tela.</summary>
    private const int UltimasEntregas = 50;

    public async Task<PainelWebhook> ObterAsync(CancellationToken ct)
    {
        var webhook = await db.WebhooksSaida.AsNoTracking()
            .Select(w => new WebhookDto(
                w.Id, w.Url, w.Ativo, w.SomenteIds,
                w.EmLeadCriado, w.EmLeadMovido, w.EmVendaFechada, w.EmVendaPerdida,
                w.EmMensagemRecebida, w.CriadoEm))
            .FirstOrDefaultAsync(ct);

        var entregas = await db.EntregasWebhook.AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Take(UltimasEntregas)
            .Select(e => new EntregaWebhookDto(
                e.Id, e.Evento.ParaApi(), e.Status.ToString().ToLowerInvariant(),
                e.Tentativas, e.CodigoResposta, e.Erro,
                e.ProximaTentativaEm, e.EntregueEm, e.CriadoEm, e.Payload,
                // Reenviar só o que DESISTIU. Pendente já vai ser tentada sozinha, e reenviar uma
                // entregue mandaria o mesmo evento duas vezes para quem já processou.
                e.Status == StatusEntregaWebhook.Falhou))
            .ToListAsync(ct);

        return new PainelWebhook(webhook, entregas);
    }

    public async Task<SegredoRevelado?> SalvarAsync(SalvarWebhook dados, CancellationToken ct)
    {
        var url = await ValidarUrlAsync(dados.Url, ct);

        var webhook = await db.WebhooksSaida.FirstOrDefaultAsync(ct);
        var novo = webhook is null;

        if (webhook is null)
        {
            webhook = new WebhookSaida
            {
                EmpresaId = contexto.EmpresaId,
                Segredo = AssinaturaWebhook.GerarSegredo()
            };
            db.WebhooksSaida.Add(webhook);
        }

        webhook.Url = url;
        webhook.Ativo = dados.Ativo;
        webhook.SomenteIds = dados.SomenteIds;
        webhook.EmLeadCriado = dados.EmLeadCriado;
        webhook.EmLeadMovido = dados.EmLeadMovido;
        webhook.EmVendaFechada = dados.EmVendaFechada;
        webhook.EmVendaPerdida = dados.EmVendaPerdida;
        webhook.EmMensagemRecebida = dados.EmMensagemRecebida;

        await db.SaveChangesAsync(ct);

        // O segredo só sai na CRIAÇÃO. Atualizar a URL não pode ser um jeito de recuperá-lo — se
        // fosse, "mostrado uma vez" seria só uma frase na tela.
        return novo ? new SegredoRevelado(webhook.Id, webhook.Segredo, Novo: true) : null;
    }

    public async Task<SegredoRevelado> RegerarSegredoAsync(CancellationToken ct)
    {
        var webhook = await MeuWebhookAsync(ct);

        // O antigo para de assinar NA HORA. É o ponto: quem regera está reagindo a um vazamento, e
        // até trocar a chave do lado dele as entregas vão chegar com assinatura que não confere —
        // o que é o comportamento certo, e a tela avisa.
        webhook.Segredo = AssinaturaWebhook.GerarSegredo();
        await db.SaveChangesAsync(ct);

        return new SegredoRevelado(webhook.Id, webhook.Segredo, Novo: false);
    }

    public async Task RemoverAsync(CancellationToken ct)
    {
        var webhook = await MeuWebhookAsync(ct);

        // As entregas FICAM. Elas são registro do que saiu daqui, e apagar junto tiraria a única
        // resposta para "vocês mandaram ou não mandaram?" — inclusive depois de o cliente desligar
        // a integração. O expurgo por idade cuida delas.
        db.WebhooksSaida.Remove(webhook);
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== teste
    public async Task<ResultadoTeste> TestarAsync(CancellationToken ct)
    {
        var webhook = await MeuWebhookAsync(ct);

        // Revalida a URL ANTES de postar, com DNS fresco — o botão de teste é uma entrega como
        // qualquer outra, e não pode ser o buraco por onde uma URL interna é alcançada.
        var url = await ValidadorUrlWebhook.ValidarAsync(webhook.Url, dns, ct);
        if (!url.Ok) throw new RegraDeNegocioException(url.Motivo!);

        var agora = relogio.GetUtcNow().UtcDateTime;
        var eventoId = Guid.NewGuid();

        // ===================== DADO SINTÉTICO, DE PROPÓSITO =====================
        // O teste não pode depender de existir contato na base: numa conta recém-criada — que é
        // exatamente quando o dono configura a integração — não existe nenhum. Ids negativos
        // marcam o exemplo: nenhum id real do sistema é negativo, então o receptor que gravar isso
        // por engano tem como achar depois.
        // =======================================================================
        var payload = PayloadWebhook.Montar(eventoId, EventoWebhook.Teste, webhook.EmpresaId, agora,
            new
            {
                mensagem = "Este é um evento de teste do Nexora. Nenhum dado real foi enviado.",
                exemplo = new { id = -1, nome = "Contato de exemplo", telefone = "5511900000000" }
            });

        var resultado = await cliente.EntregarAsync(
            webhook.Url, webhook.Segredo, payload, EventoWebhook.Teste.ParaApi(), eventoId, ct);

        // Registrada como qualquer outra entrega, e SEM retry (`proxima_tentativa_em` nulo): o
        // teste é síncrono e a pessoa já viu o resultado. Reagendá-lo faria o botão mandar o mesmo
        // evento de novo, sozinho, um minuto depois.
        db.EntregasWebhook.Add(new EntregaWebhook
        {
            EmpresaId = webhook.EmpresaId,
            EventoId = eventoId,
            Evento = EventoWebhook.Teste,
            Payload = payload,
            Url = webhook.Url,
            Status = resultado.Aceitou ? StatusEntregaWebhook.Entregue : StatusEntregaWebhook.Falhou,
            Tentativas = 1,
            CodigoResposta = resultado.Codigo,
            Erro = resultado.Erro,
            EntregueEm = resultado.Aceitou ? agora : null,
            ProximaTentativaEm = null
        });
        await db.SaveChangesAsync(ct);

        return new ResultadoTeste(resultado.Aceitou, resultado.Codigo, resultado.Erro);
    }

    public async Task ReenviarAsync(long entregaId, CancellationToken ct)
    {
        var entrega = await db.EntregasWebhook.FirstOrDefaultAsync(e => e.Id == entregaId, ct)
            ?? throw new RegraDeNegocioException("Entrega não encontrada.");

        if (entrega.Status != StatusEntregaWebhook.Falhou)
            throw new RegraDeNegocioException(
                entrega.Status == StatusEntregaWebhook.Pendente
                    ? "Esta entrega ainda está na fila — ela vai ser tentada sozinha."
                    : "Esta entrega já foi aceita pelo receptor. Reenviar mandaria o mesmo evento "
                    + "duas vezes.",
                conflito: true);

        // Volta para a fila com as tentativas ZERADAS: ganha três novas. E o `evento_id` NÃO muda
        // — o receptor precisa continuar reconhecendo que é o mesmo evento, senão o reenvio vira
        // duplicata do lado dele.
        entrega.Status = StatusEntregaWebhook.Pendente;
        entrega.Tentativas = 0;
        entrega.Erro = null;
        entrega.CodigoResposta = null;
        entrega.ProximaTentativaEm = relogio.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== apoio
    private async Task<WebhookSaida> MeuWebhookAsync(CancellationToken ct) =>
        await db.WebhooksSaida.FirstOrDefaultAsync(ct)
        ?? throw new RegraDeNegocioException("Nenhum webhook configurado.");

    /// <summary>Formato + DNS, no CADASTRO. A mesma checagem roda de novo antes de cada entrega —
    /// ver `MotorWebhooks.TentarAsync` e o bloco sobre por que uma vez não basta.</summary>
    private async Task<string> ValidarUrlAsync(string? url, CancellationToken ct)
    {
        var resultado = await ValidadorUrlWebhook.ValidarAsync(url, dns, ct);
        if (!resultado.Ok) throw new RegraDeNegocioException(resultado.Motivo!);
        return url!.Trim();
    }
}
