using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== OS RELATÓRIOS (BLOCO 14) =====================
///
/// O dashboard responde "como está agora". Isto responde "o que aconteceu no período".
///
/// TUDO NO SQL. Relatório é onde agregar em memória dói de verdade: a empresa com um ano de uso
/// tem centenas de milhares de mensagens, e trazê-las para contar no C# é a diferença entre 40ms
/// e um timeout. O `ServicoInbox` do Recupera materializa linhas antes de agregar, e o próprio
/// comentário de lá admite que aquilo cresce — é o erro que não se repete aqui.
///
/// ⚠️ NUNCA FUNÇÃO SOBRE COLUNA EM FILTRO. Os cortes são sempre `coluna >= $inicio AND coluna <
/// $fim`, com os limites calculados no C# a partir do fuso de negócio e passados como PARÂMETRO.
/// `WHERE date_trunc('month', fechada_em) = $1` daria o mesmo resultado e descartaria o índice.
/// `date_trunc` aparece só no SELECT e no GROUP BY, sobre o conjunto JÁ recortado. Existe um
/// teste que lê estas consultas e falha se a regra for quebrada.
///
/// SQL cru em vez de LINQ, no mesmo molde do `ServicoSerie`: `generate_series`, `LEFT JOIN` sobre
/// CTE, `PERCENTILE_CONT` e operador de `jsonb` não têm tradução em EF, e escrevê-los em LINQ
/// significaria trazer linha para a memória.
/// ======================================================================</summary>
public class ServicoRelatorios(NexoraDbContext db, IContextoEmpresa contexto) : IServicoRelatorios
{
    public const int TamanhoMaximoPagina = 200;

    /// <summary>Margem de varredura DEPOIS do fim do período, só para o tempo de resposta.
    ///
    /// Sem ela, a mensagem que chega 23h50 do último dia e é respondida 00h10 do dia seguinte
    /// entraria como "sem resposta" — o corte inventaria um problema de atendimento que não
    /// existiu. Dois dias cobrem folgadamente uma virada de fim de semana. Mesma constante e
    /// mesma razão do `ServicoSerie`.</summary>
    private static readonly TimeSpan MargemResposta = TimeSpan.FromDays(2);

    // ==================================================================== 1 · vendas
    /// <summary>===================== O QUE ESTA CONSULTA PROVA =====================
    /// `status <> 'cancelada'` no total e `status = 'cancelada'` na coluna à parte. É o predicado
    /// do índice parcial `ix_vendas_periodo`, e é o que faz CONCLUIR e CANCELAR terem efeitos
    /// OPOSTOS aqui: concluída continua no faturamento, cancelada sai retroativamente.
    ///
    /// Se os dois estados produzissem o mesmo número, o modelo do NEG-2 estaria errado — e é este
    /// relatório que denuncia.
    /// ======================================================================</summary>
    private const string SqlVendas = """
        WITH periodos AS (
            SELECT gs::date AS periodo
              FROM generate_series(
                       date_trunc($4, $1::timestamptz AT TIME ZONE $3),
                       date_trunc($4, ($2::timestamptz AT TIME ZONE $3) - interval '1 microsecond'),
                       $5::interval) AS gs
        ),
        base AS (
            SELECT v.fechada_em, v.valor, v.status
              FROM vendas v
              JOIN contatos c ON c.id = v.contato_id
             WHERE v.empresa_id = $6
               AND v.fechada_em >= $1 AND v.fechada_em < $2
               AND ($7::bigint IS NULL OR v.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
               AND ($9::bigint IS NULL OR c.etapa_id = $9)
               AND ($10::text  IS NULL OR v.status::text = $10)
               AND ($11::numeric IS NULL OR v.valor >= $11)
               AND ($12::numeric IS NULL OR v.valor <= $12)
        ),
        agregado AS (
            SELECT date_trunc($4, fechada_em AT TIME ZONE $3)::date AS periodo,
                   COUNT(*) FILTER (WHERE status <> 'cancelada')                AS vendas,
                   COALESCE(SUM(valor) FILTER (WHERE status <> 'cancelada'), 0) AS faturamento,
                   COUNT(*) FILTER (WHERE status = 'concluida')                 AS concluidas,
                   COALESCE(SUM(valor) FILTER (WHERE status = 'concluida'), 0)  AS valor_concluido,
                   COUNT(*) FILTER (WHERE status = 'cancelada')                 AS canceladas,
                   COALESCE(SUM(valor) FILTER (WHERE status = 'cancelada'), 0)  AS valor_cancelado
              FROM base
             GROUP BY 1
        )
        SELECT p.periodo,
               COALESCE(a.vendas, 0)::int          AS vendas,
               COALESCE(a.faturamento, 0)::numeric AS faturamento,
               COALESCE(a.concluidas, 0)::int      AS concluidas,
               COALESCE(a.valor_concluido, 0)::numeric AS valor_concluido,
               COALESCE(a.canceladas, 0)::int      AS canceladas,
               COALESCE(a.valor_cancelado, 0)::numeric AS valor_cancelado
          FROM periodos p
          LEFT JOIN agregado a ON a.periodo = p.periodo
         ORDER BY p.periodo
        """;

