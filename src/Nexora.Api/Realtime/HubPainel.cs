using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nexora.Core.Whatsapp;

namespace Nexora.Api.Realtime;

/// <summary>Hub do painel. Cada conexao entra no grupo `empresa-{id}` — o isolamento
/// multi-tenant do realtime segue o mesmo empresa_id do resto do sistema.
///
/// So EMPURRA (servidor -> painel). Responder ao contato e um POST normal.</summary>
[Authorize]
public class HubPainel : Hub
{
    public static string Grupo(long empresaId) => $"empresa-{empresaId}";

    public override async Task OnConnectedAsync()
    {
        // Le o claim direto da CONEXAO, NAO via IHttpContextAccessor: dentro de um WebSocket
        // nao existe HttpContext ativo, e o accessor devolveria null. O usuario ficaria fora do
        // grupo e o realtime simplesmente nao chegaria — sem erro nenhum.
        var claim = Context.User?.FindFirst(ContextoEmpresaHttp.ClaimEmpresa)?.Value;

        if (long.TryParse(claim, out var empresaId) && empresaId != 0)
            await Groups.AddToGroupAsync(Context.ConnectionId, Grupo(empresaId));

        await base.OnConnectedAsync();
    }
}

/// <summary>Implementa o contrato do Core com SignalR. O Core nao conhece SignalR — so sabe
/// que existe "um jeito de avisar o painel".</summary>
public class NotificadorSignalR(IHubContext<HubPainel> hub) : INotificadorPainel
{
    public Task MensagemRecebidaAsync(long empresaId, MensagemPainel mensagem, CancellationToken ct) =>
        hub.Clients.Group(HubPainel.Grupo(empresaId)).SendAsync("mensagemRecebida", mensagem, ct);

    public Task ConversaAbertaAsync(long empresaId, ConversaPainel conversa, CancellationToken ct) =>
        hub.Clients.Group(HubPainel.Grupo(empresaId)).SendAsync("conversaAberta", conversa, ct);

    public Task ContatoCriadoAsync(long empresaId, ContatoPainel contato, CancellationToken ct) =>
        hub.Clients.Group(HubPainel.Grupo(empresaId)).SendAsync("contatoCriado", contato, ct);

    public Task StatusMensagemAsync(long empresaId, long mensagemId, short ack, CancellationToken ct) =>
        hub.Clients.Group(HubPainel.Grupo(empresaId))
            .SendAsync("statusMensagem", new { mensagemId, ack }, ct);

    public Task ConexaoMudouAsync(long empresaId, ConexaoPainel conexao, CancellationToken ct) =>
        hub.Clients.Group(HubPainel.Grupo(empresaId)).SendAsync("conexaoMudou", conexao, ct);
}
