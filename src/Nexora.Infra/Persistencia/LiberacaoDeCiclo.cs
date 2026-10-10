using Microsoft.EntityFrameworkCore;

namespace Nexora.Infra.Persistencia;

/// <summary>===================== O FIM DO CICLO (NEG-3, BUG-XX) =====================
///
/// Concluir a ULTIMA venda em aberto de um contato fecha o CICLO dele: o canal do ciclo (a
/// campanha que trouxe a pessoa desta vez) e apagado das conversas. Sem isso, a proxima compra,
/// meses depois, seria creditada a campanha antiga.
///
/// ⚠️ A CONVERSA NAO E SOLTA, e era (BUG-XX, decisao do dono). A versao anterior tambem zerava o
/// responsavel de TODAS as conversas do contato — em todos os numeros — e o do contato. Uma
/// conversa nao e um negocio: concluir a venda de um card soltava a conversa da Ana, que negociava
/// outro card em Pos-venda, e a do Bruno, que atendia o mesmo cliente pelo outro numero. Agora
/// concluir fecha so o card; quem quiser largar a conversa usa "Liberar".
///
/// ⚠️ `status` NAO E TOCADO: resolver a conversa e decisao do atendente.
///
/// ⚠️ SO FECHA SEM VENDA EM ABERTO. Pedido entregue + pedido a caminho = ciclo em andamento. E o
/// que o `NOT EXISTS` abaixo garante.
///
/// UM LUGAR SO, e nao tres: a venda conclui por tres portas — o botao (`ServicoVendas`), o prazo
/// zero do balcao (`ServicoContatos.MarcarGanhoAsync`) e a rodada diaria (`ConclusaoAutomatica`).
///
/// SQL CRU e SEM filtro de tenant, de proposito: a rodada diaria varre todas as empresas de uma
/// vez. O recorte vem da ORIGEM dos ids, e o `empresa_id` no join impede que uma conversa de outra
/// empresa entre pela porta dos fundos.
/// ==================================================================================</summary>
public static class LiberacaoDeCiclo
{
    /// <summary>Devolve quantas conversas tiveram o canal do ciclo apagado.</summary>
    public static Task<int> ExecutarAsync(
        NexoraDbContext db, IReadOnlyList<long> contatoIds, DateTime agora, CancellationToken ct)
    {
        if (contatoIds.Count == 0) return Task.FromResult(0);

        // `= ANY({0})` e nao `IN (...)`: um array parametrizado gera UM plano, valha a lista uma
        // linha ou trezentas.
        //
        // O `canal_ciclo_id IS NOT NULL` nao e otimizacao: sem ele, `atualizado_em` seria reescrito
        // em conversa que nao mudou nada — e a ordenacao da caixa de entrada usa essa coluna.
        const string sql = """
            UPDATE conversas c
               SET canal_ciclo_id = NULL,
                   atualizado_em  = {1}
             WHERE c.contato_id = ANY({0})
               AND c.canal_ciclo_id IS NOT NULL
               AND NOT EXISTS (
                     SELECT 1
                       FROM negociacoes n
                      WHERE n.contato_id = c.contato_id
                        AND n.empresa_id = c.empresa_id
                        AND n.status = 'ganha')
            """;

        return db.Database.ExecuteSqlRawAsync(sql, [contatoIds.ToArray(), agora], ct);
    }
}
