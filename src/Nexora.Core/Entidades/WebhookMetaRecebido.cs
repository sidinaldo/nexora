namespace Nexora.Core.Entidades;

/// <summary>===================== A FILA DE ENTRADA DO WEBHOOK DA META (INT-XX) =====================
///
/// Cada MUDANCA aceita de uma entrega da Cloud API (`entry[].changes[]`) vira uma linha aqui, e o
/// webhook responde 200 na hora. Quem processa e um servico em segundo plano.
///
/// ⚠️ PERSISTIDA, E NAO EM MEMORIA. A Meta reenvia o que demora a responder — por isso a resposta
/// rapida —, mas tambem para de reenviar o que ja recebeu 200. Uma fila so em memoria perderia, num
/// reinicio da API, justamente as mensagens que a Meta ja considera entregues.
///
/// A linha e transitoria: processada, e apagada depois de 7 dias. O registro do que aconteceu fica em
/// `mensagens`, como para a Evolution.
/// ==========================================================================================</summary>
public class WebhookMetaRecebido
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }
    public long ConexaoId { get; set; }

    /// <summary>O `field` da mudanca: `messages` (mensagens e status) ou os de template.</summary>
    public string Campo { get; set; } = null!;

    /// <summary>A mudanca inteira (`{ field, value }`), como a Meta mandou.</summary>
    public string Payload { get; set; } = null!;

    public DateTime RecebidoEm { get; set; }

    /// <summary>Reservada por uma rodada. Reserva velha (a rodada morreu no meio) volta para a fila.</summary>
    public DateTime? ProcessandoDesde { get; set; }

    public DateTime? ProcessadoEm { get; set; }

    /// <summary>Quantas vezes foi reservada. Depois de 5, fica parada com o `Erro` para investigar.</summary>
    public short Tentativas { get; set; }

    public string? Erro { get; set; }
}
