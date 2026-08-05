using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Whatsapp;

namespace Nexora.Infra.Persistencia;

/// <summary>O acesso a dados por tras do EnviadorMensagem.
///
/// IgnoreQueryFilters em tudo: o motor de lembretes roda como JOB, sem tenant no contexto. Sem
/// isso o filtro global compara EmpresaId com 0 e a consulta volta vazia — em silencio. O
/// isolamento aqui e EXPLICITO, pelo empresaId no Where.</summary>
public class DadosMensagem(NexoraDbContext db, TimeProvider relogio) : IDadosMensagem
{
    /// <summary>INSERT ... ON CONFLICT DO NOTHING RETURNING id, contra uq_msg_lembrete.
    ///
    /// SQL CRU DE PROPOSITO: o EF nao expressa ON CONFLICT. A alternativa — SaveChanges +
    /// capturar DbUpdateException por linha — envenena o ChangeTracker e usa excecao como fluxo
    /// de controle numa operacao que barra de proposito na maior parte das vezes.
    ///
    /// Volta vazio quando este lembrete JA gerou mensagem: um crash entre "insere mensagem" e
    /// "marca lembrete concluido", ou duas instancias do motor, reenviariam sem isso. O banco e
    /// o arbitro, nao a aplicacao.</summary>
    public async Task<long?> ReservarLembreteAsync(Mensagem r, CancellationToken ct)
    {
        var ids = await db.Database.SqlQueryRaw<long>("""
            INSERT INTO mensagens (
                empresa_id, conversa_id, contato_id, conexao_id, instance_name,
                direcao, texto, tipo_midia, lembrete_id, data_disparo,
                reservado_em, criado_em)
            VALUES (
                {0}, {1}, {2}, {3}, {4},
                'saida'::direcao_mensagem_enum, {5}, 'nenhum'::tipo_midia_enum, {6}, {7},
                {8}, {8})
            ON CONFLICT DO NOTHING
            RETURNING id AS "Value"
            """,
            r.EmpresaId, r.ConversaId, r.ContatoId, r.ConexaoId, r.InstanceName,
            (object?)r.Texto ?? DBNull.Value, r.LembreteId!, r.DataDisparo!,
            relogio.GetUtcNow().UtcDateTime).ToListAsync(ct);

        return ids.Count > 0 ? ids[0] : null;
    }

    /// <summary>Mensagem MANUAL: lembrete_id NULL de proposito, entao nao entra em invariante
    /// nenhuma. Dentro de uma conversa viva o vendedor responde a vontade.</summary>
    public async Task<long> GravarManualAsync(Mensagem mensagem, CancellationToken ct)
    {
        db.Mensagens.Add(mensagem);
        await db.SaveChangesAsync(ct);
        return mensagem.Id;
    }

    /// <summary>NULLIF: a Evolution pode responder 2xx SEM key.id, e o cliente devolve "".
    /// Duas strings vazias colidiriam no indice unico uq_msg_wa_id — NULL nao colide.
    ///
    /// (No Nexora o indice tambem exclui '' no predicado, entao ha duas defesas. O NULLIF fica
    /// porque e ele que mantem a coluna semanticamente honesta: "nao sabemos o id", nao "o id e
    /// string vazia".)</summary>
    public Task ConfirmarEnvioAsync(long mensagemId, string waMessageId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("""
            UPDATE mensagens
               SET wa_message_id = NULLIF({1}, ''),
                   enviada_em = {2},
                   tentativas = tentativas + 1,
                   erro = NULL
             WHERE id = {0}
            """, [mensagemId, waMessageId, relogio.GetUtcNow().UtcDateTime], ct);

    /// <summary>A linha FICA, com o erro e o contador. Apagar liberaria a invariante — e um POST
    /// que na verdade chegou (mas deu timeout) viraria mensagem duplicada no reenvio.</summary>
    public Task RegistrarFalhaAsync(long mensagemId, string erro, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("""
            UPDATE mensagens
               SET erro = {1}, tentativas = tentativas + 1
             WHERE id = {0}
            """, [mensagemId, erro.Length <= 500 ? erro : erro[..500]], ct);

    public async Task<IReadOnlyList<Mensagem>> PendentesAsync(
        long empresaId, DateOnly desde, CancellationToken ct) =>
        await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.EmpresaId == empresaId
                     && m.Direcao == DirecaoMensagem.Saida
                     && m.LembreteId != null       // so o automatico; manual nao se reenvia
                     && m.EnviadaEm == null        // nunca despachada: falhou OU foi adiada
                     && m.ExpiradaEm == null       // e ainda nao desistimos dela
                     && m.DataDisparo >= desde)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

    /// <summary>Marca as reservas que passaram da janela de reenvio.
    ///
    /// No Recupera elas simplesmente saem do alcance da varredura e somem do radar — o alerta
    /// conta pendentes sem separar "vai ser tentada" de "nunca mais sera". Aqui a linha ganha
    /// estado terminal e vira um numero proprio no endpoint de saude.</summary>
    public Task<int> ExpirarVencidasAsync(long empresaId, DateOnly limite, CancellationToken ct) =>
        db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.EmpresaId == empresaId
                     && m.Direcao == DirecaoMensagem.Saida
                     && m.LembreteId != null
                     && m.EnviadaEm == null
                     && m.ExpiradaEm == null
                     && m.DataDisparo < limite)
            .ExecuteUpdateAsync(s => s.SetProperty(
                m => m.ExpiradaEm, relogio.GetUtcNow().UtcDateTime), ct);

    /// <summary>`IgnoreQueryFilters` + filtro explícito: o envio roda como JOB, sem tenant no
    /// contexto. Sem isso a consulta compara EmpresaId com 0, devolve `false`, e a barreira que
    /// impede o tenant de demonstração de mandar mensagem some — em silêncio, que é o pior modo
    /// de falha possível para esta checagem em particular.</summary>
    public Task<bool> EhDemonstracaoAsync(long empresaId, CancellationToken ct) =>
        db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(e => e.Id == empresaId && e.Demonstracao, ct);
}
