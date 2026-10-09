using Microsoft.EntityFrameworkCore;

namespace Nexora.Infra.Persistencia;

/// <summary>Recalcula `conversas.ultima_entrada_em` a partir das mensagens (INT-XX).
///
/// Para quem escreve conversa DIRETO no banco — os geradores de dados de demonstracao. Eles montam
/// a conversa a mao, e sem isto a janela do WhatsApp apareceria fechada em todas, inclusive na
/// empresa de demonstracao que vai para a frente do cliente.
///
/// A regra e a da `RecepcaoMensagem`: a ultima entrada PELA CONEXAO DA CONVERSA. A migracao
/// `CanalWhatsapp` tem a mesma consulta congelada, para o banco inteiro.</summary>
public static class UltimaEntradaDoHistorico
{
    public static Task RecalcularAsync(NexoraDbContext db, long empresaId, CancellationToken ct)
    {
        return db.Database.ExecuteSqlRawAsync("""
            UPDATE conversas c
               SET ultima_entrada_em = s.ultima
              FROM (SELECT m.conversa_id, max(m.recebida_em) AS ultima
                      FROM mensagens m
                      JOIN conversas c2 ON c2.id = m.conversa_id AND c2.conexao_id = m.conexao_id
                     WHERE m.empresa_id = {0} AND m.direcao = 'entrada' AND m.recebida_em IS NOT NULL
                     GROUP BY m.conversa_id) s
             WHERE c.id = s.conversa_id
            """, [empresaId], ct);
    }
}
