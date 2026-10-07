namespace Nexora.Core.Nps;

/// <summary>===================== O QUE ACONTECE DEPOIS DA NOTA =====================
///
/// As tres faixas, e o que cada uma dispara:
///
///   9-10  PROMOTOR   envia `NpsMensagemPromotor`, se configurada
///   7-8   NEUTRO     nada
///   0-6   DETRATOR   lembrete para quem vendeu E aviso para o dono; envia
///                    `NpsMensagemDetrator`, se configurada
///
/// ⚠️ O "AVISO PARA O DONO" E UM LEMBRETE, e nao ha outro mecanismo que sirva. `INotificadorPainel`
/// tem cinco eventos especificos de tempo real e nenhum de alerta; e-mail por detrator seria
/// barulho que ninguem pediu. O MEU DIA e onde trabalho aparece neste produto, e o dono tem um.
///
/// ⚠️ UM LEMBRETE SO QUANDO O RESPONSAVEL E O DONO, que e o caso comum na empresa pequena. Dois
/// identicos na mesma lista nao avisam duas vezes: avisam que o sistema nao sabe quem e quem.
///
/// ⚠️ A MENSAGEM DE AGRADECIMENTO E OPCIONAL E NASCE VAZIA. Uma segunda automatica depois da
/// primeira dobra o risco do numero, e nem toda empresa quer. A ACAO HUMANA do detrator, nao: ela
/// acontece de qualquer jeito.
/// ========================================================================</summary>
public interface IAcoesDaNota
{
    /// <summary>⚠️ CHAMADO UMA VEZ POR PESQUISA, e quem garante isso e o chamador: a transicao de
    /// status e um UPDATE condicional, e a acao so corre quando ele afetou UMA linha. Sem essa
    /// guarda, dois webhooks simultaneos do mesmo contato poderiam agradecer duas vezes.</summary>
    Task ExecutarAsync(long pesquisaId, CancellationToken ct);
}
