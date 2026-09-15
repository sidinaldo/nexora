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
///
/// ⚠️ `RegrasContato` NAO EXISTE MAIS — foi apagada no E4e/3b. Os dois predicados acima estao
/// aqui como TRADUCAO, para quem for ler um commit antigo ou um comentario que ainda os cite;
/// procura-los no codigo nao acha nada. A classe ficou sem um unico uso vivo quando o ultimo
/// servico parou de ler as colunas de funil do contato, e manter codigo morto que le colunas
/// prestes a cair so adiaria o erro de compilacao para dentro da migracao do E4e/4.
///   `anonimizado_em IS NULL`                                 ->  continua vindo do CONTATO
///
/// O anonimizado continua sendo pergunta ao contato porque a anonimização é sobre a PESSOA, não
/// sobre o negócio: o titular pediu para sumir, e some de todos os negócios dele de uma vez.
/// ====================================================================</summary>
public static class RegrasNegociacao
{
    /// <summary>===================== O CONTATO QUE AINDA SE PERSEGUE (E6) =====================
    /// Tem negócio ABERTO **ou não tem negócio nenhum**.
    ///
    /// ⚠️ A SEGUNDA METADE É O CONSERTO DE UM DEFEITO QUE O E6 CRIOU EM SILÊNCIO. Dois lugares
    /// escreviam `Negociacoes.Any(Aberta)` como tradução de "não está em estado terminal" —
    /// e enquanto todo contato nascia com negociação as duas frases eram a mesma coisa.
    ///
    /// Desde o E6 não são: o lead que chega pela caixa não tem negócio, e caía do lado de fora
    /// junto com ganho e perdido. O efeito media-se assim:
    ///   · o motor de follow-up parou de gerar lembrete para lead novo — o vendedor respondia,
    ///     o cliente sumia por cinco dias, e nada acontecia;
    ///   · a tela de Contatos, que abre em "Abertos", parou de mostrá-lo.
    ///
    /// Em ambos os casos o lead mais fresco da base era o único que o sistema ignorava.
    ///
    /// ⚠️ UMA CÓPIA SÓ, e este arquivo existe por causa disso: o predicado do funil escrito por
    /// extenso em dois serviços já divergiu neste projeto, e o cliente viu o dashboard dizer 72
    /// onde o quadro mostrava 69. Quem precisa dele a partir de uma CONVERSA usa um `EXISTS`
    /// sobre `contatos` em vez de reescrever — ver `DadosFollowUp`.
    /// ==============================================================================</summary>
    public static Expression<Func<Contato, bool>> ContatoEmAberto =>
        c => c.Negociacoes.Any(n => n.Status == StatusNegociacao.Aberta)
          || !c.Negociacoes.Any();

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