    public async Task<RelatorioVendas> VendasPorPeriodoAsync(
        FiltroRelatorio filtro, CancellationToken ct)
    {
        var j = await PrepararAsync(filtro, ct);

        var pontos = new List<PontoVendas>();
        await LerAsync(SqlVendas, j.Parametros(), l =>
        {
            pontos.Add(new PontoVendas(
                DateOnly.FromDateTime(l.GetDateTime(0)),
                l.GetInt32(1), l.GetDecimal(2),
                l.GetInt32(3), l.GetDecimal(4),
                l.GetInt32(5), l.GetDecimal(6)));
        }, ct);

        // O rodapé sai dos MESMOS pontos, e aqui somar em memória é correto: `periodos` tem no
        // máximo um item por dia do intervalo, já materializados para desenhar o gráfico. O que
        // não pode acontecer — e não acontece — é a soma varrer `vendas`.
        var totais = new TotaisVendas(
            pontos.Sum(p => p.Vendas),
            pontos.Sum(p => p.Faturamento),
            pontos.Sum(p => p.Concluidas),
            pontos.Sum(p => p.ValorConcluido),
            pontos.Sum(p => p.Canceladas),
            pontos.Sum(p => p.ValorCancelado),
            0m);

        return new RelatorioVendas(
            pontos,
            totais with
            {
                TicketMedio = totais.Vendas == 0
                    ? 0m
                    : decimal.Round(totais.Faturamento / totais.Vendas, 2)
            });
    }

    // ==================================================================== 2 · desempenho
    /// <summary>LEFT JOIN a partir de `usuarios`, e não de `vendas`: o vendedor que não vendeu
    /// nada no período precisa aparecer com zero. Some da lista, ele vira ausência silenciosa
    /// justamente no mês em que o gestor mais precisava vê-lo.
    ///
    /// A linha "sem dono" entra pelo `UNION ALL`: contato sem responsável existe e vende, e
    /// descartá-lo faria a soma das linhas não bater com o total do relatório 1.</summary>
    private const string SqlDesempenho = """
        WITH vendas_periodo AS (
            SELECT v.responsavel_id, v.valor
              FROM vendas v
              JOIN contatos c ON c.id = v.contato_id
             WHERE v.empresa_id = $6
               AND v.status <> 'cancelada'
               AND v.fechada_em >= $1 AND v.fechada_em < $2
               AND ($7::bigint IS NULL OR v.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
               AND ($11::numeric IS NULL OR v.valor >= $11)
               AND ($12::numeric IS NULL OR v.valor <= $12)
        ),
        -- Lead ATENDIDO = criado no periodo e sob responsabilidade de alguem. E a base da
        -- conversao, e por isso o recorte e por `criado_em`, nao por `ganho_em`.
        leads_periodo AS (
            SELECT c.responsavel_id,
                   COUNT(*)                                       AS leads,
                   COUNT(*) FILTER (WHERE c.perdido_em IS NOT NULL) AS perdidos
              FROM contatos c
             WHERE c.empresa_id = $6
               AND c.anonimizado_em IS NULL
               AND c.criado_em >= $1 AND c.criado_em < $2
               AND ($7::bigint IS NULL OR c.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
             GROUP BY 1
        ),
        pessoas AS (
            SELECT u.id, u.nome
              FROM usuarios u
             WHERE u.empresa_id = $6
               AND ($7::bigint IS NULL OR u.id = $7)
            UNION ALL
            SELECT NULL::bigint, 'Sem dono'
             WHERE $7::bigint IS NULL
        )
        SELECT p.id,
               p.nome,
               COALESCE(l.leads, 0)::int                       AS leads,
               COALESCE(vq.n, 0)::int                          AS vendas,
               COALESCE(vq.total, 0)::numeric                  AS valor,
               COALESCE(lp.perdidos, 0)::int                   AS perdidos
          FROM pessoas p
          LEFT JOIN LATERAL (
              SELECT COUNT(*) AS n, SUM(valor) AS total
                FROM vendas_periodo v
               WHERE v.responsavel_id IS NOT DISTINCT FROM p.id
          ) vq ON TRUE
          LEFT JOIN leads_periodo l  ON l.responsavel_id  IS NOT DISTINCT FROM p.id
          LEFT JOIN leads_periodo lp ON lp.responsavel_id IS NOT DISTINCT FROM p.id
         ORDER BY valor DESC, p.nome
        """;

