namespace Nexora.Core.Servicos;

/// <summary>O webhook da empresa, como o painel o vê.
///
/// ⚠️ SEM O SEGREDO. Ele sai UMA vez, na criação e quando é regerado — depois disso nem a tela do
/// dono o recupera. Não é teatro: um segredo que a tela busca a cada carregamento é um segredo que
/// vive no histórico do navegador, no cache do proxy e na captura de tela do suporte.</summary>
public record WebhookDto(
    long Id,
    string Url,
    bool Ativo,
    bool SomenteIds,
    bool EmLeadCriado,
    bool EmLeadMovido,
    bool EmVendaFechada,
    bool EmVendaPerdida,
    bool EmMensagemRecebida,
    DateTime CriadoEm);

/// <summary>Uma entrega no registro. `Payload` vai junto porque "o cliente diz que não recebeu"
/// costuma terminar em "o que exatamente vocês mandaram?".</summary>
public record EntregaWebhookDto(
    long Id,
    string Evento,
    string Status,
    short Tentativas,
    int? CodigoResposta,
    string? Erro,
    DateTime? ProximaTentativaEm,
    DateTime? EntregueEm,
    DateTime CriadoEm,
    string Payload,
    bool PodeReenviar);

public record PainelWebhook(WebhookDto? Webhook, IReadOnlyList<EntregaWebhookDto> Entregas);

public record SalvarWebhook(
    string Url,
    bool Ativo,
    bool SomenteIds,
    bool EmLeadCriado,
    bool EmLeadMovido,
    bool EmVendaFechada,
    bool EmVendaPerdida,
    bool EmMensagemRecebida);

/// <summary>O segredo, entregue UMA vez. `Novo` distingue "acabei de criar" de "atualizei" — a
/// tela só mostra o painel de "guarde isto agora" no primeiro caso.</summary>
public record SegredoRevelado(long Id, string Segredo, bool Novo);

/// <summary>O que aconteceu no botão "Enviar evento de teste". `Codigo` nulo = nem houve resposta
/// (DNS, timeout, conexão recusada).</summary>
public record ResultadoTeste(bool Ok, int? Codigo, string? Erro);

public interface IServicoWebhooks
{
    /// <summary>A configuração + as últimas entregas. `Webhook` nulo = a empresa nunca configurou.</summary>
    Task<PainelWebhook> ObterAsync(CancellationToken ct);

    /// <summary>Cria ou atualiza. Devolve o segredo SÓ quando cria — atualizar não mexe nele, e
    /// revelá-lo de novo a cada salvamento anularia a decisão de mostrá-lo uma vez.</summary>
    Task<SegredoRevelado?> SalvarAsync(SalvarWebhook dados, CancellationToken ct);

    /// <summary>Gera um segredo novo. O antigo para de assinar NA HORA: quem regera está reagindo
    /// a um vazamento, e o receptor precisa trocar a chave dele.</summary>
    Task<SegredoRevelado> RegerarSegredoAsync(CancellationToken ct);

    Task RemoverAsync(CancellationToken ct);

    /// <summary>Dispara um evento de teste e ESPERA a resposta.
    ///
    /// É o único lugar do sistema que entrega dentro da requisição, e a exceção se justifica: a
    /// pessoa está olhando o botão. Um "enviado, veja depois" não resolveria o chamado de suporte
    /// que este botão existe para resolver — que é sempre "não está chegando, e não sei por quê".
    ///
    /// Não depende de dado real: o payload é de exemplo, com ids sintéticos.</summary>
    Task<ResultadoTeste> TestarAsync(CancellationToken ct);

    /// <summary>Devolve uma entrega falha para a fila. Não entrega na hora — volta a ser
    /// `pendente` com as tentativas zeradas, e a próxima rodada a posta.</summary>
    Task ReenviarAsync(long entregaId, CancellationToken ct);
}
