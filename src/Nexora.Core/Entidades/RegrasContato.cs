using System.Linq.Expressions;

namespace Nexora.Core.Entidades;

/// <summary>As regras de VISIBILIDADE do contato, num lugar só.
///
/// ===================== O BUG QUE ISTO EXISTE PARA IMPEDIR =====================
/// O predicado do funil estava escrito por extenso em dois serviços, e eles divergiram: o
/// `ServicoFunil` filtrava perdido E anonimizado; o `ServicoDashboard` filtrava só perdido.
///
/// O sintoma para o cliente: o dashboard dizia 72 em "Proposta" e o quadro tinha 69 cards. Ele
/// não conclui "há um filtro divergente" — conclui que os NÚMEROS DO SISTEMA NÃO SÃO CONFIÁVEIS.
/// Num produto que vende controle de dados, é o pior tipo de bug que existe.
///
/// Duas cópias da mesma regra divergem de novo na próxima mudança. Esta é a única cópia.
/// ==============================================================================
///
/// ===================== POR QUE `Expression`, E NÃO UM MÉTODO =====================
/// O EF só traduz o que consegue LER na árvore de expressão. Uma chamada a método próprio dentro
/// de um `Where` estoura com "could not be translated" em tempo de execução — já aconteceu neste
/// projeto (ver a nota no `ServicoFunil` sobre o predicado escrito por extenso).
///
/// Uma `Expression<Func<...>>` é a árvore em si: o EF a compõe na consulta e o SQL sai igual ao
/// que sairia escrito à mão.
/// ================================================================================</summary>
public static class RegrasContato
{
    /// <summary>Contato que APARECE no quadro e entra nas contagens do funil.
    ///
    /// PERDIDO sai porque o negócio acabou — mantê-lo na coluna faria a etapa crescer para sempre.
    /// ANONIMIZADO sai porque ele foi apagado a pedido do titular: contá-lo manteria exatamente o
    /// rastro que a anonimização existe para remover.
    ///
    /// Espelha o índice parcial `ix_contatos_kanban` (`WHERE perdido_em IS NULL`), que é mais
    /// LARGO de propósito: o índice entrega as linhas por etapa e ordem, e o Postgres descarta as
    /// anonimizadas por cima — poucas, e o índice continua servindo.</summary>
    public static Expression<Func<Contato, bool>> NoQuadro =>
        c => c.PerdidoEm == null && c.AnonimizadoEm == null;

    /// <summary>Contato com venda EM ABERTO (NEG-2). Aplicada SO na etapa de ganho.
    ///
    /// A coluna Venda acumulava para sempre: quem comprou em marco continuava la em dezembro, e
    /// depois de um ano eram centenas de cards que nao diziam nada. Agora ela mostra o que ainda
    /// tem pendencia — concluida e cancelada saem.
    ///
    /// Mesma disciplina do `NoQuadro`: UMA copia da regra. Escrita por extenso nos dois servicos
    /// que a usam, ela divergiria — e o `NoQuadro` existe justamente porque isso ja aconteceu.</summary>
    /// <summary>Tem pedido VIVO — nem concluido, nem cancelado (NEG-2).
    ///
    /// So se aplica a etapa de GANHO. Nas outras nao ha venda nenhuma, e o predicado zeraria a
    /// coluna inteira.</summary>
    public static Expression<Func<Contato, bool>> ComVendaEmAberto =>
        c => c.Vendas.Any(v => v.Status == StatusVenda.Fechada);
}
