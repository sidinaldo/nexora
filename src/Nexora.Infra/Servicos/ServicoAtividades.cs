using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Nexora.Core;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O feed de atividade recente: mensagem recebida, venda fechada, lembrete concluído e
/// contato criado.
///
/// ===================== A VISIBILIDADE É DECIDIDA AQUI =====================
/// Vendedor vê as próprias atividades e as de contato sem dono; gestor e dono veem a empresa.
///
/// O recorte é da API, nunca da tela. Filtrar no cliente significa que a resposta HTTP JÁ TROUXE
/// o que o vendedor não podia ver — quem abrir a aba de rede lê tudo. A tela some com o dado;
/// ela não o protege.
///
/// "Sem dono" entrar junto é a mesma regra do Meu Dia e da caixa: contato sem responsável é de
/// todo mundo. O que o vendedor não pode ver é o que está com OUTRO vendedor.
/// ==========================================================================
///
/// SQL cru: quatro fontes com `UNION ALL`, cursor composto e um `LIMIT` por fonte não têm
/// tradução em EF sem materializar as quatro listas inteiras — que é agregar em memória.</summary>
public class ServicoAtividades(NexoraDbContext db, IContextoEmpresa contexto) : IServicoAtividades
{
    public const int TamanhoMaximo = 50;

