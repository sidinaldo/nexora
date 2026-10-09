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

    /// <summary>A mensagem mudou: o ACK avancou (entregue, lido), ou — com `ack` NULO — o
    /// conteudo dela mudou, porque foi editada no WhatsApp. A thread aberta recarrega nos dois
    /// casos, sem mexer na rolagem.
    ///
    /// ⚠️ A EDICAO REAPROVEITA ESTE EVENTO de proposito. Um evento proprio para ela exigia mexer na
    /// imitacao do servico de tempo real de 11 arquivos de teste do painel, e o que a tela faz e o
    /// mesmo: recarregar.</summary>
    Task StatusMensagemAsync(long empresaId, long mensagemId, short? ack, CancellationToken ct);

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
    DateTime Em,
    /// <summary>O total de não lidas da empresa DEPOIS desta mensagem — o badge do menu, contado
    /// no servidor (AUD-XX). A tela somava +1 por conta própria.</summary>
    int NaoLidas);

public record ConversaPainel(
    long Id,
    long ContatoId,
    string ContatoNome,
    string Telefone);

public record ContatoPainel(
    long Id,
    string Nome,
    string Telefone,
    /// <summary>⚠️ NULO desde o E6, e no caminho mais comum: lead que chega pelo WhatsApp ou por
    /// formulário não abre negociação, então não há coluna do quadro onde pôr um card.
    ///
    /// O evento continua saindo — o que ele avisa é "chegou gente nova", e isso continua
    /// verdade. Quem consome hoje é um toast, que nunca leu este campo.</summary>
    long? EtapaId);

public record ConexaoPainel(
    long Id,
    string Status,
    string? Numero,
    string? NumeroAnterior);