    public async Task<IReadOnlyList<LinhaVendedor>> DesempenhoVendedoresAsync(
        FiltroRelatorio filtro, CancellationToken ct)
    {
        var j = await PrepararAsync(filtro, ct);

        var linhas = new List<LinhaVendedor>();
        await LerAsync(SqlDesempenho, j.Parametros(), l =>
        {
            var id = l.IsDBNull(0) ? (long?)null : l.GetInt64(0);
            var vendas = l.GetInt32(3);
            var valor = l.GetDecimal(4);
            var perdidos = l.GetInt32(5);

            // Conversão em memória sobre o conjunto JÁ agregado (uma linha por pessoa): é
            // aritmética sobre dois inteiros, não varredura. Mesma conta do dashboard —
            // contato ainda em negociação não entra no denominador.
            var fechados = vendas + perdidos;

            linhas.Add(new LinhaVendedor(
                id, l.GetString(1), l.GetInt32(2), vendas, valor,
                vendas == 0 ? 0m : decimal.Round(valor / vendas, 2),
                fechados == 0 ? 0d : (double)vendas / fechados));
        }, ct);

        // A linha "Sem dono" só aparece quando tem o que mostrar — uma linha de zeros em toda
        // empresa que atribui tudo seria ruído permanente.
        return [.. linhas.Where(l => l.UsuarioId is not null || l.LeadsAtendidos > 0 || l.Vendas > 0)];
    }

    // ==================================================================== 3 · origem
    /// <summary>O VALOR é o que responde "qual canal traz dinheiro" — sem ele o relatório diria só
    /// de onde vem gente, e volume alto com ticket baixo pareceria o melhor canal.</summary>
    private const string SqlOrigem = """
        WITH leads AS (
            SELECT c.id, c.origem::text AS origem, c.perdido_em
              FROM contatos c
             WHERE c.empresa_id = $6
               AND c.anonimizado_em IS NULL
               AND c.criado_em >= $1 AND c.criado_em < $2
               AND ($7::bigint IS NULL OR c.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
               AND ($9::bigint IS NULL OR c.etapa_id = $9)
        ),
        -- A venda entra pelo CONTATO, e o recorte dela e o mesmo periodo: o lead de marco que
        -- fechou em abril nao conta no abril deste relatorio, porque a pergunta e "o que o canal
        -- trouxe no periodo", e o lead e do canal.
        vendas_do_lead AS (
            SELECT l.origem,
                   COUNT(DISTINCT v.contato_id) AS ganhos,
                   COALESCE(SUM(v.valor), 0)    AS total
              FROM leads l
              JOIN vendas v ON v.contato_id = l.id
             WHERE v.status <> 'cancelada'
               AND ($11::numeric IS NULL OR v.valor >= $11)
               AND ($12::numeric IS NULL OR v.valor <= $12)
             GROUP BY 1
        )
        SELECT l.origem,
               COUNT(*)::int                                        AS leads,
               COALESCE(MAX(v.ganhos), 0)::int                      AS vendas,
               COALESCE(MAX(v.total), 0)::numeric                   AS valor,
               COUNT(*) FILTER (WHERE l.perdido_em IS NOT NULL)::int AS perdidos
          FROM leads l
          LEFT JOIN vendas_do_lead v ON v.origem = l.origem
         GROUP BY l.origem
         ORDER BY valor DESC, leads DESC
        """;

    public async Task<IReadOnlyList<LinhaOrigem>> OrigemLeadsAsync(
        FiltroRelatorio filtro, CancellationToken ct)
    {
        var j = await PrepararAsync(filtro, ct);

        var linhas = new List<LinhaOrigem>();
        await LerAsync(SqlOrigem, j.Parametros(), l =>
        {
            var leads = l.GetInt32(1);
            var vendas = l.GetInt32(2);

            // Denominador = LEADS do canal, não ganhos+perdidos. A pergunta aqui é "de cada 100
            // que este canal trouxe, quantos compraram" — é a taxa que decide onde investir, e
            // ela precisa contar quem ainda está em negociação.
            linhas.Add(new LinhaOrigem(
                l.GetString(0), leads, vendas, l.GetDecimal(3),
                leads == 0 ? 0d : (double)vendas / leads));
        }, ct);

        return linhas;
    }