    public async Task<PaginaAtividades> ListarAsync(
        DateTime? cursorEm, string? cursorChave, long? responsavelId, int tamanho,
        CancellationToken ct)
    {
        tamanho = Math.Clamp(tamanho, 1, TamanhoMaximo);

        // ===== O RECORTE POR PAPEL =====
        // Para Vendedor o parâmetro é DESCARTADO e o próprio usuário é imposto. Aceitar o valor
        // do cliente aqui seria deixar a autorização na mão de quem monta a requisição.
        var ehVendedor = !string.Equals(contexto.Papel, "dono", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(contexto.Papel, "gestor", StringComparison.OrdinalIgnoreCase);

        var filtroResponsavel = ehVendedor ? contexto.UsuarioId : responsavelId;

        // Pede um a mais do que cabe: se voltar, existe próxima página. Contar o total exigiria
        // um segundo COUNT sobre as quatro fontes, e ninguém precisa do total de um feed.
        var itens = await ConsultarAsync(
            cursorEm, cursorChave, filtroResponsavel, ehVendedor, tamanho + 1, ct);

        var temMais = itens.Count > tamanho;
        if (temMais) itens.RemoveAt(itens.Count - 1);

        return new PaginaAtividades(itens, temMais);
    }

    private async Task<List<Atividade>> ConsultarAsync(
        DateTime? cursorEm, string? cursorChave, long? responsavelId, bool incluirSemDono,
        int limite, CancellationToken ct)
    {
        // O predicado do cursor é o MESMO em todas as fontes. `$2 IS NULL` cobre a primeira
        // página sem exigir um segundo SQL.
        //
        // A forma `quando < c OR (quando = c AND chave < ck)` é a comparação lexicográfica
        // escrita à mão. O primeiro ramo é uma faixa e usa o índice; o segundo só toca as linhas
        // do empate exato de timestamp, que são raras. Um `(a,b) < (x,y)` de linha inteira seria
        // mais curto e o planejador o trata pior aqui, porque `chave` é expressão, não coluna.
        const string cursor = """
            ($2::timestamptz IS NULL
             OR {0} < $2
             OR ({0} = $2 AND {1} < $3))
            """;

        // O filtro de responsável muda por fonte (a coluna vive em tabelas diferentes), então
        // cada bloco recebe o seu. `$4 IS NULL` = sem filtro (dono/gestor sem escolher ninguém).
        string PorResponsavel(string coluna) => incluirSemDono
            // Vendedor: as minhas E as sem dono. Contato sem responsável é de todo mundo.
            ? $"($4::bigint IS NULL OR {coluna} = $4 OR {coluna} IS NULL)"
            : $"($4::bigint IS NULL OR {coluna} = $4)";

        var sql = $"""
            WITH
            msg AS (
                -- Casts explícitos: numa UNION ALL os tipos das colunas saem do PRIMEIRO ramo,
                -- e literal sem cast entra como `unknown`. Deixar a resolução por conta do
                -- planejador funciona até o dia em que um ramo devolve NULL puro na coluna e o
                -- erro sai como "could not determine data type", longe daqui.
                SELECT 'mensagem'::text AS tipo, ('mensagem:' || m.id)::text AS chave,
                       m.criado_em AS quando,
                       c.id AS contato_id, c.nome::text AS contato_nome,
                       ('Mensagem de ' || c.nome)::text AS titulo,
                       LEFT(COALESCE(m.texto, ''), 140)::text AS detalhe,
                       NULL::numeric AS valor,
                       cv.responsavel_id AS responsavel_id
                  FROM mensagens m
                  JOIN conversas cv ON cv.id = m.conversa_id AND cv.empresa_id = m.empresa_id
                  JOIN contatos  c  ON c.id  = m.contato_id  AND c.empresa_id  = m.empresa_id
                 WHERE m.empresa_id = $1
                   AND m.direcao = 'entrada'
                   AND {string.Format(cursor, "m.criado_em", "'mensagem:' || m.id")}
                   AND {PorResponsavel("cv.responsavel_id")}
                 ORDER BY m.criado_em DESC
                 LIMIT $5
            ),
            venda AS (
                SELECT 'venda'::text, ('venda:' || c.id)::text, c.ganho_em,
                       c.id, c.nome::text,
                       ('Venda fechada com ' || c.nome)::text,
                       NULL::text,
                       c.valor,
                       c.responsavel_id
                  FROM contatos c
                 WHERE c.empresa_id = $1
                   AND c.ganho_em IS NOT NULL
                   AND {string.Format(cursor, "c.ganho_em", "'venda:' || c.id")}
                   AND {PorResponsavel("c.responsavel_id")}
                 ORDER BY c.ganho_em DESC
                 LIMIT $5
            ),
            lembrete AS (
                SELECT 'lembrete'::text, ('lembrete:' || l.id)::text, l.concluido_em,
                       c.id, c.nome::text,
                       ('Follow-up concluído: ' || l.titulo)::text,
                       NULL::text,
                       NULL::numeric,
                       l.responsavel_id
                  FROM lembretes l
                  JOIN contatos c ON c.id = l.contato_id AND c.empresa_id = l.empresa_id
                 WHERE l.empresa_id = $1
                   AND l.concluido_em IS NOT NULL
                   AND {string.Format(cursor, "l.concluido_em", "'lembrete:' || l.id")}
                   AND {PorResponsavel("l.responsavel_id")}
                 ORDER BY l.concluido_em DESC
                 LIMIT $5
            ),
            novo AS (
                SELECT 'contato'::text, ('contato:' || c.id)::text, c.criado_em,
                       c.id, c.nome::text,
                       ('Novo contato: ' || c.nome)::text,
                       NULL::text,
                       NULL::numeric,
                       c.responsavel_id
                  FROM contatos c
                 WHERE c.empresa_id = $1
                   AND {string.Format(cursor, "c.criado_em", "'contato:' || c.id")}
                   AND {PorResponsavel("c.responsavel_id")}
                 ORDER BY c.criado_em DESC
                 LIMIT $5
            ),
            tudo AS (
                SELECT * FROM msg
                UNION ALL SELECT * FROM venda
                UNION ALL SELECT * FROM lembrete
                UNION ALL SELECT * FROM novo
            )
            SELECT t.tipo, t.chave, t.quando, t.contato_id, t.contato_nome,
                   t.titulo, t.detalhe, t.valor, t.responsavel_id, u.nome AS responsavel_nome
              FROM tudo t
              LEFT JOIN usuarios u ON u.id = t.responsavel_id AND u.empresa_id = $1
             ORDER BY t.quando DESC, t.chave DESC
             LIMIT $5
            """;

        var conexao = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conexao.State != System.Data.ConnectionState.Open)
            await conexao.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conexao);
        cmd.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();

        cmd.Parameters.Add(new() { Value = contexto.EmpresaId });                  // $1
        cmd.Parameters.Add(new() { Value = (object?)cursorEm ?? DBNull.Value });   // $2
        cmd.Parameters.Add(new() { Value = (object?)cursorChave ?? DBNull.Value });// $3
        cmd.Parameters.Add(new() { Value = (object?)responsavelId ?? DBNull.Value });// $4
        cmd.Parameters.Add(new() { Value = limite });                              // $5

        var itens = new List<Atividade>();
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            itens.Add(new Atividade(
                leitor.GetString(0),
                leitor.GetString(1),
                leitor.GetDateTime(2),
                leitor.GetInt64(3),
                leitor.GetString(4),
                leitor.GetString(5),
                await leitor.IsDBNullAsync(6, ct) ? null : leitor.GetString(6),
                await leitor.IsDBNullAsync(7, ct) ? null : leitor.GetDecimal(7),
                await leitor.IsDBNullAsync(8, ct) ? null : leitor.GetInt64(8),
                await leitor.IsDBNullAsync(9, ct) ? null : leitor.GetString(9)));
        }

        return itens;
    }
}
