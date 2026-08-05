using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.FollowUp;

namespace Nexora.Infra.Persistencia;

/// <summary>O acesso a dados da rodada de follow-up.
///
/// IgnoreQueryFilters em TUDO: a rodada roda como JOB, sem tenant no contexto. Sem isso o filtro
/// global compara EmpresaId com 0 e a rodada não vê empresa nenhuma — em silêncio. O isolamento
/// aqui é EXPLÍCITO, pelo empresaId no Where.</summary>
public class DadosFollowUp(NexoraDbContext db, TimeProvider relogio) : IDadosFollowUp
{
    /// <summary>As empresas que entram na rodada.
    ///
    /// ===================== `!e.Demonstracao` É BARREIRA DE SEGURANÇA =====================
    /// Tenant de demonstração tem contato com telefone e conversa parada — exatamente o que a
    /// regra de elegibilidade procura. Se ele entrasse aqui e a empresa estivesse pareada a uma
    /// instância real da Evolution, a rodada mandaria follow-up automático para os números
    /// semeados. É mensagem de WhatsApp para gente de verdade, em nome de uma empresa que ela
    /// não conhece.
    ///
    /// O corte é NA FONTE, e não dentro do laço do motor: aqui ele vale para tudo que a rodada
    /// faz com a empresa — gerar lembrete, drenar pendente, expirar reserva. Uma checagem lá
    /// dentro teria que ser repetida em cada ramo, e bastaria um esquecer.
    /// ===================================================================================</summary>
    public async Task<IReadOnlyList<Empresa>> EmpresasAtivasAsync(CancellationToken ct) =>
        await db.Empresas.IgnoreQueryFilters()
            .Where(e => e.Ativo && !e.Demonstracao)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

    public async Task<(long Id, string InstanceName)?> ConexaoAsync(long empresaId, CancellationToken ct)
    {
        var c = await db.Conexoes.IgnoreQueryFilters()
            .Where(x => x.EmpresaId == empresaId)
            .Select(x => new { x.Id, x.InstanceName })
            .FirstOrDefaultAsync(ct);

        return c is null ? null : (c.Id, c.InstanceName);
    }

    public async Task<HashSet<DateOnly>> FeriadosAsync(
        long empresaId, DateOnly de, DateOnly ate, CancellationToken ct)
    {
        // Globais (empresa_id NULL) valem para todos; manuais só para o próprio tenant. E os
        // globais que ESTA empresa dispensou saem fora: se ela atende no Corpus Christi, o
        // follow-up dela não pode deslizar por causa dele.
        var datas = await db.Feriados.IgnoreQueryFilters()
            .Where(f => f.Data >= de && f.Data <= ate
                     && (f.EmpresaId == null || f.EmpresaId == empresaId)
                     && !db.FeriadosIgnorados.IgnoreQueryFilters()
                            .Any(i => i.FeriadoId == f.Id && i.EmpresaId == empresaId))
            .Select(f => f.Data)
            .ToListAsync(ct);

        return [.. datas];
    }

