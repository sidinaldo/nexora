using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Nexora.Core;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A evolução no período: leads, vendas, faturamento e tempo médio de resposta.
///
/// ===================== TUDO NO SQL, UMA IDA SÓ =====================
/// Quatro séries, uma consulta. Série temporal é onde agregar em memória dói de verdade: a
/// empresa com um ano de uso tem centenas de milhares de mensagens, e trazê-las para contar no
/// C# é a diferença entre 40ms e um timeout.
/// ===================================================================
///
/// ===================== O FILTRO PRESERVA O ÍNDICE =====================
/// Os cortes são SEMPRE `coluna >= @inicio AND coluna < @fim`, com os limites calculados no C#
/// a partir do fuso de negócio e passados como PARÂMETRO.
///
/// `WHERE date_trunc('month', criado_em) = @mes` daria o mesmo resultado e descartaria o índice:
/// função sobre a coluna força o planejador a calcular a expressão linha a linha, e
/// ix_contatos_criado deixa de servir. `date_trunc` aparece só no SELECT e no GROUP BY, onde é
/// aplicado sobre o conjunto JÁ recortado.
/// ======================================================================
///
/// SQL cru em vez de LINQ: `generate_series`, `LEFT JOIN` sobre CTE e função de janela não têm
/// tradução em EF, e escrever isso em LINQ significaria trazer linha para a memória.</summary>
public class ServicoSerie(NexoraDbContext db, IContextoEmpresa contexto) : IServicoSerie
{
    /// <summary>O que a consulta precisa saber da empresa. Tipo nomeado em vez de anônimo porque
    /// ele atravessa a fronteira de método — anônimo obrigaria a `dynamic`, que troca erro de
    /// compilação por erro em tempo de execução num caminho que roda uma vez por requisição.</summary>
    private sealed record Contexto(
        string? FusoHorario, short JanelaHoraInicio, short JanelaHoraFim, short JanelaDiasSemana);

    /// <summary>Margem de varredura DEPOIS do fim do período, só para o tempo de resposta.
    ///
    /// Sem ela, a mensagem que chega 23h50 do último dia e é respondida 00h10 do dia seguinte
    /// entraria como "sem resposta" — o corte da consulta inventaria um problema de atendimento
    /// que não existiu. Dois dias cobrem folgadamente uma virada de fim de semana.</summary>
    private static readonly TimeSpan MargemResposta = TimeSpan.FromDays(2);

