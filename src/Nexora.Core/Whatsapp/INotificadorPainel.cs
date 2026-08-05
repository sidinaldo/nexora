namespace Nexora.Core.Whatsapp;

/// <summary>Empurra evento para o painel da empresa em tempo real.
/// Interface no Core; a Api implementa com SignalR. O Core nao conhece SignalR.</summary>
public interface INotificadorPainel
{
    /// <summary>Mensagem de entrada persistida.</summary>
    Task MensagemRecebidaAsync(long empresaId, MensagemPainel mensagem, CancellationToken ct);

    /// <summary>Conversa criada (contato novo, ou primeira mensagem de um contato antigo).</summary>
    Task ConversaAbertaAsync(long empresaId, ConversaPainel conversa, CancellationToken ct);

    /// <summary>Contato criado AUTOMATICAMENTE pelo webhook — a captura de lead da fase 1.
    /// O painel usa para atualizar o kanban sem recarregar.</summary>
    Task ContatoCriadoAsync(long empresaId, ContatoPainel contato, CancellationToken ct);

    /// <summary>O ACK avancou (entregue, lido).</summary>
    Task StatusMensagemAsync(long empresaId, long mensagemId, short ack, CancellationToken ct);

    /// <summary>O numero conectou ou caiu. Acende/apaga o banner global do painel.</summary>
    Task ConexaoMudouAsync(long empresaId, ConexaoPainel conexao, CancellationToken ct);
}

public record MensagemPainel(
    long Id,
    long ConversaId,
    long ContatoId,
    string ContatoNome,
    string? Previa,
    string Direcao,
    DateTime Em);

public record ConversaPainel(
    long Id,
    long ContatoId,
    string ContatoNome,
    string Telefone);

public record ContatoPainel(
    long Id,
    string Nome,
    string Telefone,
    long EtapaId);

public record ConexaoPainel(
    long Id,
    string Status,
    string? Numero,
    string? NumeroAnterior);
