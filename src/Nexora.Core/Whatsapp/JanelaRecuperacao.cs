namespace Nexora.Core.Whatsapp;

/// <summary>===================== O QUE CONTA COMO MENSAGEM RECUPERADA =====================
///
/// Mensagem que entra com o timestamp bem mais velho que o instante da gravacao chegou ATRASADA:
/// ou a API estava fora e a Evolution reentregou o webhook, ou a instancia caiu e o WhatsApp so
/// entregou ao reconectar. Nos dois casos o vendedor precisa saber, senao dez conversas surgindo
/// de uma vez parecem defeito.
///
/// NAO existe caminho de importacao: a mensagem entra pelo webhook de sempre. O que muda e o
/// CARIMBO — e ele e decidido aqui, num lugar so, para a tela e o teste concordarem.
/// ============================================================================================
/// </summary>
public static class JanelaRecuperacao
{
    /// <summary>Cinco minutos. Entrega normal leva segundos; a primeira retentativa da Evolution
    /// sai em 5s e o teto do backoff dela e 300s.
    ///
    /// O numero e alto de proposito. O timestamp vem do servidor do WhatsApp e o nosso relogio e
    /// outro — um limiar de segundos carimbaria "recuperada" em mensagem normal so por desvio de
    /// relogio, e um aviso que aparece sem queda nenhuma treina o vendedor a ignora-lo.</summary>
    public static readonly TimeSpan Limiar = TimeSpan.FromMinutes(5);

    /// <summary>Teto de 7 dias. Queda de horas ou dias e operacao; alem disso e outra conversa.
    ///
    /// Aqui ele NAO recorta o que entra — quem decide o que entrega e o WhatsApp, e recusar uma
    /// mensagem que ele nos deu seria jogar fora o dado do cliente. O teto governa o AVISO: uma
    /// mensagem de tres meses atras nao pertence a "o periodo em que o WhatsApp esteve fora", e
    /// anuncia-la assim seria mentira. Ela entra, sem carimbo.</summary>
    public static readonly TimeSpan Teto = TimeSpan.FromDays(7);

    /// <summary>O instante a gravar em `recuperada_em`, ou NULL quando a mensagem chegou em tempo
    /// real (o caso normal).
    ///
    /// `quando` no futuro devolve NULL: relogio adiantado do outro lado nao e atraso.</summary>
    public static DateTime? CarimboDe(DateTime quandoDaMensagem, DateTime agora)
    {
        var atraso = agora - quandoDaMensagem;
        return atraso >= Limiar && atraso <= Teto ? agora : null;
    }
}