    public async Task<SerieTemporalDto> ObterAsync(
        DateOnly de, DateOnly ate, AgrupamentoSerie agrupamento, CancellationToken ct)
    {
        if (ate < de) throw new RegraDeNegocioException("A data final não pode ser antes da inicial.");

        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new Contexto(
                e.FusoHorario, e.JanelaHoraInicio, e.JanelaHoraFim, e.JanelaDiasSemana))
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Empresa não encontrada.");

        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);

        // O usuário pede "até 31/08" pensando no dia inteiro. O SQL usa corte EXCLUSIVO — é o
        // que mantém `< @fim` em vez de `<= @fim`, e `<=` sobre timestamp perderia tudo que
        // acontecesse depois de 00h00 do último dia.
        var inicioLocal = de.ToDateTime(TimeOnly.MinValue);
        var fimLocal = ate.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var inicioUtc = TimeZoneInfo.ConvertTimeToUtc(inicioLocal, fuso);
        var fimUtc = TimeZoneInfo.ConvertTimeToUtc(fimLocal, fuso);

        // Feriados do período (mais a margem), já descontando os que a empresa dispensou: para
        // ela aquele dia foi útil, e contá-lo como fechado inflaria o tempo de resposta.
        var feriados = await db.Feriados.AsNoTracking()
            .Where(f => f.Data >= de && f.Data <= ate.AddDays(MargemResposta.Days)
                     && !db.FeriadosIgnorados.Any(i => i.FeriadoId == f.Id))
            .Select(f => f.Data)
            .ToArrayAsync(ct);

        var (unidade, passo) = Unidade(agrupamento);

        // ===== O NOME DO FUSO QUE VAI PARA O POSTGRES =====
        // NÃO se manda o id do fallback (`br-fixo`) nem um "UTC-03" montado à mão. Em sintaxe
        // POSIX o sinal é INVERTIDO — `AT TIME ZONE 'UTC-03'` significa três horas a LESTE de
        // Greenwich, o oposto de Brasília. Seriam seis horas de erro em cada ponto, sem nenhum
        // erro de execução para denunciar.
        //
        // O fallback fixo do .NET existe porque `FindSystemTimeZoneById` depende do tzdata do
        // host (container alpine sem o pacote lança). O PostgreSQL embarca o próprio tzdata,
        // então aqui o nome IANA é sempre seguro.
        var nomeFuso = string.IsNullOrWhiteSpace(empresa.FusoHorario)
            ? FusoDeNegocio.PadraoBrasil
            : empresa.FusoHorario;

        var pontos = await ConsultarAsync(
            inicioUtc, fimUtc, nomeFuso, unidade, passo, empresa, feriados, ct);

        return new SerieTemporalDto(de, ate, agrupamento.ToString().ToLowerInvariant(), pontos);
    }

    /// <summary>Lista fechada: o valor vai para dentro de `date_trunc`, e aceitar texto do cliente
    /// ali seria injeção com outro nome — ainda que como parâmetro.</summary>
    private static (string Unidade, string Passo) Unidade(AgrupamentoSerie a) => a switch
    {
        AgrupamentoSerie.Dia => ("day", "1 day"),
        AgrupamentoSerie.Semana => ("week", "1 week"),
        AgrupamentoSerie.Mes => ("month", "1 month"),
        _ => throw new RegraDeNegocioException("Agrupamento inválido.")
    };

    private async Task<List<PontoSerie>> ConsultarAsync(
        DateTime inicioUtc, DateTime fimUtc, string fuso, string unidade, string passo,
        Contexto empresa, DateOnly[] feriados, CancellationToken ct)
    {
        // ===================== A ESPINHA: generate_series + LEFT JOIN =====================
        // `periodos` gera TODOS os pontos do intervalo; as CTEs de dado entram por LEFT JOIN.
        // Sem isso, um dia sem venda simplesmente não voltaria — e o gráfico ligaria o ponto
        // anterior no seguinte, desenhando uma reta onde houve um buraco. Gráfico com buraco
        // mente sobre a tendência, e mente para melhor.
        //
        // O `date_trunc` no generate_series é sobre PARÂMETRO, não sobre coluna: é o que garante
        // que os pontos gerados caiam exatamente nos mesmos limites que o GROUP BY produz
        // (semana começa na segunda, mês no dia 1).
        const string sql = """
            WITH periodos AS (
                SELECT gs::date AS periodo
                  FROM generate_series(
                           date_trunc($4, $1::timestamptz AT TIME ZONE $3),
                           date_trunc($4, ($2::timestamptz AT TIME ZONE $3) - interval '1 microsecond'),
                           $5::interval) AS gs
            ),
            leads AS (
                SELECT date_trunc($4, criado_em AT TIME ZONE $3)::date AS periodo,
                       COUNT(*) AS n
                  FROM contatos
                 WHERE empresa_id = $6
                   AND criado_em >= $1 AND criado_em < $2
                 GROUP BY 1
            ),
            -- ===================== A SÉRIE VEM DE `vendas` (NEG-1) =====================
            -- Era `contatos.ganho_em`, e a coluna guarda um valor só: reabrir um card apagava o
            -- ponto do mês passado do gráfico. Uma série histórica que muda para trás não é
            -- série histórica.
            --
            -- `status <> 'cancelada'` acompanha o predicado do índice parcial ix_vendas_periodo.
            -- O `date_trunc` continua sobre a coluna no SELECT (é o agrupamento), mas o FILTRO
            -- é faixa semi-aberta sobre `fechada_em` — é o filtro que precisa usar o índice.
            -- ===========================================================================
            ganhos AS (
                SELECT date_trunc($4, fechada_em AT TIME ZONE $3)::date AS periodo,
                       COUNT(*) AS n,
                       COALESCE(SUM(valor), 0) AS total
                  FROM vendas
                 WHERE empresa_id = $6
                   AND status <> 'cancelada'
                   AND fechada_em >= $1 AND fechada_em < $2
                 GROUP BY 1
            ),
            -- ===================== COMO A RESPOSTA É ENCONTRADA =====================
            -- `grupo` é a contagem corrente de saídas até a linha (inclusive). Toda entrada com
            -- grupo = g é respondida pela saída de grupo = g+1: a primeira que veio depois dela.
            --
            -- ⚠️ A FORMA ÓBVIA É QUADRÁTICA. A primeira versão usava
            --     MIN(criado_em) FILTER (WHERE direcao='saida')
            --       OVER (PARTITION BY conversa_id ORDER BY id
            --             ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING)
            -- que lê igualzinho ao enunciado do problema — e o `MIN` não tem função de transição
            -- INVERSA, então o Postgres não consegue mover o quadro incrementalmente: ele
            -- recalcula o agregado inteiro a cada linha. Numa conversa com 26 mil mensagens são
            -- ~676 milhões de operações, e a consulta estourou o timeout de 30s.
            --
            -- `SUM` sobre janela padrão TEM transição inversa e roda em uma passada; o resto é um
            -- hash join. Mesma resposta, O(n log n).
            -- ======================================================================
            timeline AS (
                SELECT conversa_id, direcao, criado_em,
                       SUM((direcao = 'saida')::int) OVER (
                           PARTITION BY conversa_id ORDER BY id) AS grupo,
                       LAG(direcao) OVER (PARTITION BY conversa_id ORDER BY id) AS anterior
                  FROM mensagens
                 WHERE empresa_id = $6
                   AND criado_em >= $1 AND criado_em < $7
            ),
            -- Cada `grupo` de saída tem exatamente UMA linha (o contador anda a cada saída), então
            -- este MIN é só a forma de projetar o instante junto da chave do join.
            saidas AS (
                SELECT conversa_id, grupo, MIN(criado_em) AS quando
                  FROM timeline
                 WHERE direcao = 'saida'
                 GROUP BY conversa_id, grupo
            ),
            respostas AS (
                SELECT date_trunc($4, e.criado_em AT TIME ZONE $3)::date AS periodo,
                       AVG(nexora_minutos_uteis(e.criado_em, s.quando, $3, $8, $9, $10, $11)) AS media
                  FROM timeline e
                  -- JOIN e não LEFT JOIN: entrada sem resposta fica de fora da média. Entrar como
                  -- zero premiaria quem não respondeu.
                  JOIN saidas s ON s.conversa_id = e.conversa_id AND s.grupo = e.grupo + 1
                 -- Só a PRIMEIRA entrada de cada rajada conta, igual ao `aguardando_desde ??=` do
                 -- webhook: se o cliente manda três mensagens seguidas, ele espera desde a
                 -- primeira, não desde a terceira.
                 WHERE e.direcao = 'entrada'
                   AND e.anterior IS DISTINCT FROM 'entrada'
                   -- A margem serve para ACHAR a resposta, não para criar ponto fora do período.
                   AND e.criado_em < $2
                 GROUP BY 1
            )
            SELECT p.periodo,
                   COALESCE(l.n, 0)::int      AS leads,
                   COALESCE(g.n, 0)::int      AS vendas,
                   COALESCE(g.total, 0)::numeric AS faturamento,
                   r.media::numeric           AS tempo_resposta
              FROM periodos p
              LEFT JOIN leads     l ON l.periodo = p.periodo
              LEFT JOIN ganhos    g ON g.periodo = p.periodo
              LEFT JOIN respostas r ON r.periodo = p.periodo
             ORDER BY p.periodo
            """;

        var conexao = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conexao.State != System.Data.ConnectionState.Open)
            await conexao.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conexao);
        // A transação em curso precisa ser passada à mão: comando cru não se alista sozinho, e
        // sem isto o teste (que roda tudo numa transação revertida) não enxergaria as próprias
        // linhas.
        cmd.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();

        cmd.Parameters.Add(new() { Value = inicioUtc });                                   // $1
        cmd.Parameters.Add(new() { Value = fimUtc });                                      // $2
        cmd.Parameters.Add(new() { Value = fuso });                                        // $3
        cmd.Parameters.Add(new() { Value = unidade });                                     // $4
        cmd.Parameters.Add(new() { Value = passo });                                       // $5
        cmd.Parameters.Add(new() { Value = contexto.EmpresaId });                          // $6
        cmd.Parameters.Add(new() { Value = fimUtc + MargemResposta });                     // $7
        cmd.Parameters.Add(new() { Value = (int)empresa.JanelaHoraInicio });               // $8
        cmd.Parameters.Add(new() { Value = (int)empresa.JanelaHoraFim });                  // $9
        cmd.Parameters.Add(new() { Value = (int)empresa.JanelaDiasSemana });               // $10
        cmd.Parameters.Add(new() { Value = feriados, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Date }); // $11

        var pontos = new List<PontoSerie>();
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            pontos.Add(new PontoSerie(
                DateOnly.FromDateTime(leitor.GetDateTime(0)),
                leitor.GetInt32(1),
                leitor.GetInt32(2),
                leitor.GetDecimal(3),
                await leitor.IsDBNullAsync(4, ct) ? null : leitor.GetDecimal(4)));
        }

        return pontos;
    }
}
