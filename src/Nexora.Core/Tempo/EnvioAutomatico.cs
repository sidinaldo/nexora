namespace Nexora.Core.Tempo;

/// <summary>===================== QUANDO UMA AUTOMATICA PODE SAIR (BUG-XX) =====================
///
/// Follow-up, lembrete com mensagem e pesquisa pos-venda saem so quando as DUAS coisas valem, no
/// relogio DA EMPRESA:
///   · sao 8h ou mais — decisao do dono: nenhuma automatica antes das 8h, mesmo para quem abre
///     mais cedo;
///   · a empresa esta no horario de atendimento dela.
///
/// ⚠️ A RODADA ERA UMA SO, AS 8H DE BRASILIA, e so postava se o horario estivesse aberto naquela
/// hora. Quem abria as 9h, ou ficava em outro fuso (Manaus as 7h locais), nunca recebia nada: a
/// mensagem era guardada para "a proxima rodada", que caia de novo antes de abrir. Agora a rodada
/// passa de hora em hora, e cada empresa recebe na primeira hora em que esta regra vale.
/// ==========================================================================================</summary>
public static class EnvioAutomatico
{
    /// <summary>Antes disso, no relogio da empresa, nenhuma automatica sai.</summary>
    public const int HoraMinima = 8;

    public static bool PodeSairAgora(
        DateTime agoraLocal, JanelaAtendimento janela, IReadOnlySet<DateOnly> feriados) =>
        agoraLocal.Hour >= HoraMinima && janela.Contem(agoraLocal, feriados);
}
