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

    /// <summary>⚠️ A REGRA QUE SEGURA O QUADRO EM UM CARD POR CONTATO — e ela é TEMPORÁRIA.
    ///
    /// ===================== POR QUE ELA EXISTE AGORA =====================
    /// Depois do E4b, reabrir um contato que já ganhou deixa DUAS negociações vivas: a ganha, que
    /// continua esperando conclusão, e a nova aberta. É o estado que o modelo velho não sabia
    /// representar, e mostrá-lo é o ganho do E4.
    ///
    /// Mas o card ainda é identificado pelo id do CONTATO, e o arrasto ainda manda `contatoId`.
    /// Com as duas na tela, arrastar o card da coluna de ganho moveria a negociação ABERTA — o
    /// card errado se mexeria, e sem erro nenhum.
    ///
    /// Então este bloco troca só a FONTE da leitura, e o quadro continua mostrando o que mostra
    /// hoje. A regra cai no commit seguinte, junto com a virada do contrato para `negociacaoId`.
    ///
    /// "Vigente" = a ABERTA, se houver; senão a ganha mais recente.
    ///
    /// ⚠️ A PRIMEIRA VERSÃO ERA "a de maior id", E ESTAVA ERRADA — pego comparando as duas
    /// leituras contra os 1015 contatos do banco de desenvolvimento. Um contato tinha
    /// `aberta` id 245 e `ganha` id 1074, e o card pulava da coluna 30 para a 32.
    ///
    /// O motivo: a migração do E4b APAGOU E REINSERIU as negociações vindas de venda, para
    /// gravar o elo `venda_id`. Os ids delas passaram a ser maiores que os das abertas, e id
    /// deixou de refletir cronologia. É o mesmo tipo de armadilha do "timestamp não é chave" que
    /// o `CancelarAsync` registra — aqui era "id não é relógio".
    ///
    /// Preferir a aberta não é só mais robusto, é mais CERTO: um contato com `ganho_em` vazio
    /// aparece hoje como card aberto mesmo tendo uma venda `fechada` pendurada, porque o quadro
    /// é montado pela posição do contato. A ganha que sobrou é resto de rodada anterior.
    ///
    /// O desempate por id fica só ENTRE GANHAS, que é alcançável: ganhar, reabrir e ganhar de
    /// novo deixa duas. Sem ele, as duas apareceriam na coluna de ganho.
    /// ==================================================================</summary>
    public static Expression<Func<Negociacao, bool>> CardVigente =>
        n => n.Status == StatusNegociacao.Aberta
             || !n.Contato.Negociacoes.Any(
                 o => o.Status == StatusNegociacao.Aberta
                      || (o.Status == StatusNegociacao.Ganha && o.Id > n.Id));
}
