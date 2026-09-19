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

    /// <summary>JÁ COMPROU e não tem nada em andamento — a aba "Ganhos" da lista de contatos.
    ///
    /// `Concluida` conta junto com `Ganha`: concluir é o fim do PEDIDO, não do relacionamento, e
    /// quem já pagou não deixa de ser cliente porque a entrega saiu.
    ///
    /// ⚠️ `!Any(Aberta)` NA FRENTE, e é o que faz as abas se somarem exatamente à base. Sem essa
    /// metade, quem comprou e voltou a negociar apareceria nas DUAS abas, e a conta de "Em aberto
    /// + Ganhos + Perdidos = Todos" — que a tela agora mostra em números — deixaria de fechar.
    ///
    /// ⚠️ ESTE PREDICADO ESTAVA ESCRITO POR EXTENSO DENTRO DE `ServicoContatos`. Subiu para cá
    /// quando a tela passou a mostrar a CONTAGEM de cada aba: o número e a lista têm de sair da
    /// mesma pergunta, ou o cliente clica em "Ganhos 2" e vê três linhas. É a mesma lição de
    /// `A_CONTAGEM_DO_MENU_BATE_COM_A_SOMA_DO_QUADRO`, um andar acima.</summary>
    public static Expression<Func<Contato, bool>> ContatoGanho =>
        c => !c.Negociacoes.Any(n => n.Status == StatusNegociacao.Aberta)
          && c.Negociacoes.Any(n => n.Status == StatusNegociacao.Ganha
                                 || n.Status == StatusNegociacao.Concluida);

    /// <summary>PERDEU e nunca comprou — a aba "Perdidos".
    ///
    /// O recorte é o resto: quem tem perda mas também tem compra é CLIENTE, e aparece em
    /// "Ganhos". Perder uma negociação de alguém que já comprou antes não o devolve para cá.</summary>
    public static Expression<Func<Contato, bool>> ContatoPerdido =>
        c => !c.Negociacoes.Any(n => n.Status == StatusNegociacao.Aberta
                                  || n.Status == StatusNegociacao.Ganha
                                  || n.Status == StatusNegociacao.Concluida)
          && c.Negociacoes.Any(n => n.Status == StatusNegociacao.Perdida);

    // ==================================================================== o lugar num funil
    /// <summary>===================== OS ESTADOS QUE OCUPAM O LUGAR NUM FUNIL =====================
    /// `aberta` e `ganha` — a mesma pergunta que `uq_negociacoes_card_por_funil` responde no
    /// banco: "já há um card desta pessoa aqui?". A venda ganha ocupa até o pedido ser concluído.
    ///
    /// ⚠️ ESTAVA ESCRITA EM QUATRO LUGARES: o índice, a projeção da caixa, a abertura de
    /// negociação e a tela do contato no painel. A versão estreita (só `aberta`) já apareceu uma
    /// vez numa delas, e a tela ofereceu um funil que a API recusava com 409. O índice continua
    /// sendo SQL; o resto lê daqui.
    /// ==========================================================================</summary>
    public static Expression<Func<Negociacao, bool>> OcupaOFunil =>
        n => n.Status == StatusNegociacao.Aberta || n.Status == StatusNegociacao.Ganha;

    // ==================================================================== o selo de cada linha
    /// <summary>Tem ao menos um negócio ABERTO — a metade de `ContatoEmAberto` que distingue
    /// "em aberto" de "sem negócio".</summary>
    private static Expression<Func<Contato, bool>> TemNegocioAberto =>
        c => c.Negociacoes.Any(n => n.Status == StatusNegociacao.Aberta);

    /// <summary>===================== O SELO, MONTADO DAS MESMAS PEÇAS DAS ABAS =====================
    /// A situação que a tela escreve ao lado do nome: "em aberto", "venda fechada", "perdido",
    /// "sem negócio".
    ///
    /// ⚠️ ELA MORAVA NO PAINEL, EM DUAS CÓPIAS, e as duas divergiram das abas. A da lista
    /// perguntava `ganhoEm` antes de "tem negócio vivo?", e a Ysia — três negócios abertos e uma
    /// compra antiga — aparecia como "venda fechada" DENTRO da aba "Em aberto". Consertada a da
    /// lista, a da tela do contato ficou com a regra velha (sem uso, por sorte).
    ///
    /// Aqui ela não é reescrita: é MONTADA com `ContatoGanho` e `ContatoPerdido`, as mesmas
    /// expressões que filtram e contam as abas. Mudar uma aba muda o selo junto — não há segunda
    /// cópia para esquecer. `TemNegocioAberto` separa, dentro de "Em aberto", quem tem negócio de
    /// quem ainda não tem.
    /// ==========================================================================</summary>
    public static Expression<Func<Contato, SituacaoContato>> Situacao { get; } = MontarSituacao();

    private static Expression<Func<Contato, SituacaoContato>> MontarSituacao()
    {
        var c = Expression.Parameter(typeof(Contato), "c");

        Expression Corpo(Expression<Func<Contato, bool>> regra) =>
            new TrocaDeParametro(regra.Parameters[0], c).Visit(regra.Body);

        static Expression Valor(SituacaoContato s) => Expression.Constant(s);

        var corpo =
            Expression.Condition(Corpo(TemNegocioAberto), Valor(SituacaoContato.Aberto),
            Expression.Condition(Corpo(ContatoGanho), Valor(SituacaoContato.Ganho),
            Expression.Condition(Corpo(ContatoPerdido), Valor(SituacaoContato.Perdido),
                Valor(SituacaoContato.SemNegocio))));

        return Expression.Lambda<Func<Contato, SituacaoContato>>(corpo, c);
    }

    /// <summary>A situação do contato, para usar DENTRO de uma projeção passada por
    /// <see cref="Projetar{T}"/> — que troca esta chamada pelo corpo de <see cref="Situacao"/>
    /// antes de o EF ver a consulta.
    ///
    /// Fora de `Projetar` ela lança, e é de propósito: o EF avaliaria a chamada no cliente, com o
    /// contato sem as negociações carregadas, e o selo sairia "sem negócio" para todo mundo — em
    /// silêncio.</summary>
    public static SituacaoContato SituacaoDe(Contato contato) =>
        throw new InvalidOperationException(
            "`RegrasNegociacao.SituacaoDe` só vale dentro de `RegrasNegociacao.Projetar`.");

    /// <summary>Prepara uma projeção que usa <see cref="SituacaoDe"/>: cada chamada vira a
    /// expressão da situação, e o banco a calcula na MESMA consulta que traz a linha — sem uma
    /// ida a mais por página.</summary>
    public static Expression<Func<Contato, T>> Projetar<T>(Expression<Func<Contato, T>> projecao) =>
        (Expression<Func<Contato, T>>)new ExpansorDeSituacao().Visit(projecao);

    private sealed class TrocaDeParametro(ParameterExpression de, Expression para) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == de ? para : base.VisitParameter(node);
    }

    private sealed class ExpansorDeSituacao : ExpressionVisitor
    {
        private static readonly System.Reflection.MethodInfo Marcador =
            typeof(RegrasNegociacao).GetMethod(nameof(SituacaoDe))!;

        protected override Expression VisitMethodCall(MethodCallExpression node) =>
            node.Method == Marcador
                ? new TrocaDeParametro(Situacao.Parameters[0], Visit(node.Arguments[0])).Visit(Situacao.Body)
                : base.VisitMethodCall(node);
    }

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

/// <summary>O selo de uma pessoa na lista e na tela do contato. Ver `RegrasNegociacao.Situacao`.
///
/// `Aberto` e `SemNegocio` são as duas metades da aba "Em aberto"; `Ganho` e `Perdido` são as
/// abas com o mesmo nome. Sai da API como `sem_negocio`, `aberto`, `ganho`, `perdido`.</summary>
public enum SituacaoContato
{
    /// <summary>Nenhum negócio vivo, e nunca comprou nem perdeu — o lead que chegou pela caixa.</summary>
    SemNegocio,
    Aberto,
    Ganho,
    Perdido
}
