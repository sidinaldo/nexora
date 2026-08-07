using Microsoft.EntityFrameworkCore;

namespace Nexora.Infra.Persistencia;

/// <summary>===================== A CONCLUSAO AUTOMATICA (NEG-2) =====================
///
/// O botao "concluir" sozinho nao resolve o bloco. Vendedor nao gosta de tarefa administrativa:
/// em tres meses a coluna volta a acumular, e o problema que o NEG-2 existiu para resolver
/// volta inteiro. O prazo por empresa e o que mantem a coluna limpa sem ninguem lembrar dela.
///
/// UM UPDATE, sem tenant, com o prazo de CADA empresa vindo do join. Varrer empresa a empresa
/// seria N consultas para chegar ao mesmo conjunto — e o `dias_para_concluir_venda` e
/// justamente por empresa, entao ele PRECISA estar no predicado, nao no laco.
///
/// `concluida_por = NULL` e `ator = 'Sistema'`: ninguem clicou. Carimbar um usuario aqui
/// produziria AUTORIA FALSA — a mesma regra do `AtorAuditoria`.
///
/// SQL CRU e nao LINQ: o predicado e `fechada_em &lt; agora - (coluna * interval '1 day')`, com a
/// coluna do OUTRO lado do join. E a trilha sai na mesma ida ao banco, por CTE — o
/// `ExecuteUpdate` do EF nao passa pelo interceptor de auditoria, entao gravar os eventos
/// depois seria uma segunda transacao e uma janela em que a venda esta concluida sem registro.
/// ============================================================================</summary>
public static class ConclusaoAutomatica
{
    /// <summary>Devolve quantas foram concluidas.</summary>
    public static Task<int> ExecutarAsync(
        NexoraDbContext db, TimeProvider relogio, CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;

        // `dias = 0` cai aqui tambem, e de proposito: com zero o predicado vira
        // `fechada_em < agora`, e a rodada vira a rede de seguranca do caminho imediato do
        // `MarcarGanhoAsync` — se ele falhar por qualquer motivo, a venda nao fica presa.
        //
        // `entidade`/`acao`/`ator` sao TEXTO na trilha (ver o mapeamento de `Auditoria`), e o
        // valor gravado e o nome do membro em C#: 'Venda', 'Concluiu', 'Sistema'.
        //
        // ⚠️ NADA de `'{}'` literal aqui: `ExecuteSqlRaw` interpreta chaves como placeholder de
        // formato e estoura FormatException (custou um diagnostico no AUD-1).
        const string sql = """
            WITH concluidas AS (
                UPDATE vendas v
                   SET status = 'concluida',
                       concluida_em = {0},
                       concluida_por = NULL
                  FROM empresas e
                 WHERE e.id = v.empresa_id
                   AND v.status = 'fechada'
                   AND v.fechada_em < {0} - (e.dias_para_concluir_venda * interval '1 day')
                RETURNING v.id, v.empresa_id
            )
            INSERT INTO auditoria
                (empresa_id, entidade, entidade_id, acao, alteracoes, usuario_id, ator, quando)
            SELECT empresa_id, 'Venda', id, 'Concluiu',
                   jsonb_build_object('automatico', true), NULL, 'Sistema', {0}
              FROM concluidas
            """;

        // O retorno do comando e a contagem do INSERT, que e exatamente quantas linhas o UPDATE
        // afetou — a CTE alimenta um do outro.
        return db.Database.ExecuteSqlRawAsync(sql, [agora], ct);
    }
}
