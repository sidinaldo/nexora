namespace Nexora.Core.Entidades;

/// <summary>===================== PARA ONDE UM CARD PODE IR (POS-1) =====================
///
/// Uma pergunta, uma resposta: este negócio pode ir para esta etapa? `null` = pode; qualquer outra
/// coisa é a frase que o cliente vai ler.
///
/// Nasceu porque o quadro parava na venda. A empresa criava "Pós-Venda" e "Entregue" depois da
/// etapa de ganho, e a coluna aceitava exatamente a coisa errada: o card vendido era recusado
/// (<c>MoverAsync</c> só movia `Aberta`) e o card EM ABERTO entrava, porque a etapa não é de ganho.
///
/// ===================== POR QUE NÃO DENTRO DE `RegrasNegociacao` =====================
/// Aquela classe é de VISIBILIDADE e é inteira `Expression` — ela existe para o Postgres avaliar.
/// Esta é decisão em C# sobre quatro escalares, e devolve português. Separadas, a pergunta "isto
/// vira SQL?" se responde pelo nome do arquivo; juntas, alguém tentaria usar uma recusa dentro de
/// um `Where` e descobriria tarde.
/// ==================================================================================
///
/// ===================== AS REGRAS, E DE ONDE CADA UMA VEM =====================
/// Com `og` = ordem da etapa de ganho, `oa` = ordem atual do card, `od` = ordem do destino:
///
///   1. destino é a etapa de ganho  -> RECUSA. A porta única do ganho: venda entra com valor
///      fechado, pela tela de venda. Esta regra já existia, escrita DUAS vezes com duas frases
///      diferentes (`ServicoFunil` e `ServicoContatos`). Agora é uma;
///
///   2. `Perdida`   -> RECUSA, reabra primeiro. Como antes;
///   3. `Concluida` / `Cancelada` -> RECUSA. O pedido acabou, e a etapa dele é o registro de onde
///      fechou. ⚠️ A frase velha dizia "este negócio já foi fechado e não se move mais no quadro",
///      e cobria `Ganha` também — ela TINHA de mudar, porque `Ganha` passa a se mover;
///
///   4. `Aberta` indo para DEPOIS do ganho -> RECUSA. Pós-venda é de quem já vendeu. Hoje isso
///      passa, e é o terceiro defeito que este bloco conserta: dá para pôr um negócio não vendido
///      em "Entregue";
///   5. `Aberta` no resto -> PODE, nos dois sentidos. Soltar na coluna errada é comum, e o
///      vendedor corrige na hora;
///
///   6. `Ganha` trocando de funil -> RECUSA. `og` é sempre do funil de DESTINO, então comparar
///      `oa` com `od` entre dois funis não quer dizer nada — e mover um card vendido para outro
///      funil destrói o registro de onde ele fechou;
///   7. `Ganha` indo para antes do ganho -> RECUSA: não volta para a negociação;
///   8. `Ganha` indo para trás -> RECUSA: só avança;
///   9. `Ganha` no resto (`od >= oa`) -> PODE.
///
/// ⚠️ A ORDEM DAS LINHAS 7 E 8 É DELIBERADA. Arrastar de Pós-Venda para Proposta viola as duas, e
/// a frase que sai deve ser a da 7 — ela diz o MOTIVO ("não volta para a negociação"), não o
/// mecanismo ("só avança").
///
/// ⚠️ `od == oa` É PERMITIDO para `Ganha`: é reordenar dentro da própria coluna, e `MoverAsync` é o
/// único caminho que calcula `OrdemKanban`. Com vinte entregas pendentes o vendedor vai querer
/// ordenar a fila. "Só avança" é sobre ETAPA, não sobre posição dentro dela.
///
/// ⚠️ FUNIL SEM ETAPA DE GANHO (`OrdemDoGanho` nulo) é estado legal: o schema permite, e
/// `MarcarGanhoAsync` nesse caso deixa o card onde está e só troca o status. Ali `Aberta` anda
/// livre e `Ganha` só avança em relação a si mesmo — "antes da venda" não tem significado sem a
/// fronteira, mas "para frente" tem.
/// ==========================================================================
///
/// TUDO 409. Considerei 400 para as regras de direção — é pedido inválido, não visão velha. Mas o
/// cliente já trata 409 recarregando as duas colunas afetadas, e isso é exatamente o conserto certo
/// quando a ordem das etapas mudou em outra aba. Um segundo código não compra nada e arrisca o
/// caminho de recuperação não disparar.
/// ============================================================================</summary>
public static class RegrasDoQuadro
{
    /// <summary>O destino que se quer avaliar.</summary>
    /// <param name="Status">O status atual da negociação.</param>
    /// <param name="OrdemAtual">A ordem da etapa onde o card está, ou `null` quando não é um
    /// movimento e sim uma ENTRADA — a criação de contato com etapa escolhida à mão.</param>
    /// <param name="OrdemDestino">A ordem da etapa de destino.</param>
    /// <param name="DestinoEGanho">O destino é a etapa de ganho do funil.</param>
    /// <param name="OrdemDoGanho">A ordem da etapa de ganho do funil de DESTINO, ou `null` se esse
    /// funil não tem uma.</param>
    /// <param name="TrocaDeFunil">O destino está em outro funil.</param>
    public readonly record struct Destino(
        StatusNegociacao Status,
        short? OrdemAtual,
        short OrdemDestino,
        bool DestinoEGanho,
        short? OrdemDoGanho,
        bool TrocaDeFunil);