    /// <summary>=========== A ELEGIBILIDADE, TODA NO SQL ===========
    ///
    /// Cinco condições, e cada uma tem uma razão:
    ///
    /// 1. conversa ABERTA — resolvida não precisa de follow-up;
    /// 2. última mensagem foi de SAÍDA — a bola está com o cliente. Se fosse de entrada, ELE
    ///    está esperando resposta, e isso é semáforo, não follow-up;
    /// 3. parada há N dias — o `limite` chega pronto da aplicação, comparado por desigualdade
    ///    CONTRA a coluna. Nunca `CURRENT_DATE - ultima_mensagem_em`: função sobre a coluna
    ///    descarta o índice e a varredura vira seq scan;
    /// 4. contato NÃO está em etapa terminal — ganho ou perdido não se persegue;
    /// 5. NÃO existe lembrete pendente para o contato — senão o vendedor recebe a mesma tarefa
    ///    todo dia até fazer.</summary>
    public async Task<IReadOnlyList<ConversaInativa>> ConversasInativasAsync(
        long empresaId, DateTime limite, CancellationToken ct) =>
        await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EmpresaId == empresaId
                     && c.Status == StatusConversa.Aberta
                     && c.UltimaMensagemDirecao == DirecaoMensagem.Saida
                     && c.UltimaMensagemEm <= limite
                     && c.Contato.GanhoEm == null
                     && c.Contato.PerdidoEm == null
                     && c.Contato.AnonimizadoEm == null
                     && !db.Lembretes.IgnoreQueryFilters().Any(
                            l => l.ContatoId == c.ContatoId && l.Status == StatusLembrete.Pendente))
            .OrderBy(c => c.UltimaMensagemEm)
            .Select(c => new ConversaInativa(
                c.Id, c.ContatoId, c.Contato.Nome, c.Contato.Telefone,
                c.ConexaoId, c.Conexao.InstanceName, c.ResponsavelId, c.UltimaMensagemEm))
            .ToListAsync(ct);

    /// <summary>INSERT ... ON CONFLICT DO NOTHING contra uq_lembrete_teto_diario.
    ///
    /// SQL cru pelo mesmo motivo de sempre: o EF não expressa ON CONFLICT, e capturar
    /// DbUpdateException por linha envenenaria o ChangeTracker numa operação que barra de
    /// propósito. NULL de volta = o teto barrou, e isso é o sistema funcionando.</summary>
    public async Task<long?> CriarLembreteAutomaticoAsync(
        long empresaId, long contatoId, long conversaId, long? responsavelId,
        DateOnly dataAlvo, string titulo, string texto, CancellationToken ct)
    {
        var ids = await db.Database.SqlQueryRaw<long>("""
            INSERT INTO lembretes (
                empresa_id, contato_id, conversa_id, origem, status,
                data_alvo, titulo, envia_mensagem, texto_mensagem,
                responsavel_id, criado_em, atualizado_em)
            VALUES (
                {0}, {1}, {2}, 'automatico'::origem_lembrete_enum, 'pendente'::status_lembrete_enum,
                {3}, {4}, true, {5},
                {6}, {7}, {7})
            ON CONFLICT DO NOTHING
            RETURNING id AS "Value"
            """,
            empresaId, contatoId, conversaId, dataAlvo, titulo, texto,
            (object?)responsavelId ?? DBNull.Value,
            relogio.GetUtcNow().UtcDateTime).ToListAsync(ct);

        return ids.Count > 0 ? ids[0] : null;
    }

    /// <summary>`data_alvo &lt;= hoje`, não `= hoje`: com igualdade estrita, um dia de
    /// indisponibilidade perderia o lembrete para sempre. `&lt;=` continua usando o índice
    /// (range scan) — o que descartaria o índice seria função SOBRE a coluna.</summary>
    public async Task<IReadOnlyList<LembreteParaDisparar>> LembretesADispararAsync(
        long empresaId, DateOnly hoje, CancellationToken ct) =>
        await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.EmpresaId == empresaId
                     && l.Status == StatusLembrete.Pendente
                     && l.EnviaMensagem
                     && l.DataAlvo <= hoje
                     && l.ConversaId != null)
            .OrderBy(l => l.DataAlvo).ThenBy(l => l.Id)
            .Select(l => new LembreteParaDisparar(
                l.Id, l.ContatoId, l.ConversaId, l.Contato.Telefone, l.TextoMensagem!,
                l.Conversa!.ConexaoId, l.Conversa.Conexao.InstanceName))
            .ToListAsync(ct);

    public Task ConcluirLembreteAsync(long lembreteId, CancellationToken ct) =>
        db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.Id == lembreteId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, StatusLembrete.Concluido)
                .SetProperty(l => l.ConcluidoEm, relogio.GetUtcNow().UtcDateTime), ct);

    public Task<string?> TelefoneDoContatoAsync(long contatoId, CancellationToken ct) =>
        db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == contatoId)
            .Select(c => c.Telefone)
            .FirstOrDefaultAsync(ct)!;
}
