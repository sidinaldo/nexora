using Microsoft.EntityFrameworkCore;

namespace Nexora.Infra.Persistencia;

/// <summary>===================== O FIM DO CICLO (NEG-3) =====================
///
/// Concluir a ULTIMA venda em aberto de um contato devolve a conversa dele para a fila:
/// `responsavel_id` e `atribuido_em` voltam a nulo, e o canal do ciclo e apagado.
///
/// O problema que isto resolve: cliente compra, some por tres semanas, volta. A conversa
/// continuava no nome do vendedor de antes — a mensagem nova nao caia em "Nao atribuidas", e
/// quem estava disponivel simplesmente nao a via. Concluir e o marco certo para soltar, porque
/// e o unico momento em que o sistema sabe que o pedido acabou.
///
/// ⚠️ `status` NAO E TOCADO. Liberar responsavel e resolver conversa sao coisas diferentes:
/// resolver e decisao do atendente, e confundir as duas faria a conversa sumir da caixa de quem
/// ainda precisa dela — o vendedor descobriria pelo cliente reclamando.
///
/// ⚠️ SO LIBERA SEM VENDA EM ABERTO. Pedido entregue + pedido a caminho = atendimento em
/// andamento, e o dono continua. E o que o `NOT EXISTS` abaixo garante.
///
/// UM LUGAR SO, e nao tres: a venda conclui por tres portas — o botao (`ServicoVendas`), o
/// prazo zero do balcao (`ServicoContatos.MarcarGanhoAsync`) e a rodada diaria
/// (`ConclusaoAutomatica`). Tres copias desta regra divergiriam no dia em que uma delas mudasse,
/// e as duas automaticas sao justamente as que ninguem olha.
///
/// SQL CRU e SEM filtro de tenant, de proposito: a rodada diaria varre todas as empresas de uma
/// vez. O recorte vem da ORIGEM dos ids — quem chama pelo painel os leu de consulta ja filtrada,
/// e o `empresa_id` no join impede que uma conversa de outra empresa entre pela porta dos fundos.
/// ==================================================================================</summary>
public static class LiberacaoDeCiclo
{
    /// <summary>Devolve quantas conversas foram liberadas.</summary>
    public static Task<int> ExecutarAsync(
        NexoraDbContext db, IReadOnlyList<long> contatoIds, DateTime agora, CancellationToken ct)
    {
        if (contatoIds.Count == 0) return Task.FromResult(0);

        // `= ANY({0})` e nao `IN (...)`: um array parametrizado gera UM plano, valha a lista uma
        // linha ou trezentas. `IN` com lista variavel produz um plano novo a cada tamanho e enche
        // o cache do Postgres de entradas descartaveis.
        //
        // O `(responsavel_id IS NOT NULL OR canal_ciclo_id IS NOT NULL)` nao e otimizacao: sem
        // ele, `atualizado_em` seria reescrito em conversa que nao mudou nada — e a ordenacao da
        // caixa de entrada usa essa coluna.
        const string sql = """
            UPDATE conversas c
               SET responsavel_id = NULL,
                   atribuido_em   = NULL,
                   canal_ciclo_id = NULL,
                   atualizado_em  = {1}
             WHERE c.contato_id = ANY({0})
               AND (c.responsavel_id IS NOT NULL OR c.canal_ciclo_id IS NOT NULL)
               AND NOT EXISTS (
                     SELECT 1
                       FROM vendas v
                      WHERE v.contato_id = c.contato_id
                        AND v.empresa_id = c.empresa_id
                        AND v.status = 'fechada')
            """;

        return db.Database.ExecuteSqlRawAsync(sql, [contatoIds.ToArray(), agora], ct);
    }
}
