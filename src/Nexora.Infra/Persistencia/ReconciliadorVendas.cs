using Microsoft.EntityFrameworkCore;

namespace Nexora.Infra.Persistencia;

/// <summary>===================== TODO `ganho_em` PRECISA DE UMA LINHA EM `vendas` (NEG-1) =====================
///
/// O caminho normal — `ServicoContatos.MarcarGanhoAsync` — grava o carimbo e a linha na mesma
/// transacao, e essa e a porta unica.
///
/// Mas existem escritas que passam POR FORA dela: os semeadores carimbam `ganho_em` em lote, com
/// `ExecuteUpdate`, para montar meses de historico de uma vez. Eles nao poderiam chamar o servico
/// (que valida, move de etapa e publica evento por contato), e sem uma linha em `vendas` o tenant
/// de demonstracao abriria com faturamento ZERO — que e exatamente o que a demonstracao existe
/// para nao mostrar.
///
/// Este reconciliador e o mesmo INSERT do backfill da migration `HistoricoVendas`, disponivel para
/// quem escreve em lote. IDEMPOTENTE pelo `NOT EXISTS`: rodar duas vezes nao duplica.
///
/// ⚠️ Isto NAO e uma segunda porta de gravacao de venda. E a reconciliacao de uma escrita que ja
/// aconteceu — e o fato de ela ser necessaria e o preco de `ganho_em` continuar existindo como
/// carimbo. Codigo novo que fecha venda chama o SERVICO.
/// ==========================================================================================</summary>
public static class ReconciliadorVendas
{
    /// <summary>Gera as linhas que faltam e devolve QUANTAS foram geradas — o numero entra no
    /// log do semeador, porque "rodou" e "fez alguma coisa" sao afirmacoes diferentes.</summary>
    public static Task<int> SincronizarAsync(NexoraDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("""
            INSERT INTO vendas (empresa_id, contato_id, valor, fechada_em, status, responsavel_id, etapa_id, criado_em)
            SELECT c.empresa_id,
                   c.id,
                   COALESCE(c.valor, 0.01),
                   c.ganho_em,
                   -- `fechada` e nao `concluida` (NEG-2): o contato esta na coluna Venda AGORA, e
                   -- e essa coluna que a linha reconstroi. Nascer concluida esvaziaria o quadro
                   -- do tenant de demonstracao — o oposto do que ele existe para mostrar.
                   --
                   -- ⚠️ EXPLICITO, e nao por DEFAULT: a coluna nao tem default no banco, de
                   -- proposito (ver a migration `ConclusaoVenda`), justamente para que todo
                   -- INSERT cru precise DIZER em que estado a venda nasce.
                   'fechada',
                   c.responsavel_id,
                   COALESCE(
                       (SELECT e.id FROM etapas_funil e
                         WHERE e.empresa_id = c.empresa_id AND e.e_ganho
                         ORDER BY e.ordem LIMIT 1),
                       (SELECT e.id FROM etapas_funil e
                         WHERE e.empresa_id = c.empresa_id
                         ORDER BY e.ordem DESC LIMIT 1)),
                   c.ganho_em
              FROM contatos c
             WHERE c.ganho_em IS NOT NULL
               AND EXISTS (SELECT 1 FROM etapas_funil e WHERE e.empresa_id = c.empresa_id)
               AND NOT EXISTS (
                       SELECT 1 FROM vendas v
                        WHERE v.contato_id = c.id AND v.fechada_em = c.ganho_em);
            """, ct);
}