    // ==================================================================== 4 · funil
    /// <summary>⚠️ O predicado é `alteracoes ? 'etapaId'`, e NÃO `acao = 'Moveu'`.
    ///
    /// O interceptor grava `etapaId: {antes, depois}` em qualquer evento que mude a etapa. Filtrar
    /// pelo verbo perderia `Ganhou` — que é como o card chega à coluna Venda — e "entraram em
    /// Venda" viria sempre zero, sem erro nenhum para denunciar.
    ///
    /// A existência da chave é testada por `jsonb_exists(...)`, e NÃO pelo operador `?`. O `?` do
    /// jsonb colide com o marcador de parâmetro de vários drivers, e o Npgsql com parâmetros
    /// POSICIONAIS não o reescreve: `??` chega literal ao Postgres e sai
    /// `operador não existe: jsonb ?? unknown`. A forma de função não tem essa ambiguidade.</summary>
    private const string SqlFunilEntradas = """
        WITH entradas AS (
            SELECT (a.alteracoes->'etapaId'->>'depois')::bigint AS etapa_id,
                   COUNT(*) AS n
              FROM auditoria a
             WHERE a.empresa_id = $6
               AND a.entidade = 'Contato'
               AND a.quando >= $1 AND a.quando < $2
               AND jsonb_exists(a.alteracoes, 'etapaId')
               AND a.alteracoes->'etapaId'->>'depois' IS NOT NULL
             GROUP BY 1
        )
        SELECT e.id, e.nome, e.ordem, e.cor, COALESCE(x.n, 0)::int AS entradas
          FROM etapas_funil e
          LEFT JOIN entradas x ON x.etapa_id = e.id
         WHERE e.empresa_id = $6
         ORDER BY e.ordem
        """;

    /// <summary>A FOTO. `NoQuadro` por extenso: não perdido, não anonimizado — o mesmo predicado
    /// que `RegrasContato.NoQuadro` traduz no kanban e no dashboard.</summary>
    private const string SqlFunilAgora = """
        SELECT e.id, e.nome, e.ordem, e.cor,
               COUNT(c.id)::int                  AS contatos,
               COALESCE(SUM(c.valor), 0)::numeric AS valor
          FROM etapas_funil e
          LEFT JOIN contatos c
                 ON c.etapa_id = e.id
                AND c.perdido_em IS NULL
                AND c.anonimizado_em IS NULL
                AND ($7::bigint IS NULL OR c.responsavel_id = $7)
                AND ($8::text   IS NULL OR c.origem::text = $8)
         WHERE e.empresa_id = $6
         GROUP BY e.id, e.nome, e.ordem, e.cor
         ORDER BY e.ordem
        """;

    public async Task<RelatorioFunil> FunilNoPeriodoAsync(
        FiltroRelatorio filtro, CancellationToken ct)
    {
        var j = await PrepararAsync(filtro, ct);

        var entradas = new List<EntradaEtapa>();
        await LerAsync(SqlFunilEntradas, j.Parametros(), l => entradas.Add(new EntradaEtapa(
            l.GetInt64(0), l.GetString(1), l.GetInt16(2), l.GetString(3), l.GetInt32(4))), ct);

        var agora = new List<EtapaAgora>();
        await LerAsync(SqlFunilAgora, j.Parametros(), l => agora.Add(new EtapaAgora(
            l.GetInt64(0), l.GetString(1), l.GetInt16(2), l.GetString(3),
            l.GetInt32(4), l.GetDecimal(5))), ct);

        // Desde quando existe movimentação registrada. Sem este dado a tela não consegue explicar
        // por que um cliente de um ano vê zero entradas, e o relatório passa por quebrado.
        var comeca = await db.Auditoria.AsNoTracking()
            .OrderBy(a => a.Quando)
            .Select(a => (DateTime?)a.Quando)
            .FirstOrDefaultAsync(ct);

        return new RelatorioFunil(entradas, agora, comeca);
    }

