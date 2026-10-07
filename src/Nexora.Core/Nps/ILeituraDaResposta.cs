namespace Nexora.Core.Nps;

/// <summary>O que a leitura fez com a mensagem recebida.</summary>
public enum RespostaDaPesquisa
{
    /// <summary>Nao havia pesquisa esperando, ou a mensagem nao e nota. Segue a vida normal.</summary>
    Nenhuma,

    /// <summary>Nota registrada. ⚠️ A MENSAGEM FOI CONSUMIDA: nao acende o semaforo nem conta como
    /// nao lida — "10" nao e pergunta, ninguem tem de responder.</summary>
    NotaRegistrada,

    /// <summary>Ha um numero e o leitor nao se arrisca. A pesquisa vai para `PossivelNota` e o
    /// vendedor decide na conversa.
    ///
    /// ⚠️ ESTA *ACENDE* O SEMAFORO, ao contrario da nota confirmada, e e deliberado: pode nao ser
    /// nota nenhuma. "quero 2 unidades" e um pedido esperando resposta, e apagar a espera dele para
    /// perguntar "isto e uma nota?" trocaria um atendimento perdido por uma duvida respondida.</summary>
    DuvidaRegistrada
}

/// <summary>===================== LER A NOTA NA MENSAGEM QUE CHEGOU =====================
///
/// Roda dentro do processamento do webhook, uma vez por mensagem de ENTRADA. A decisao de "isto e
/// uma nota?" nao esta aqui — ela e do `LeitorDeNota`, que e pura e tem a suite inteira. Aqui mora
/// o que o banco precisa saber: ha pesquisa esperando? qual mensagem foi citada? o que gravar?
///
/// ⚠️ CAMINHO QUENTE: toda mensagem recebida passa por aqui. A primeira coisa que o metodo faz e a
/// consulta mais estreita possivel — `ix_pesquisas_nps_aberta` e parcial nos dois estados que ainda
/// esperam algo — e sem pesquisa aberta ele devolve `Nenhuma` sem olhar o texto.
/// ==========================================================================</summary>
public interface ILeituraDaResposta
{
    /// <param name="stanzaIdCitado">O `wa_message_id` da mensagem citada, de
    /// `data.contextInfo.stanzaId`. Nulo quando a resposta nao cita nada — o que e o caso comum:
    /// medi 3 citacoes em 1440 entradas no `nexora_dev`.</param>
    Task<RespostaDaPesquisa> LerAsync(
        long empresaId, long contatoId, long mensagemId, string? texto, string? stanzaIdCitado,
        CancellationToken ct);

    /// <summary>===================== AS ACOES DA NOTA, DEPOIS DO COMMIT =====================
    ///
    /// ⚠️ `LerAsync` DECIDE e GRAVA; isto AGE — e e separado de proposito (revisao NPS-1). A
    /// leitura roda DENTRO da transacao do webhook, porque o semaforo precisa saber se a entrada
    /// era nota. As acoes mandam WhatsApp, e rodavam ali dentro tambem: se a gravacao da conversa
    /// ou o commit falhassem depois, a nota voltava atras mas o agradecimento ja tinha saido — e a
    /// reentrega do webhook agradecia de novo. E o POST segurava os locks da transacao inteira.
    ///
    /// Chame DEPOIS do commit, e so quando `LerAsync` devolveu `NotaRegistrada`. A nota e achada
    /// pela MENSAGEM que a trouxe: sem estado guardado entre as duas chamadas.
    /// =================================================================================</summary>
    Task AgirAsync(long empresaId, long mensagemId, CancellationToken ct);
}
