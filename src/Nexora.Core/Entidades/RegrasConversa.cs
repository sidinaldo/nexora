using System.Linq.Expressions;

namespace Nexora.Core.Entidades;

/// <summary>===================== A CONVERSA PRINCIPAL DO CONTATO (CONV-XX) =====================
///
/// Desde o CONV-XX o contato tem UMA CONVERSA POR NÚMERO (`uq_conversas_contato_conexao`). Quem
/// precisa de "a conversa do contato" — a tela do contato, o card do funil, o lembrete, o
/// follow-up, o NPS — usa a principal: a de mensagem mais recente, desempate pelo id.
///
/// ⚠️ ANTES ERA `FirstOrDefault` SEM ORDEM, e funcionava porque só havia uma. Com duas, cada tela
/// sortearia a sua — o card mostraria a janela de um número e o lembrete sairia pelo outro.
///
/// UMA CÓPIA SÓ, pelo mesmo motivo de `RegrasNegociacao`. É um PREDICADO, e não uma ordenação,
/// para caber dentro de qualquer subconsulta: `.Where(RegrasConversa.Principal)`.
/// ==============================================================================</summary>
public static class RegrasConversa
{
    /// <summary>Nenhuma outra conversa do mesmo contato é mais recente. Exatamente uma por contato.</summary>
    public static Expression<Func<Conversa, bool>> Principal =>
        c => !c.Contato.Conversas.Any(o => o.UltimaMensagemEm > c.UltimaMensagemEm
                                        || (o.UltimaMensagemEm == c.UltimaMensagemEm && o.Id > c.Id));

    /// <summary>===================== QUEM ESPERA RESPOSTA DE MIM (BUG-XX) =====================
    /// Aberta, com espera, e MINHA OU SEM RESPONSÁVEL — a sem dono é de quem pegar primeiro, e
    /// cobra todo mundo. O Meu Dia contava assim e o Início do vendedor contava só as dele: os
    /// dois números da mesma pessoa não batiam. `usuarioId` nulo = a empresa toda (o dono).
    /// ==============================================================================</summary>
    public static Expression<Func<Conversa, bool>> EsperandoResposta(long? usuarioId) =>
        c => c.Status == StatusConversa.Aberta
          && c.AguardandoDesde != null
          && (usuarioId == null || c.ResponsavelId == usuarioId || c.ResponsavelId == null);
}