    // ==================================================================== 5 · tempo de resposta
    /// <summary>===================== A FORMA ÓBVIA É QUADRÁTICA =====================
    /// Esta é a mesma espinha do `ServicoSerie`, e pelo mesmo motivo: `MIN(...) OVER (ROWS BETWEEN
    /// 1 FOLLOWING AND UNBOUNDED FOLLOWING)` lê igualzinho ao enunciado do problema e NÃO tem
    /// função de transição inversa — o Postgres recalcula o agregado inteiro a cada linha. Numa
    /// conversa com 26 mil mensagens são ~676 milhões de operações.
    ///
    /// `SUM` sobre janela padrão tem transição inversa e roda em uma passada. Mesma resposta,
    /// O(n log n).
    ///
    /// A MEDIANA sai de `PERCENTILE_CONT(0.5)`, que é agregado nativo — trazer os tempos para
    /// ordenar no C# seria agregar em memória exatamente onde o volume é maior.
    /// ======================================================================</summary>
    private const string SqlTempoResposta = """
        WITH timeline AS (
            SELECT m.conversa_id, m.direcao, m.criado_em, m.enviado_por,
                   SUM((m.direcao = 'saida')::int) OVER (
                       PARTITION BY m.conversa_id ORDER BY m.id) AS grupo,
                   LAG(m.direcao) OVER (PARTITION BY m.conversa_id ORDER BY m.id) AS anterior
              FROM mensagens m
             WHERE m.empresa_id = $6
               AND m.criado_em >= $1 AND m.criado_em < $13
        ),
        -- Cada `grupo` de saida tem exatamente UMA linha (o contador anda a cada saida), entao
        -- estes MIN sao so a forma de projetar instante e autor junto da chave do join.
        saidas AS (
            SELECT conversa_id, grupo, MIN(criado_em) AS quando,
                   MIN(enviado_por) AS enviado_por
              FROM timeline
             WHERE direcao = 'saida'
             GROUP BY conversa_id, grupo
        ),
        respostas AS (
            SELECT s.enviado_por,
                   nexora_minutos_uteis(e.criado_em, s.quando, $3, $17, $18, $19, $20) AS minutos
              FROM timeline e
              -- JOIN e nao LEFT JOIN: entrada sem resposta fica de fora da media. Entrar como
              -- zero premiaria quem nao respondeu.
              JOIN saidas s ON s.conversa_id = e.conversa_id AND s.grupo = e.grupo + 1
             WHERE e.direcao = 'entrada'
               -- So a PRIMEIRA entrada de cada rajada conta, igual ao `aguardando_desde ??=` do
               -- webhook: tres mensagens seguidas do cliente sao uma espera, nao tres.
               AND e.anterior IS DISTINCT FROM 'entrada'
               -- A margem serve para ACHAR a resposta, nao para criar ponto fora do periodo.
               AND e.criado_em < $2
               AND ($7::bigint IS NULL OR s.enviado_por = $7)
        ),
        -- O follow-up automatico responde SEM usuario (`enviado_por` nulo, mesma regra do
        -- `AtorAuditoria.Sistema`). Ele vira uma linha propria em vez de sumir: e resposta que o
        -- cliente recebeu, e atribui-la a alguem seria autoria falsa.
        pessoas AS (
            SELECT u.id, u.nome
              FROM usuarios u
             WHERE u.empresa_id = $6
               AND ($7::bigint IS NULL OR u.id = $7)
            UNION ALL
            SELECT NULL::bigint, 'Automático'
             WHERE $7::bigint IS NULL
        )
        SELECT p.id, p.nome,
               COUNT(r.minutos)::int                                          AS respostas,
               COALESCE(AVG(r.minutos), 0)::float8                            AS media,
               COALESCE(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY r.minutos), 0)::float8 AS mediana
          FROM pessoas p
          LEFT JOIN respostas r ON r.enviado_por IS NOT DISTINCT FROM p.id
         GROUP BY p.id, p.nome
         ORDER BY respostas DESC, p.nome
        """;

    public async Task<IReadOnlyList<LinhaTempoResposta>> TempoRespostaAsync(
        FiltroRelatorio filtro, CancellationToken ct)
    {
        var j = await PrepararAsync(filtro, ct);

        var linhas = new List<LinhaTempoResposta>();
        await LerAsync(SqlTempoResposta, j.Parametros(), l => linhas.Add(new LinhaTempoResposta(
            l.IsDBNull(0) ? null : l.GetInt64(0),
            l.GetString(1), l.GetInt32(2),
            Math.Round(l.GetDouble(3), 1),
            Math.Round(l.GetDouble(4), 1))), ct);

        return linhas;
    }

    // ==================================================================== 6 · motivos de perda
    /// <summary>Ordenado pelo VALOR, não pela contagem: "perdemos 3 por preço e 1 por prazo" muda
    /// de leitura quando o de prazo valia dez vezes mais. O relatório existe para dizer onde
    /// mexer, e onde mexer é onde dói.</summary>
    private const string SqlMotivos = """
        SELECT COALESCE(NULLIF(TRIM(c.motivo_perda), ''), 'Sem motivo informado') AS motivo,
               COUNT(*)::int                     AS contatos,
               COALESCE(SUM(c.valor), 0)::numeric AS valor
          FROM contatos c
         WHERE c.empresa_id = $6
           AND c.anonimizado_em IS NULL
           AND c.perdido_em >= $1 AND c.perdido_em < $2
           AND ($7::bigint IS NULL OR c.responsavel_id = $7)
           AND ($8::text   IS NULL OR c.origem::text = $8)
           AND ($14::text  IS NULL OR c.motivo_perda = $14)
         GROUP BY 1
         ORDER BY valor DESC, contatos DESC
        """;

