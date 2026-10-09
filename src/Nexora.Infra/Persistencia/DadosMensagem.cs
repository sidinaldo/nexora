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
    /// <summary>A reserva da pesquisa de NPS. Gemeo do `ReservarLembreteAsync` ao lado, com duas
    /// diferencas que importam: a ancora do `ON CONFLICT` e `uq_msg_nps` (por `negociacao_id`), e
    /// o par cravado e `automatica`/`nps`.</summary>
    public async Task<long?> ReservarNpsAsync(Mensagem r, CancellationToken ct)
    {
        var ids = await db.Database.SqlQueryRaw<long>("""
            INSERT INTO mensagens (
                empresa_id, conversa_id, contato_id, conexao_id, instance_name,
                direcao, texto, tipo_midia, negociacao_id, data_disparo,
                origem, tipo_automacao, modelo_id,
                reservado_em, criado_em)
            VALUES (
                {0}, {1}, {2}, {3}, {4},
                'saida'::direcao_mensagem_enum, {5}, 'nenhum'::tipo_midia_enum, {6}, {7},
                -- ⚠️ CRAVADO AQUI PELA MESMA RAZAO DO VIZINHO: este INSERT lista as colunas uma a
                -- uma, e propriedade marcada na entidade NAO chega ao banco por este caminho. O
                -- `tipo_automacao = 'nps'` tambem e o PREDICADO de `uq_msg_nps` — sem ele cravado,
                -- o indice parcial nao pega a linha e o dedupe deixa de existir.
                'automatica'::origem_mensagem_enum, 'nps'::tipo_automacao_enum,
                -- INT-XX: o template, quando a pergunta sai como template (API oficial, janela
                -- fechada). Listado aqui pela mesma razao das duas colunas acima.
                {9},
                {8}, {8})
            ON CONFLICT DO NOTHING
            RETURNING id AS "Value"
            """,
            r.EmpresaId, r.ConversaId, r.ContatoId, r.ConexaoId, r.InstanceName,
            (object?)r.Texto ?? DBNull.Value, r.NegociacaoId!, r.DataDisparo!,
            relogio.GetUtcNow().UtcDateTime, (object?)r.ModeloId ?? DBNull.Value).ToListAsync(ct);

        return ids.Count > 0 ? ids[0] : null;
    }

    /// <summary>`IgnoreQueryFilters` + empresa a mao: roda no job, sem tenant. O
    /// `negociacao_id` preenchido e o que separa a PERGUNTA do agradecimento, que tambem e
    /// `tipo_automacao = 'nps'` mas nasce sem a venda (ver `EnviarAgradecimentoNpsAsync`).</summary>
    public Task<Mensagem?> PerguntaNpsDaVendaAsync(long empresaId, long negociacaoId, CancellationToken ct) =>
        db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.EmpresaId == empresaId
                     && m.NegociacaoId == negociacaoId
                     && m.TipoAutomacao == TipoAutomacao.Nps)
            .OrderBy(m => m.Id)
            .FirstOrDefaultAsync(ct);

    public async Task<long?> ReservarLembreteAsync(Mensagem r, CancellationToken ct)
    {
        var ids = await db.Database.SqlQueryRaw<long>("""
            INSERT INTO mensagens (
                empresa_id, conversa_id, contato_id, conexao_id, instance_name,
                direcao, texto, tipo_midia, lembrete_id, data_disparo,
                origem, tipo_automacao, modelo_id,
                reservado_em, criado_em)
            VALUES (
                {0}, {1}, {2}, {3}, {4},
                'saida'::direcao_mensagem_enum, {5}, 'nenhum'::tipo_midia_enum, {6}, {7},
                -- ⚠️ CRAVADO AQUI, E NAO LIDO DA ENTIDADE (NPS-1). Este INSERT lista as colunas
                -- uma a uma: propriedade nova na entidade `Mensagem` NAO chega ao banco por este
                -- caminho. Marquei `Origem` no `MotorFollowUp` e o teste continuou dizendo
                -- "Humana" — foi assim que este ponto apareceu.
                --
                -- Toda reserva que passa por aqui vem de lembrete, entao o par e constante.
                'automatica'::origem_mensagem_enum, 'lembrete'::tipo_automacao_enum,
                -- INT-XX: o template, quando o lembrete sai como template.
                {9},
                {8}, {8})
            ON CONFLICT DO NOTHING
            RETURNING id AS "Value"
            """,
            r.EmpresaId, r.ConversaId, r.ContatoId, r.ConexaoId, r.InstanceName,
            (object?)r.Texto ?? DBNull.Value, r.LembreteId!, r.DataDisparo!,
            relogio.GetUtcNow().UtcDateTime, (object?)r.ModeloId ?? DBNull.Value).ToListAsync(ct);

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

    /// <summary>Expirada AGORA, com o motivo (INT-XX). Sai da drenagem — que so pega linha sem
    /// `expirada_em` — e a thread a mostra como "nao enviada", dizendo por que.</summary>
    public Task DescartarAsync(long mensagemId, string motivo, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("""
            UPDATE mensagens
               SET expirada_em = {1},
                   erro = {2}
             WHERE id = {0} AND enviada_em IS NULL
            """, [mensagemId, relogio.GetUtcNow().UtcDateTime, motivo], ct);

    public Task TrocarPorModeloAsync(long mensagemId, long modeloId, string texto, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("""
            UPDATE mensagens
               SET modelo_id = {1},
                   texto = {2}
             WHERE id = {0} AND enviada_em IS NULL
            """, [mensagemId, modeloId, texto], ct);

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
