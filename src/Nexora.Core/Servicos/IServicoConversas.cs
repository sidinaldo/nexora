using Nexora.Core.Whatsapp;

namespace Nexora.Core.Servicos;

/// <summary>Resultado de responder. `Enviada=false` NAO e erro do ponto de vista do produto: a
/// mensagem foi registrada e aparece na conversa marcada como "nao chegou". Devolver 502 aqui
/// esconderia o id da mensagem recem-criada, que a tela precisa para renderizar o balao.</summary>
public record RespostaEnviada(long MensagemId, bool Enviada, string? Erro);

public interface IServicoConversas
{
    /// <summary>O vendedor responde na conversa. Protocolo grava->dispara->confirma.
    ///
    /// Efeitos colaterais na MESMA transacao: zera aguardando_desde e nao_lidas (respondemos,
    /// ninguem mais espera) e ATRIBUI a conversa se ela nao tinha dono.</summary>
    Task<RespostaEnviada> ResponderAsync(long conversaId, string texto, CancellationToken ct);

    /// <summary>O vendedor assume a conversa. 409 se ja for de OUTRO — assim ninguem "rouba" um
    /// atendimento em andamento sem querer. Reassumir a propria e no-op.</summary>
    Task AssumirAsync(long conversaId, CancellationToken ct);

    /// <summary>Devolve a conversa para "Nao atribuidas". So quem esta atendendo pode.</summary>
    Task LiberarAsync(long conversaId, CancellationToken ct);
}