    public async Task<IReadOnlyList<LinhaMotivoPerda>> MotivosPerdaAsync(
        FiltroRelatorio filtro, CancellationToken ct)
    {
        var j = await PrepararAsync(filtro, ct);

        var linhas = new List<LinhaMotivoPerda>();
        await LerAsync(SqlMotivos, j.Parametros(), l => linhas.Add(new LinhaMotivoPerda(
            l.GetString(0), l.GetInt32(1), l.GetDecimal(2))), ct);

        return linhas;
    }

    // ==================================================================== 7 · recorrentes
    /// <summary>`HAVING COUNT(*) > 1` — quem comprou uma vez é cliente, não recorrente.
    ///
    /// O recorte por período vale sobre a ÚLTIMA compra, não sobre todas: a pergunta é "quem
    /// voltou recentemente", e exigir que as duas compras caiam no intervalo esconderia
    /// justamente o cliente antigo que acabou de voltar — que é o mais interessante da lista.
    ///
    /// PAGINADO no banco: uma padaria com dois anos de uso tem milhares.</summary>
    private const string SqlRecorrentes = """
        WITH compras AS (
            SELECT v.contato_id,
                   COUNT(*)                AS compras,
                   COALESCE(SUM(v.valor), 0) AS total,
                   MAX(v.fechada_em)       AS ultima_em
              FROM vendas v
              JOIN contatos c ON c.id = v.contato_id
             WHERE v.empresa_id = $6
               AND v.status <> 'cancelada'
               AND c.anonimizado_em IS NULL
               AND ($7::bigint IS NULL OR v.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
               AND ($11::numeric IS NULL OR v.valor >= $11)
               AND ($12::numeric IS NULL OR v.valor <= $12)
             GROUP BY v.contato_id
            HAVING COUNT(*) > 1
        ),
        recorte AS (
            SELECT * FROM compras
             WHERE ultima_em >= $1 AND ultima_em < $2
        )
        SELECT c.id, c.nome, c.telefone,
               r.compras::int, r.total::numeric, r.ultima_em,
               COUNT(*) OVER ()::int AS total_linhas
          FROM recorte r
          JOIN contatos c ON c.id = r.contato_id
         ORDER BY r.total DESC, r.ultima_em DESC
         LIMIT $15 OFFSET $16
        """;

    public async Task<Pagina<LinhaClienteRecorrente>> ClientesRecorrentesAsync(
        FiltroRelatorio filtro, int pagina, int tamanho, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamanho = Math.Clamp(tamanho, 1, TamanhoMaximoPagina);

        var j = await PrepararAsync(filtro, ct);

        var itens = new List<LinhaClienteRecorrente>();
        var total = 0;

        // `COUNT(*) OVER ()` traz o total na MESMA ida: um segundo SELECT COUNT repetiria a
        // agregação inteira sobre `vendas` só para desenhar "1 de 7".
        await LerAsync(SqlRecorrentes, j.Parametros(tamanho, (pagina - 1) * tamanho), l =>
        {
            itens.Add(new LinhaClienteRecorrente(
                l.GetInt64(0), l.GetString(1), l.GetString(2),
                l.GetInt32(3), l.GetDecimal(4), l.GetDateTime(5)));
            total = l.GetInt32(6);
        }, ct);

        return new Pagina<LinhaClienteRecorrente>(total, pagina, tamanho, itens);
    }

    // ==================================================================== opções da barra
    public async Task<OpcoesRelatorio> OpcoesAsync(CancellationToken ct)
    {
        // O MESMO recorte dos relatórios: se o vendedor só vê os próprios números, o seletor dele
        // só pode oferecer ele mesmo. Deixar a lista cheia e confiar na API para recusar depois
        // seria oferecer um caminho que não leva a lugar nenhum.
        var soEu = ResponsavelEfetivo(null);

        var responsaveis = await db.Usuarios.AsNoTracking()
            .Where(u => soEu == null || u.Id == soEu)
            .OrderBy(u => u.Nome)
            .Select(u => new OpcaoFiltro(u.Id, u.Nome))
            .ToListAsync(ct);

        var etapas = await db.EtapasFunil.AsNoTracking()
            .OrderBy(e => e.Ordem)
            .Select(e => new OpcaoFiltro(e.Id, e.Nome))
            .ToListAsync(ct);

        // DISTINCT no banco. A alternativa — trazer os contatos perdidos e distinguir no C# —
        // varreria a tabela inteira para produzir meia dúzia de strings.
        var motivos = await db.Contatos.AsNoTracking()
            .Where(c => c.PerdidoEm != null && c.MotivoPerda != null && c.MotivoPerda != "")
            .Select(c => c.MotivoPerda!)
            .Distinct()
            .OrderBy(m => m)
            .Take(100)
            .ToListAsync(ct);

        return new OpcoesRelatorio(responsaveis, etapas, motivos);
    }

