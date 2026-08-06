using Nexora.Core.Entidades;

namespace Nexora.Core.Webhooks;

/// <summary>Coloca um evento na fila de saída — e SÓ ISSO.
///
/// ===================== NÃO ENTREGA NADA =====================
/// Fechar uma venda não pode ficar lento porque o servidor do cliente está devagar. O que acontece
/// aqui é um INSERT; quem posta é a rodada de drenagem, depois. Um receptor com 10s de latência
/// não pode aparecer como 10s no botão "Venda fechada".
///
/// A única exceção é o evento de TESTE, disparado pelo botão da tela: ali a pessoa está esperando
/// o resultado na frente dela, e um "enviado, veja depois" não resolveria o chamado de suporte que
/// aquele botão existe para resolver.
/// ============================================================
///
/// ===================== NUNCA LANÇA =====================
/// Se a publicação falhar, o lead continua criado e a venda continua fechada. Um webhook que
/// derruba a operação do cliente é pior que um webhook que não sai — e quem descobre que não saiu
/// é o registro de entregas, que é o motivo de ele existir.
/// =======================================================</summary>
public interface IPublicadorEventos
{
    /// <summary>Enfileira o evento de um CONTATO, se a empresa assinar esse evento.
    ///
    /// `etapaAnteriorId` só faz sentido em `lead.movido` — é o que permite ao receptor saber de
    /// onde o card veio, e não só onde ele está.</summary>
    Task PublicarContatoAsync(
        EventoWebhook evento, Contato contato, long? etapaAnteriorId = null,
        CancellationToken ct = default);

    /// <summary>Enfileira `mensagem.recebida`.
    ///
    /// Recebe `empresaId` explícito porque o único caminho que dispara isto é o webhook da
    /// Evolution, que roda SEM tenant no contexto — o mesmo motivo de todo `IgnoreQueryFilters`
    /// daquele arquivo.</summary>
    Task PublicarMensagemAsync(
        long empresaId, long mensagemId, long contatoId, long conversaId,
        string? texto, string contatoNome, string contatoTelefone, DateTime recebidaEm,
        CancellationToken ct = default);
}