    /// <summary>`null` quando pode. A frase sai como está, direto para o cliente.</summary>
    public static string? Recusa(Destino d)
    {
        // 1 — a porta única do ganho. Vale para arrasto e para criação, e é por isso que a frase
        // não cita nenhum dos dois.
        if (d.DestinoEGanho)
            return "A etapa de venda só recebe negociação com valor fechado. Registre a venda.";

        // 2 e 3 — os estados terminais. `Ganha` NÃO está aqui: é o ponto do bloco.
        if (d.Status == StatusNegociacao.Perdida)
            return "Esta negociação está marcada como perdida. Reabra antes de movê-la.";

        if (d.Status is StatusNegociacao.Concluida or StatusNegociacao.Cancelada)
            return "Esta venda já foi concluída. A etapa dela é o registro de onde fechou.";

        if (d.Status == StatusNegociacao.Aberta)
        {
            // 4 — pós-venda é de quem já vendeu.
            if (d.OrdemDoGanho is { } ganho && d.OrdemDestino > ganho)
                return "Esta etapa é de pós-venda: só entra negociação já vendida. "
                     + "Registre a venda antes.";

            return null;   // 5
        }

        if (d.Status == StatusNegociacao.Ganha)
        {
            // 6 — trocar de funil apaga o registro de onde fechou.
            if (d.TrocaDeFunil)
                return "Negociação vendida não troca de funil. Conclua a venda antes.";

            // 7 antes de 8: o motivo vem antes do mecanismo.
            if (d.OrdemDoGanho is { } ganho && d.OrdemDestino < ganho)
                return "Negociação vendida não volta para antes da venda. "
                     + "Ela avança para as etapas de pós-venda.";

            // 8 — `OrdemAtual` nulo é entrada, e entrada não tem "para trás".
            if (d.OrdemAtual is { } atual && d.OrdemDestino < atual)
                return "Negociação vendida só avança. Ela não volta para uma etapa anterior.";

            return null;   // 9
        }

        return null;
    }

    /// <summary>===================== A MESMA REGRA, VISTA DO OUTRO LADO (POS-1) =====================
    ///
    /// "Vendido não volta para antes da venda" também pode ser violado SEM NINGUÉM ARRASTAR NADA —
    /// mexendo nas etapas. Três portas fazem isso, e nenhuma tinha guarda:
    ///
    ///   · reordenar, jogando a etapa de ganho para depois de uma que tem card vendido;
    ///   · mudar QUAL etapa é a de ganho, para uma mais à frente;
    ///   · apagar uma etapa de pós-venda mandando os cards dela para uma etapa de negociação.
    ///
    /// ⚠️ E O ESTRAGO É SILENCIOSO E DIFERIDO, que é o pior par que existe. Os cards continuam
    /// visíveis — o recorte largo cuida disso —, mas passam a estar numa coluna "pré-venda", e aí o
    /// `NOT EXISTS` da `ConclusaoAutomatica` lê "não há etapa de ganho antes de mim" e **conclui
    /// todos na próxima rodada diária**. Um arrasto na tela de Configurações, nada acontece na hora,
    /// e de manhã a coluna de pós-venda inteira sumiu.
    ///
    /// ⚠️ SÓ RECUSA O QUE A MUDANÇA PIORA. Negócio vendido parado numa etapa anterior já existe
    /// (funil sem etapa de ganho, linhas de antes do E4c/2) — recusar por causa dele travaria a
    /// tela de etapas para sempre, por um estado que a pessoa não criou e não tem como consertar
    /// dali. A pergunta é "estava do lado certo e passa para o errado?".
    /// ======================================================================================</summary>
    /// <param name="OrdemAntes">A ordem da etapa onde os cards estão hoje.</param>
    /// <param name="OrdemDepois">A ordem que essa etapa (ou a de destino, num apagar) vai ter.</param>
    /// <param name="Quantos">Quantos negócios vendidos estão nela — entra na mensagem.</param>
    public readonly record struct EtapaComVendido(short OrdemAntes, short OrdemDepois, int Quantos);

    public static string? RecusaMexerNasEtapas(
        short? ordemDoGanhoAntes, short? ordemDoGanhoDepois,
        IEnumerable<EtapaComVendido> etapasComVendido)
    {
        var presos = 0;

        foreach (var e in etapasComVendido)
        {
            var estavaDoLadoCerto = ordemDoGanhoAntes is not { } antes || e.OrdemAntes >= antes;
            var ficaDoLadoCerto = ordemDoGanhoDepois is { } depois && e.OrdemDepois >= depois;

            if (estavaDoLadoCerto && !ficaDoLadoCerto) presos += e.Quantos;
        }

        if (presos == 0) return null;

        // A frase diz o EFEITO, e não a regra. "Isto violaria a ordem das etapas" não ajudaria
        // ninguém a decidir o que fazer; "a conclusão automática encerraria estes pedidos amanhã"
        // explica o estrago e já aponta as duas saídas.
        return presos == 1
            ? "Esta mudança deixaria 1 venda atrás da etapa de venda, e a conclusão "
            + "automática a encerraria na próxima rodada. Conclua ou mova essa venda antes."
            : $"Esta mudança deixaria {presos} vendas atrás da etapa de venda, e a "
            + "conclusão automática as encerraria na próxima rodada. Conclua ou mova essas "
            + "vendas antes.";
    }
}