    // ==================================================================== o preparo comum
    /// <summary>O que a consulta precisa saber da empresa. Tipo nomeado porque atravessa a
    /// fronteira de método — anônimo obrigaria a `dynamic`, que troca erro de compilação por erro
    /// em tempo de execução.</summary>
    private sealed record Empresa(
        string? FusoHorario, short JanelaHoraInicio, short JanelaHoraFim, short JanelaDiasSemana);

    /// <summary>Os parâmetros já resolvidos, na ORDEM em que as consultas os citam. Um bloco só
    /// para as sete: consulta que não usa `$14` simplesmente não o cita, e mandar parâmetro a
    /// mais não custa nada — enquanto manter sete listas diferentes custaria o dia em que uma
    /// delas saísse de ordem.</summary>
    private sealed record Juncao(
        DateTime InicioUtc, DateTime FimUtc, string Fuso, string Unidade, string Passo,
        long EmpresaId, long? ResponsavelId, string? Origem, long? EtapaId, string? Status,
        decimal? ValorMin, decimal? ValorMax, DateTime FimComMargem, string? MotivoPerda,
        DateOnly[] Feriados, Empresa Dados)
    {
        public NpgsqlParameter[] Parametros(int? limite = null, int? deslocamento = null) =>
        [
            new() { Value = InicioUtc },                                        // $1
            new() { Value = FimUtc },                                           // $2
            new() { Value = Fuso },                                             // $3
            new() { Value = Unidade },                                          // $4
            new() { Value = Passo },                                            // $5
            new() { Value = EmpresaId },                                        // $6
            Nulavel(ResponsavelId, NpgsqlDbType.Bigint),                        // $7
            Nulavel(Origem, NpgsqlDbType.Text),                                 // $8
            Nulavel(EtapaId, NpgsqlDbType.Bigint),                              // $9
            Nulavel(Status, NpgsqlDbType.Text),                                 // $10
            Nulavel(ValorMin, NpgsqlDbType.Numeric),                            // $11
            Nulavel(ValorMax, NpgsqlDbType.Numeric),                            // $12
            new() { Value = FimComMargem },                                     // $13
            Nulavel(MotivoPerda, NpgsqlDbType.Text),                            // $14
            new() { Value = limite ?? 20 },                                     // $15
            new() { Value = deslocamento ?? 0 },                                // $16
            // ⚠️ A JANELA VAI NO FIM, e não junto dos outros filtros: `nexora_minutos_uteis`
            // recebe quatro argumentos, e na primeira versão eles caíram em $8..$11 — em cima de
            // origem, etapa, status e valor mínimo. O tempo de resposta saía calculado contra a
            // origem do lead, sem erro nenhum para denunciar.
            new() { Value = (int)Dados.JanelaHoraInicio },                       // $17
            new() { Value = (int)Dados.JanelaHoraFim },                          // $18
            new() { Value = (int)Dados.JanelaDiasSemana },                       // $19
            new() { Value = Feriados,                                           // $20
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Date }
        ];

        /// <summary>`DBNull` COM TIPO DECLARADO. Sem o `NpgsqlDbType` o driver manda `unknown` e o
        /// Postgres não consegue resolver `$7::bigint IS NULL` — o erro sai como "could not
        /// determine data type", e a consulta inteira falha por causa de um filtro não usado.</summary>
        private static NpgsqlParameter Nulavel(object? valor, NpgsqlDbType tipo) =>
            new() { Value = valor ?? DBNull.Value, NpgsqlDbType = tipo };
    }

