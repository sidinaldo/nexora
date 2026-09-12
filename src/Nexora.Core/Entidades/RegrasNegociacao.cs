using System.Linq.Expressions;

namespace Nexora.Core.Entidades;

/// <summary>As regras de VISIBILIDADE da negociação no quadro, num lugar só.
///
/// É a continuação direta de <see cref="RegrasContato"/>, e existe pelo mesmo motivo: o predicado
/// do funil escrito por extenso em dois serviços já divergiu uma vez neste projeto, e o cliente
/// viu o dashboard dizer 72 onde o quadro mostrava 69. Uma cópia só.
///
/// ===================== A TRADUÇÃO, LINHA POR LINHA =====================
/// O que `ServicoFunil` perguntava a `contatos`/`vendas`, agora pergunta aqui:
///
///   `RegrasContato.NoQuadro`          (perdido_em IS NULL)   ->  Status == Aberta
///   `RegrasContato.ComVendaEmAberto`  (tem venda `fechada`)  ->  Status == Ganha
///   `anonimizado_em IS NULL`                                 ->  continua vindo do CONTATO
///
/// O anonimizado continua sendo pergunta ao contato porque a anonimização é sobre a PESSOA, não
/// sobre o negócio: o titular pediu para sumir, e some de todos os negócios dele de uma vez.
/// ====================================================================</summary>
public static class RegrasNegociacao
{
    /// <summary>Negociação que APARECE no quadro e entra nas contagens.
    ///
    /// `Concluida`, `Perdida` e `Cancelada` ficam de fora — as três já acabaram, e mantê-las faria
    /// a coluna crescer para sempre, que é exatamente o defeito que o NEG-2 corrigiu na coluna de
    /// ganho.
    ///
    /// Espelha o índice parcial `ix_negociacoes_kanban` (`status NOT IN ('perdida','cancelada')`),
    /// que é mais LARGO de propósito: o índice entrega por etapa e ordem, e o Postgres descarta as
    /// concluídas e as anonimizadas por cima.</summary>
    public static Expression<Func<Negociacao, bool>> NoQuadro =>
        n => (n.Status == StatusNegociacao.Aberta || n.Status == StatusNegociacao.Ganha)
             && n.Contato.AnonimizadoEm == null;
}