    private async Task<Juncao> PrepararAsync(FiltroRelatorio filtro, CancellationToken ct)
    {
        if (filtro.Ate < filtro.De)
            throw new RegraDeNegocioException("A data final não pode ser antes da inicial.");

        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new Empresa(
                e.FusoHorario, e.JanelaHoraInicio, e.JanelaHoraFim, e.JanelaDiasSemana))
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Empresa não encontrada.");

        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);

        // O usuário pede "até 31/08" pensando no dia inteiro. O SQL usa corte EXCLUSIVO — é o que
        // mantém `< $fim` em vez de `<= $fim`, e `<=` sobre timestamp perderia tudo que
        // acontecesse depois de 00h00 do último dia.
        var inicioUtc = TimeZoneInfo.ConvertTimeToUtc(filtro.De.ToDateTime(TimeOnly.MinValue), fuso);
        var fimUtc = TimeZoneInfo.ConvertTimeToUtc(
            filtro.Ate.AddDays(1).ToDateTime(TimeOnly.MinValue), fuso);

        var feriados = await db.Feriados.AsNoTracking()
            .Where(f => f.Data >= filtro.De && f.Data <= filtro.Ate.AddDays(MargemResposta.Days)
                     && !db.FeriadosIgnorados.Any(i => i.FeriadoId == f.Id))
            .Select(f => f.Data)
            .ToArrayAsync(ct);

        var (unidade, passo) = Unidade(filtro.Agrupamento);

        // ===== O NOME DO FUSO QUE VAI PARA O POSTGRES =====
        // NÃO se manda o id do fallback (`br-fixo`) nem um "UTC-03" montado à mão: em sintaxe
        // POSIX o sinal é INVERTIDO, e seriam seis horas de erro por ponto, sem exceção nenhuma
        // para denunciar. O PostgreSQL embarca o próprio tzdata, então o nome IANA é seguro.
        var nomeFuso = string.IsNullOrWhiteSpace(empresa.FusoHorario)
            ? FusoDeNegocio.PadraoBrasil
            : empresa.FusoHorario;

        return new Juncao(
            inicioUtc, fimUtc, nomeFuso, unidade, passo,
            contexto.EmpresaId,
            ResponsavelEfetivo(filtro.ResponsavelId),
            filtro.Origem?.ToString().ToLowerInvariant(),
            filtro.EtapaId,
            filtro.Status?.ToString().ToLowerInvariant(),
            filtro.ValorMin, filtro.ValorMax,
            fimUtc + MargemResposta,
            string.IsNullOrWhiteSpace(filtro.MotivoPerda) ? null : filtro.MotivoPerda,
            feriados, empresa);
    }

    /// <summary>===================== O CORTE DE PAPEL =====================
    /// Para VENDEDOR o parâmetro que veio do cliente é DESCARTADO e o próprio usuário é imposto.
    /// Aceitar o valor da requisição aqui seria deixar a autorização na mão de quem a monta — a
    /// tela esconder o seletor não impede ninguém de trocar o parâmetro.
    ///
    /// Mesma linha de corte do `ServicoAtividades`, e escrita do mesmo jeito de propósito: duas
    /// formas diferentes da mesma regra divergem no dia em que uma delas muda.
    /// ==============================================================</summary>
    private long? ResponsavelEfetivo(long? pedido)
    {
        var ehVendedor = !string.Equals(contexto.Papel, "dono", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(contexto.Papel, "gestor", StringComparison.OrdinalIgnoreCase);

        return ehVendedor ? contexto.UsuarioId : pedido;
    }

    /// <summary>Lista FECHADA: o valor vai para dentro de `date_trunc`, e aceitar texto do cliente
    /// ali seria injeção com outro nome — ainda que passado como parâmetro.</summary>
    private static (string Unidade, string Passo) Unidade(AgrupamentoSerie a) => a switch
    {
        AgrupamentoSerie.Dia => ("day", "1 day"),
        AgrupamentoSerie.Semana => ("week", "1 week"),
        AgrupamentoSerie.Mes => ("month", "1 month"),
        _ => throw new RegraDeNegocioException("Agrupamento inválido.")
    };

    private async Task LerAsync(
        string sql, NpgsqlParameter[] parametros, Action<NpgsqlDataReader> ler, CancellationToken ct)
    {
        var conexao = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conexao.State != ConnectionState.Open) await conexao.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conexao);

        // A transação em curso precisa ser passada à mão: comando cru não se alista sozinho, e sem
        // isto o teste (que roda tudo numa transação revertida) não enxergaria as próprias linhas.
        cmd.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.Parameters.AddRange(parametros);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct)) ler(leitor);
    }

    /// <summary>===================== EXPOSTAS PARA O TESTE LER =====================
    /// Há um teste que varre estas consultas atrás de função sobre coluna dentro de um `WHERE` —
    /// a regra que descarta índice e transforma relatório em varredura sequencial.
    ///
    /// Expor SQL para teste é feio; a alternativa é uma regra que vale só enquanto alguém lembra
    /// dela na revisão. O comentário no topo do arquivo não impede ninguém de escrever
    /// `date_trunc('month', fechada_em) = $1` daqui a seis meses; este teste impede.
    /// ==================================================================</summary>
    public static IReadOnlyList<(string Nome, string Sql)> ConsultasParaAuditoria =>
    [
        ("1 · vendas", SqlVendas),
        ("2 · desempenho", SqlDesempenho),
        ("3 · origem", SqlOrigem),
        ("4 · funil (entradas)", SqlFunilEntradas),
        ("4 · funil (agora)", SqlFunilAgora),
        ("5 · tempo de resposta", SqlTempoResposta),
        ("6 · motivos de perda", SqlMotivos),
        ("7 · recorrentes", SqlRecorrentes)
    ];
}
