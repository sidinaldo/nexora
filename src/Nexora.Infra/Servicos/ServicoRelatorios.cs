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
using Nexora.Core.Seguranca;

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
public class ServicoRelatorios(NexoraDbContext db, IContextoEmpresa contexto, TimeProvider relogio)
    : IServicoRelatorios
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
        -- ⚠️ E4d: a fonte e `negociacoes`. `ganha_em` E o que era `fechada_em`, e aberta e
        -- perdida tem a coluna nula — a faixa ja as exclui sem precisar listar status.
        --
        -- ⚠️ O FILTRO DE ETAPA MUDOU DE SENTIDO NO E4e/4, e o comentario antigo avisava que
        -- isso ia acontecer. Ele recortava por onde a PESSOA estava no quadro HOJE; agora recorta
        -- por onde o NEGOCIO fechou. Nao ha escolha: `contatos.etapa_id` nao existe mais, e a
        -- pessoa pode estar em dois funis ao mesmo tempo — "a etapa dela" deixou de ter resposta.
        --
        -- A leitura nova e a mais util das duas para este relatorio: "quanto fechou vindo da
        -- Proposta" responde sobre o negocio, nao sobre onde a pessoa esta agora.
        base AS (
            SELECT v.ganha_em, v.valor, v.status
              FROM negociacoes v
              JOIN contatos c ON c.id = v.contato_id
             WHERE v.empresa_id = $6
               AND v.ganha_em >= $1 AND v.ganha_em < $2
               AND ($7::bigint IS NULL OR v.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
               AND ($9::bigint IS NULL OR v.etapa_id = $9)
               AND ($10::text  IS NULL OR v.status::text = $10)
               AND ($11::numeric IS NULL OR v.valor >= $11)
               AND ($12::numeric IS NULL OR v.valor <= $12)
        ),
        agregado AS (
            SELECT date_trunc($4, ganha_em AT TIME ZONE $3)::date AS periodo,
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
        var (pontos, totais) = await LerVendasAsync(j, ct);

        // ===================== O PERÍODO ANTERIOR (CMP-1) =====================
        // O MESMO cálculo, com outras datas. Não existe uma segunda consulta "da comparação": uma
        // cópia do SQL divergiria do original no primeiro ajuste de filtro, e o relatório passaria
        // a se comparar com uma pergunta ligeiramente diferente — sem erro nenhum para denunciar.
        //
        // ⚠️ `empresa` E `feriados` NÃO SÃO RELIDOS: a `Juncao` do período anterior é a mesma, com
        // as datas trocadas. São duas consultas economizadas por chamada, no relatório que a tela
        // abre primeiro.
        //
        // O desperdício que sobra, e é aceito: o anterior materializa os pontos do gráfico para
        // somar sete números. Num mês em dias são ~30 linhas; o teto de pontos da rota é 400.
        // ======================================================================
        var janela = PeriodoAnterior.Calcular(filtro.De, filtro.Ate, j.HojeLocal);

        var (_, anterior) = await LerVendasAsync(
            NoPeriodo(j, janela.AnteriorDe, janela.AnteriorAte), ct);

        return new RelatorioVendas(pontos, totais, Comparar(totais, anterior, janela));
    }

    private async Task<(List<PontoVendas> Pontos, TotaisVendas Totais)> LerVendasAsync(
        Juncao j, CancellationToken ct)
    {
        var pontos = new List<PontoVendas>();
        await LerAsync(SqlVendas, j.Parametros(), l =>
        {
            pontos.Add(new PontoVendas(
                DateOnly.FromDateTime(l.GetDateTime(0)),
                l.GetInt32(1), l.GetDecimal(2),
                l.GetInt32(3), l.GetDecimal(4),
                l.GetInt32(5), l.GetDecimal(6),
                null));
        }, ct);

        // A média móvel do gráfico, só no agrupamento por dia (AUD-XX, #23) — ver `MediaMovel`.
        if (j.Unidade == "day")
        {
            var medias = MediaMovel.De(pontos.Select(p => p.Faturamento).ToList());
            for (var i = 0; i < pontos.Count; i++)
            {
                pontos[i] = pontos[i] with { MediaFaturamento = medias[i] };
            }
        }

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

        return (pontos, totais with
        {
            TicketMedio = totais.Vendas == 0
                ? 0m
                : decimal.Round(totais.Faturamento / totais.Vendas, 2)
        });
    }

    /// <summary>A MESMA junção, noutras datas. Reaproveita empresa, feriados, filtros e janela de
    /// atendimento — o que muda é só o corte de tempo.</summary>
    private static Juncao NoPeriodo(Juncao j, DateOnly de, DateOnly ate)
    {
        var (inicio, fim) = EmUtc(de, ate, FusoDeNegocio.Resolver(j.Dados.FusoHorario));

        return j with { InicioUtc = inicio, FimUtc = fim, FimComMargem = fim + MargemResposta };
    }

    /// <summary>⚠️ A CONVERSÃO NUM LUGAR SÓ. O `PrepararAsync` e o período anterior precisam dela, e
    /// escrita duas vezes ela divergiria no corte — um usando `&lt;= fim` e o outro `&lt; fim+1d`
    /// faria o período anterior perder o último dia, com os dois números plausíveis na tela.</summary>
    private static (DateTime Inicio, DateTime Fim) EmUtc(
        DateOnly de, DateOnly ate, TimeZoneInfo fuso) =>
        (TimeZoneInfo.ConvertTimeToUtc(de.ToDateTime(TimeOnly.MinValue), fuso),
         TimeZoneInfo.ConvertTimeToUtc(ate.AddDays(1).ToDateTime(TimeOnly.MinValue), fuso));

    /// <summary>Os sete números contra os sete de antes.
    ///
    /// ⚠️ "CANCELADO" É `SentidoBom.Desce`, E É O QUE PROVA A REGRA DE COR. Subindo: seta para cima
    /// e vermelho. Caindo: seta para baixo e VERDE. Se a cor seguisse a seta, a tela pintaria de
    /// vermelho a melhor notícia do mês.</summary>
    private static ComparativoVendas Comparar(
        TotaisVendas atual, TotaisVendas anterior, PeriodoComparado janela)
    {
        IndicadorComparativo Um(decimal a, decimal b, SentidoBom bom) =>
            Comparacao.De(a, b, bom, janela.AnteriorDe, janela.AnteriorAte);

        return new ComparativoVendas(
            Um(atual.Vendas, anterior.Vendas, SentidoBom.Sobe),
            Um(atual.Faturamento, anterior.Faturamento, SentidoBom.Sobe),
            Um(atual.Concluidas, anterior.Concluidas, SentidoBom.Sobe),
            Um(atual.ValorConcluido, anterior.ValorConcluido, SentidoBom.Sobe),
            Um(atual.Canceladas, anterior.Canceladas, SentidoBom.Desce),
            Um(atual.ValorCancelado, anterior.ValorCancelado, SentidoBom.Desce),
            Um(atual.TicketMedio, anterior.TicketMedio, SentidoBom.Sobe),
            janela.De, janela.Ate, janela.EmAndamento);
    }

    /// <summary>⚠️ A TRADUÇÃO MORREU (E4e/5), como o comentário dela prometia.
    ///
    /// Entre o E4d e agora o contrato público falava `StatusVenda` e o banco já falava
    /// `StatusNegociacao`, e os dois divergiam justamente no estado mais comum: `fechada` era
    /// `ganha`. Mandar o texto cru fazia o filtro devolver ZERO linhas, sem erro nenhum e com o
    /// gráfico ao lado mostrando faturamento.
    ///
    /// Agora os dois falam a mesma língua e não há o que traduzir. O rótulo da tela não mudou:
    /// aquele filtro sempre se chamou "Em aberto" — era a palavra do fio, `fechada`, que já
    /// discordava do próprio rótulo.</summary>
    private static string? StatusNoBanco(StatusNegociacao? status) =>
        status?.ToString().ToLowerInvariant();

    // ==================================================================== 2 · desempenho
    /// <summary>LEFT JOIN a partir de `usuarios`, e não de `vendas`: o vendedor que não vendeu
    /// nada no período precisa aparecer com zero. Some da lista, ele vira ausência silenciosa
    /// justamente no mês em que o gestor mais precisava vê-lo.
    ///
    /// A linha "sem dono" entra pelo `UNION ALL`: contato sem responsável existe e vende, e
    /// descartá-lo faria a soma das linhas não bater com o total do relatório 1.</summary>
    private const string SqlDesempenho = """
        WITH vendas_periodo AS (
            -- E4d: `negociacoes` no lugar de `vendas`; `ganha_em` E o que era `fechada_em`.
            SELECT v.responsavel_id, v.valor
              FROM negociacoes v
              JOIN contatos c ON c.id = v.contato_id
             WHERE v.empresa_id = $6
               AND v.status <> 'cancelada'
               AND v.ganha_em >= $1 AND v.ganha_em < $2
               AND ($7::bigint IS NULL OR v.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
               AND ($11::numeric IS NULL OR v.valor >= $11)
               AND ($12::numeric IS NULL OR v.valor <= $12)
        ),
        -- Lead ATENDIDO = criado no periodo e sob responsabilidade de alguem. E a coluna de
        -- volume do vendedor, e por isso o recorte e por `criado_em`, nao por `ganho_em`.
        leads_periodo AS (
            SELECT c.responsavel_id,
                   COUNT(*) AS leads
              FROM contatos c
             WHERE c.empresa_id = $6
               AND c.anonimizado_em IS NULL
               AND c.criado_em >= $1 AND c.criado_em < $2
               AND ($7::bigint IS NULL OR c.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
             GROUP BY 1
        ),
        -- ===================== O OUTRO LADO DA CONVERSAO (AUD-XX, B10) =====================
        -- Negocio PERDIDO no periodo, pelo dono do NEGOCIO — o mesmo recorte das vendas logo
        -- acima, com os mesmos filtros. Era a perda pelo dono do CONTATO, de lead criado no
        -- periodo e perdido em qualquer data: o numerador perguntava "o que voce fechou neste
        -- mes" e o denominador "o que os seus leads deste mes perderam um dia".
        -- =================================================================================
        perdas_periodo AS (
            SELECT n.responsavel_id, COUNT(*) AS n
              FROM negociacoes n
              JOIN contatos c ON c.id = n.contato_id
             WHERE n.empresa_id = $6
               AND n.status = 'perdida'
               AND n.perdida_em >= $1 AND n.perdida_em < $2
               AND ($7::bigint IS NULL OR n.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
               AND ($11::numeric IS NULL OR n.valor >= $11)
               AND ($12::numeric IS NULL OR n.valor <= $12)
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
               COALESCE(pp.n, 0)::int                          AS perdidos
          FROM pessoas p
          LEFT JOIN LATERAL (
              SELECT COUNT(*) AS n, SUM(valor) AS total
                FROM vendas_periodo v
               WHERE v.responsavel_id IS NOT DISTINCT FROM p.id
          ) vq ON TRUE
          LEFT JOIN leads_periodo l  ON l.responsavel_id  IS NOT DISTINCT FROM p.id
          LEFT JOIN perdas_periodo pp ON pp.responsavel_id IS NOT DISTINCT FROM p.id
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
            // negócio ainda em negociação não entra no denominador, e sem nada decidido é `null`.
            var decididos = vendas + perdidos;

            linhas.Add(new LinhaVendedor(
                id, l.GetString(1), l.GetInt32(2), vendas, valor,
                vendas == 0 ? 0m : decimal.Round(valor / vendas, 2),
                Percentual.De(vendas, decididos)));
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
            SELECT c.id, c.origem::text AS origem,
                   -- E4e/4: o "perdeu" virou pergunta sobre os negocios da pessoa. Mesmo cuidado
                   -- do relatorio de vendedor: `EXISTS`, para nao multiplicar a linha do lead.
                   EXISTS (SELECT 1 FROM negociacoes n
                            WHERE n.contato_id = c.id
                              AND n.status = 'perdida') AS perdeu
              FROM contatos c
             WHERE c.empresa_id = $6
               AND c.anonimizado_em IS NULL
               AND c.criado_em >= $1 AND c.criado_em < $2
               AND ($7::bigint IS NULL OR c.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
               AND ($9::bigint IS NULL OR EXISTS (
                       SELECT 1 FROM negociacoes n
                        WHERE n.contato_id = c.id AND n.etapa_id = $9))
        ),
        -- A venda entra pelo CONTATO, e o recorte dela e o mesmo periodo: o lead de marco que
        -- fechou em abril nao conta no abril deste relatorio, porque a pergunta e "o que o canal
        -- trouxe no periodo", e o lead e do canal.
        vendas_do_lead AS (
            SELECT l.origem,
                   COUNT(DISTINCT v.contato_id) AS ganhos,
                   COALESCE(SUM(v.valor), 0)    AS total
              -- E4d: `negociacoes`. `COUNT(DISTINCT contato_id)` continua contando PESSOAS que
              -- compraram, que e o par certo do `perdido_em` do lado de la — os dois lados desta
              -- razao falam de gente.
              FROM leads l
              JOIN negociacoes v ON v.contato_id = l.id
             WHERE v.status <> 'cancelada'
               AND v.ganha_em IS NOT NULL
               AND ($11::numeric IS NULL OR v.valor >= $11)
               AND ($12::numeric IS NULL OR v.valor <= $12)
             GROUP BY 1
        )
        SELECT l.origem,
               COUNT(*)::int                                        AS leads,
               COALESCE(MAX(v.ganhos), 0)::int                      AS vendas,
               COALESCE(MAX(v.total), 0)::numeric                   AS valor,
               COUNT(*) FILTER (WHERE l.perdeu)::int                 AS perdidos
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
                Percentual.De(vendas, leads)));
        }, ct);

        return linhas;
    }

    // ==================================================================== 3b · vendas por canal
    /// <summary>O faturamento por CAMPANHA, e não por tipo de origem.
    ///
    /// `LEFT JOIN` e não `JOIN`: a venda sem canal identificado tem que aparecer. Ela é a maioria
    /// hoje — só passa a ter canal quem escaneou um QR desde a última compra —, e omiti-la faria
    /// a soma da tabela não bater com o faturamento do relatório 1, sem nada na tela explicando
    /// a diferença.
    ///
    /// ⚠️ O canal sai do CADASTRO do canal, não de `contatos.origem_detalhe`. O texto no contato
    /// é o nome congelado no dia da captura; aqui a pergunta é sobre a campanha viva, que pode
    /// ter sido renomeada — e o `id` é o que liga as duas coisas.
    ///
    /// ⚠️ Canal REMOVIDO vira `NULL` pela FK (`ON DELETE SET NULL`) e cai na linha "sem canal".
    /// É a razão de a remoção só ser permitida com zero leads: apagar campanha com histórico
    /// mudaria um relatório do mês passado.</summary>
    private const string SqlVendasPorCanal = """
        SELECT k.nome AS canal,
               COUNT(*)::int                     AS vendas,
               COALESCE(SUM(v.valor), 0)::numeric AS valor
          -- E4d: `negociacoes`, e o canal desta rodada chama-se `canal_ciclo_id` la — era
          -- `vendas.canal_id`, e a coluna guarda o MESMO fato (NEG-3).
          FROM negociacoes v
          JOIN contatos c ON c.id = v.contato_id
          LEFT JOIN canais_captacao k ON k.id = v.canal_ciclo_id
         WHERE v.empresa_id = $6
           AND v.ganha_em >= $1 AND v.ganha_em < $2
           AND v.status <> 'cancelada'
           AND ($7::bigint  IS NULL OR v.responsavel_id = $7)
           AND ($8::text    IS NULL OR c.origem::text = $8)
           AND ($9::bigint  IS NULL OR v.etapa_id = $9)
           AND ($10::text   IS NULL OR v.status::text = $10)
           AND ($11::numeric IS NULL OR v.valor >= $11)
           AND ($12::numeric IS NULL OR v.valor <= $12)
         GROUP BY k.nome
         ORDER BY valor DESC, vendas DESC
        """;

    public async Task<IReadOnlyList<LinhaCanalVenda>> VendasPorCanalAsync(
        FiltroRelatorio filtro, CancellationToken ct)
    {
        var j = await PrepararAsync(filtro, ct);

        var linhas = new List<LinhaCanalVenda>();
        await LerAsync(SqlVendasPorCanal, j.Parametros(), l => linhas.Add(new LinhaCanalVenda(
            l.IsDBNull(0) ? null : l.GetString(0), l.GetInt32(1), l.GetDecimal(2))), ct);

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
               -- ===================== OS MESMOS RECORTES DA FOTO (AUD-XX) =====================
               -- ⚠️ AS ENTRADAS IGNORAVAM PESSOA E ORIGEM, e a foto logo abaixo aplicava os dois.
               -- Quem nao tem `ver_numeros_da_equipe` recebe `$7` com o proprio id — e via as
               -- entradas da EMPRESA INTEIRA ao lado da foto so dele. Com filtro de origem, as duas
               -- metades do mesmo cartao respondiam perguntas diferentes.
               --
               -- A pessoa e a dona do NEGOCIO naquele funil, como na foto (`n.responsavel_id`); a
               -- origem e a do contato. `EXISTS`, e nao `JOIN`: sem filtro, nada muda.
               AND ($8::text IS NULL OR EXISTS (
                     SELECT 1 FROM contatos c
                      WHERE c.id = a.entidade_id AND c.empresa_id = a.empresa_id
                        AND c.origem::text = $8))
               AND ($7::bigint IS NULL OR EXISTS (
                     SELECT 1 FROM negociacoes n
                       JOIN etapas_funil d ON d.id = (a.alteracoes->'etapaId'->>'depois')::bigint
                      WHERE n.contato_id = a.entidade_id AND n.empresa_id = a.empresa_id
                        AND n.pipeline_id = d.pipeline_id
                        AND n.responsavel_id = $7))
             GROUP BY 1
        )
        SELECT e.id, e.nome, e.ordem, e.cor, COALESCE(x.n, 0)::int AS entradas,
               p.id AS pipeline_id, p.nome AS pipeline_nome
          FROM etapas_funil e
          JOIN pipelines p ON p.id = e.pipeline_id
          LEFT JOIN entradas x ON x.etapa_id = e.id
         WHERE e.empresa_id = $6
         -- O desempate NAO e enfeite: `ordem` e unica POR PIPELINE (`uq_etapas_ordem`), nao por
         -- empresa. So com `ORDER BY e.ordem`, as etapas de ordem 1 dos dois funis saem juntas, as
         -- de ordem 2 juntas, e a ordem RELATIVA entre elas e a que o Postgres quiser — pode
         -- mudar entre dois carregamentos. A tela nao tem como agrupar uma lista intercalada.
         --
         -- A mesma ordem de `ServicoPipelines.ListarAsync` (ordem, depois nome), para o relatorio
         -- e o menu lateral nao discordarem sobre qual funil vem primeiro.
         ORDER BY p.ordem, p.nome, p.id, e.ordem
        """;

    /// <summary>A FOTO, agora com o MESMO recorte do quadro (E4d).
    ///
    /// ⚠️ ESTE NÚMERO MUDA, e de propósito. Antes a consulta era `NoQuadro` por extenso — não
    /// perdido, não anonimizado — SEM a restrição que o kanban aplica na coluna de ganho. O
    /// relatório mostrava naquela coluna gente que o quadro já não mostrava, e ninguém tinha como
    /// saber por quê olhando as duas telas.
    ///
    /// Agora é o mesmo recorte do `ServicoFunil` e do `ServicoDashboard`. Nos dados de
    /// desenvolvimento a foto foi de 915 para 913 — saíram 3 contatos cujos negócios já foram todos
    /// concluídos (que o quadro também não mostra) e entrou duas vezes o contato que tem dois
    /// negócios vivos.
    ///
    /// ⚠️ ESTA É A QUINTA CÓPIA DO RECORTE, e a única escrita em SQL cru — então ela não quebra
    /// quando as outras mudam, ela só DIVERGE. No POS-1 ela ficou para trás por um momento: o card
    /// vendido que avança para a pós-venda aparecia no quadro e não aqui. A rede é o
    /// `A_CONTAGEM_DO_MENU_BATE_COM_A_SOMA_DO_QUADRO`, que agora compara os quatro consumidores.
    ///
    /// A subconsulta existe para o `LEFT JOIN` continuar sendo LEFT: condição sobre `contatos`
    /// no `WHERE` externo descartaria a etapa vazia, e a etapa sem negócio tem de aparecer com
    /// zero — sumir dela é pior que mostrar zero.</summary>
    private const string SqlFunilAgora = """
        SELECT e.id, e.nome, e.ordem, e.cor,
               COUNT(n.id)::int                  AS contatos,
               COALESCE(SUM(n.valor), 0)::numeric AS valor,
               p.id AS pipeline_id, p.nome AS pipeline_nome
          FROM etapas_funil e
          JOIN pipelines p ON p.id = e.pipeline_id
          LEFT JOIN (
              SELECT n.id, n.etapa_id, n.status, n.valor, n.responsavel_id
                FROM negociacoes n
                JOIN contatos c ON c.id = n.contato_id
               WHERE n.empresa_id = $6
                 AND c.anonimizado_em IS NULL
                 AND ($8::text IS NULL OR c.origem::text = $8)
          ) n ON n.etapa_id = e.id
             -- POS-1: a QUINTA copia do recorte, e a unica em SQL cru. So a coluna de ganho
             -- filtra; as outras mostram o que o `NoQuadro` admitir, inclusive o negocio ja
             -- vendido que avancou para uma etapa de pos-venda. Com o par antigo, esse card
             -- aparecia no quadro e NAO neste relatorio — a divergencia que o comentario acima
             -- diz que esta consulta existe para nao ter.
             --
             -- ⚠️ AS DUAS LINHAS, E A PRIMEIRA E A QUE EU ESQUECI. O par antigo
             -- ("ganho=ganha OU comum=aberta") cobria o equivalente do `NoQuadro` por ACIDENTE:
             -- ao nomear os dois status permitidos, ele excluia concluida/perdida/cancelada sem
             -- dizer que estava fazendo isso. Sozinha, a linha de baixo passou a admitir QUALQUER
             -- status nas colunas comuns, e o relatorio contou 6 onde o quadro mostrava 5.
             --
             -- Quem pegou foi o teste de paridade, na primeira execucao.
             AND n.status IN ('aberta', 'ganha')
             AND (NOT e.e_ganho OR n.status = 'ganha')
             AND ($7::bigint IS NULL OR n.responsavel_id = $7)
         WHERE e.empresa_id = $6
         GROUP BY e.id, e.nome, e.ordem, e.cor, p.id, p.nome, p.ordem
         ORDER BY p.ordem, p.nome, p.id, e.ordem
        """;

    public async Task<RelatorioFunil> FunilNoPeriodoAsync(
        FiltroRelatorio filtro, CancellationToken ct)
    {
        var j = await PrepararAsync(filtro, ct);

        // ===================== AS DUAS METADES, JUNTADAS AQUI (AUD-XX) =====================
        // As duas consultas listam TODAS as etapas da empresa (`WHERE e.empresa_id = $6`, com os
        // números por LEFT JOIN), então cada etapa da primeira está na segunda. A junção era da
        // tela, que punha 0 quando não achava a etapa.
        // =================================================================================
        var agora = new Dictionary<long, (int Contatos, decimal Valor)>();
        await LerAsync(SqlFunilAgora, j.Parametros(),
            l => agora[l.GetInt64(0)] = (l.GetInt32(4), l.GetDecimal(5)), ct);

        var etapas = new List<EtapaDoFunil>();
        await LerAsync(SqlFunilEntradas, j.Parametros(), l =>
        {
            var etapaId = l.GetInt64(0);
            var contatosAgora = 0;
            var valorAgora = 0m;
            if (agora.TryGetValue(etapaId, out var foto))
            {
                contatosAgora = foto.Contatos;
                valorAgora = foto.Valor;
            }

            etapas.Add(new EtapaDoFunil(
                etapaId, l.GetString(1), l.GetInt16(2), l.GetString(3),
                l.GetInt64(5), l.GetString(6),
                l.GetInt32(4), contatosAgora, valorAgora));
        }, ct);

        // Desde quando existe movimentação registrada. Sem este dado a tela não consegue explicar
        // por que um cliente de um ano vê zero entradas, e o relatório passa por quebrado.
        var comeca = await db.Auditoria.AsNoTracking()
            .OrderBy(a => a.Quando)
            .Select(a => (DateTime?)a.Quando)
            .FirstOrDefaultAsync(ct);

        return new RelatorioFunil(etapas, comeca);
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
               -- ⚠️ A AUTOMATICA SAI DA LINHA DO TEMPO INTEIRA (NPS-1), e nao so da contagem: o
               -- `grupo` e um contador acumulado sobre as saidas, entao deixa-la aqui e filtrar
               -- depois quebraria o pareamento entrada->resposta de todas as outras.
               AND m.origem = 'humana'
               -- ⚠️ E A ENTRADA QUE A PESQUISA CONSUMIU SAI TAMBEM (revisao NPS-1). O "10" do
               -- cliente nao e pergunta — e por isso nao acende o semaforo. Aqui ela ficava, e a
               -- proxima saida do vendedor, dias depois e sobre outro assunto, era pareada com ela:
               -- "respondeu em 5 dias". A coluna e NOT NULL, entao o `NOT` nao descarta nulo.
               AND NOT m.tratada_por_automacao
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
        -- ===================== A LINHA "AUTOMATICO" SAIU (NPS-1) =====================
        -- Havia aqui um `UNION ALL` com 'Automático', e o comentario que o defendia dizia: "e
        -- resposta que o cliente recebeu, e atribui-la a alguem seria autoria falsa".
        --
        -- ⚠️ O ARGUMENTO VALIA PARA UM FOLLOW-UP QUE RESPONDE, E ESSE CASO NAO ACONTECE. Medido:
        -- `DadosFollowUp.ConversasInativasAsync` exige `ultima_mensagem_direcao = saida`, ou seja,
        -- o follow-up so dispara quando a ultima palavra JA foi nossa — nunca em cima de uma
        -- entrada esperando resposta.
        --
        -- Quem caia aqui era o LEMBRETE com mensagem: ele dispara por `data_alvo <= hoje`, sem
        -- olhar a conversa. Se o cliente escreveu de manha e o lembrete saiu a tarde, a linha
        -- entrava como "resposta em 4 horas" — e nao foi resposta a nada. A linha media o defeito.
        --
        -- ===================== E A LINHA "PELO CELULAR" VOLTOU NO LUGAR DELA =====================
        -- ⚠️ TIRAR O `UNION ALL` SUMIU COM AS RESPOSTAS DADAS PELO CELULAR (revisao NPS-1). O
        -- INSERT do webhook nao grava `enviado_por` — ele nao sabe qual usuario do painel seria —,
        -- entao a resposta que o vendedor manda do proprio WhatsApp chega `humana` e SEM autor. Com
        -- a linha "Automático" ela caia ali, com o rotulo errado mas contada; sem a linha, o
        -- `LEFT JOIN` nao achava par para o `NULL` e ela simplesmente desaparecia.
        --
        -- Agora que a automatica sai pelo `origem = 'humana'` da linha do tempo, o balde do `NULL`
        -- so junta resposta HUMANA sem usuario do painel — ou seja, pelo celular. E aparece so
        -- quando tem resposta (o `HAVING`): a antiga aparecia zerada para todo mundo.
        --
        -- Com recorte por pessoa ($7) ela nao entra, e quem barra e o `s.enviado_por = $7` de
        -- `respostas`: resposta sem autor nao e de vendedor nenhum, a linha fica zerada, e o
        -- `HAVING` a tira. ⚠️ Havia tambem um `WHERE $7 IS NULL` aqui — segunda guarda do mesmo
        -- fato, e a sabotagem dele nao derrubava nada. Saiu.
        -- ======================================================================================
        pessoas AS (
            SELECT u.id, u.nome
              FROM usuarios u
             WHERE u.empresa_id = $6
               AND ($7::bigint IS NULL OR u.id = $7)
            UNION ALL
            SELECT NULL::bigint, 'Pelo celular'
        )
        SELECT p.id, p.nome,
               COUNT(r.minutos)::int                                          AS respostas,
               COALESCE(AVG(r.minutos), 0)::float8                            AS media,
               COALESCE(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY r.minutos), 0)::float8 AS mediana
          FROM pessoas p
          LEFT JOIN respostas r ON r.enviado_por IS NOT DISTINCT FROM p.id
         GROUP BY p.id, p.nome
        HAVING p.id IS NOT NULL OR COUNT(r.minutos) > 0
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
    /// <summary>⚠️ E4d: conta NEGÓCIO perdido, não pessoa perdida.
    ///
    /// É a mesma correção de unidade que o dashboard levou na taxa de conversão. Quem perdeu dois
    /// negócios com a mesma pessoa — por motivos diferentes, que é o caso interessante — entrava
    /// uma vez só, e o motivo da segunda perda sumia do relatório que existe para contá-los.
    ///
    /// `origem` e `anonimizado_em` continuam vindo do CONTATO: são da pessoa.</summary>
    private const string SqlMotivos = """
        WITH perdas AS (
            -- FONTE 1: o negocio que NUNCA virou venda. Valor quase sempre nulo — ele nao teve
            -- compra de onde herdar um numero, e pedir uma estimativa na hora de perder seria
            -- pedir para alguem inventar um valor depois do fato.
            SELECT COALESCE(NULLIF(TRIM(n.motivo_perda), ''), 'Sem motivo informado') AS motivo,
                   n.valor
              FROM negociacoes n
              JOIN contatos c ON c.id = n.contato_id
             WHERE n.empresa_id = $6
               AND c.anonimizado_em IS NULL
               AND n.perdida_em >= $1 AND n.perdida_em < $2
               AND ($7::bigint IS NULL OR n.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
               AND ($14::text  IS NULL OR n.motivo_perda = $14)

            UNION ALL

            -- ===================== FONTE 2: A VENDA DESFEITA (CAN-1) =====================
            -- A venda existiu, foi contada, e o cliente voltou atras. E a unica perda que vem com
            -- dinheiro de verdade: o `valor` e o da propria venda, nao uma estimativa.
            --
            -- ⚠️ `cancelamento_motivo IS NOT NULL` E O RECORTE INTEIRO. Nulo e "registrei errado"
            -- — um lancamento corrigido, que nao e perda de ninguem e nao pode entrar aqui.
            --
            -- ⚠️ A DATA E `cancelada_em`, NAO `ganha_em`. Perda e um EVENTO, e ele aconteceu no
            -- dia do cancelamento. Usar a data da venda poria a perda no mes em que o negocio
            -- fechou — um mes ja encerrado — e o mesmo fato apareceria em dois meses diferentes,
            -- porque o relatorio de Vendas mostra a cancelada pela data da venda.
            --
            -- ⚠️ `UNION ALL`, NAO `UNION`. Duas perdas com o mesmo motivo e o mesmo valor sao DOIS
            -- negocios perdidos; `UNION` as fundiria numa, e a contagem mentiria para baixo
            -- exatamente no caso que mais importa — o motivo que se repete.
            SELECT TRIM(n.cancelamento_motivo),
                   n.valor
              FROM negociacoes n
              JOIN contatos c ON c.id = n.contato_id
             WHERE n.empresa_id = $6
               AND c.anonimizado_em IS NULL
               AND n.cancelamento_motivo IS NOT NULL
               AND n.cancelada_em >= $1 AND n.cancelada_em < $2
               AND ($7::bigint IS NULL OR n.responsavel_id = $7)
               AND ($8::text   IS NULL OR c.origem::text = $8)
               AND ($14::text  IS NULL OR n.cancelamento_motivo = $14)
        )
        SELECT motivo,
               COUNT(*)::int                   AS contatos,
               COALESCE(SUM(valor), 0)::numeric AS valor
          FROM perdas
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
            -- E4d: `negociacoes`. `ganha_em` E o que era `fechada_em`, e o `IS NOT NULL`
            -- explicito tira aberta e perdida da contagem de COMPRAS — elas nao sao compra.
            SELECT v.contato_id,
                   COUNT(*)                AS compras,
                   COALESCE(SUM(v.valor), 0) AS total,
                   MAX(v.ganha_em)         AS ultima_em
              FROM negociacoes v
              JOIN contatos c ON c.id = v.contato_id
             WHERE v.empresa_id = $6
               AND v.status <> 'cancelada'
               AND v.ganha_em IS NOT NULL
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

        // ===================== A PÁGINA ALÉM DO FIM (AUD-XX, B5) =====================
        // O total vem de `COUNT(*) OVER ()`, lido de dentro das linhas — e uma página além do fim
        // não tem linha nenhuma: o total saía 0, e a tela dizia "nada aqui" com as páginas
        // anteriores cheias. A pergunta é refeita na MESMA consulta, do início e com uma linha só:
        // os filtros são os mesmos por construção, e o caso comum não paga nada a mais.
        // =============================================================================
        if (itens.Count == 0 && pagina > 1)
        {
            await LerAsync(SqlRecorrentes, j.Parametros(1, 0), l => { total = l.GetInt32(6); }, ct);
        }

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

        // ===================== A ORDEM PRECISA DE DESEMPATE =====================
        // `Ordem` e unica POR PIPELINE (`uq_etapas_ordem`), nao por empresa. Ordenando so por ela,
        // as etapas de ordem 1 dos dois funis saem juntas, as de ordem 2 juntas, e o Postgres
        // escolhe a ordem RELATIVA — que pode mudar entre dois carregamentos.
        //
        // A mesma ordem de `ServicoPipelines.ListarAsync` (`Ordem`, depois `Nome`), para o seletor
        // e o menu lateral nao discordarem sobre qual funil vem primeiro. `PipelineId` fecha o
        // desempate no dia em que dois funis tiverem ordem E nome iguais.
        // =======================================================================
        var etapas = await db.EtapasFunil.AsNoTracking()
            .OrderBy(e => e.Pipeline.Ordem).ThenBy(e => e.Pipeline.Nome).ThenBy(e => e.PipelineId)
            .ThenBy(e => e.Ordem)
            .Select(e => new OpcaoEtapa(e.Id, e.Nome, e.PipelineId, e.Pipeline.Nome))
            .ToListAsync(ct);

        // DISTINCT no banco. A alternativa — trazer os negócios perdidos e distinguir no C# —
        // varreria a tabela inteira para produzir meia dúzia de strings.
        //
        // ⚠️ SAIU DE `contatos` (E4e/4). O motivo da perda é do NEGÓCIO: a mesma pessoa pode ter
        // perdido por preço em março e por prazo em agosto, e a coluna do contato só guardava a
        // última — o filtro de relatório oferecia um motivo a menos do que existia.
        // ⚠️ AS DUAS FONTES AQUI TAMBEM (CAN-1). O relatorio passou a somar a venda desfeita
        // por desistencia, e um seletor que so oferece os motivos de perda deixaria as linhas
        // novas impossiveis de filtrar — visiveis na tabela e inalcancaveis pelo filtro, que e
        // pior que nao mostra-las.
        var motivos = await db.Negociacoes.AsNoTracking()
            .Where(n => n.PerdidaEm != null && n.MotivoPerda != null && n.MotivoPerda != "")
            .Select(n => n.MotivoPerda!)
            .Union(db.Negociacoes.AsNoTracking()
                .Where(n => n.CancelamentoMotivo != null)
                .Select(n => n.CancelamentoMotivo!))
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
        DateOnly[] Feriados, Empresa Dados, DateOnly HojeLocal)
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
        //
        // A conta mora em `EmUtc` porque o período anterior (CMP-1) precisa da MESMA.
        var (inicioUtc, fimUtc) = EmUtc(filtro.De, filtro.Ate, fuso);

        var feriados = await db.Feriados.AsNoTracking()
            .Where(f => f.Data >= filtro.De && f.Data <= filtro.Ate.AddDays(MargemResposta.Days)
                     && !db.FeriadosIgnorados.Any(i => i.FeriadoId == f.Id))
            .Select(f => f.Data)
            .ToArrayAsync(ct);

        var (unidade, passo) = Unidade(filtro.Agrupamento);

        // ===== O NOME DO FUSO QUE VAI PARA O POSTGRES =====
        // NÃO se manda o id do fallback (`br-fixo`) nem um "UTC-03" montado à mão. A regra mora em
        // `FusoDeNegocio.NomeIana` desde que o agendamento do NPS a esqueceu (revisão NPS-1): ela
        // estava escrita só aqui, e o segundo lugar que precisava dela mandou `fuso.Id`.
        var nomeFuso = FusoDeNegocio.NomeIana(empresa.FusoHorario);

        // HOJE na hora da EMPRESA, não do servidor. É o que decide se o período está em andamento
        // (CMP-1) — e às 22h de Brasília o servidor em UTC já está no dia seguinte.
        var hojeLocal = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(relogio.GetUtcNow().UtcDateTime, fuso));

        return new Juncao(
            inicioUtc, fimUtc, nomeFuso, unidade, passo,
            contexto.EmpresaId,
            ResponsavelEfetivo(filtro.ResponsavelId),
            filtro.Origem?.ToString().ToLowerInvariant(),
            filtro.EtapaId,
            StatusNoBanco(filtro.Status),
            filtro.ValorMin, filtro.ValorMax,
            fimUtc + MargemResposta,
            string.IsNullOrWhiteSpace(filtro.MotivoPerda) ? null : filtro.MotivoPerda,
            feriados, empresa, hojeLocal);
    }

    /// <summary>===================== O CORTE NÃO É POR PAPEL, É POR GESTO =====================
    /// Quem NÃO vê os números da equipe vê só os seus: o parâmetro que veio do cliente é
    /// DESCARTADO e o próprio usuário é imposto. Aceitar o valor da requisição aqui seria deixar a
    /// autorização na mão de quem a monta — a tela esconder o seletor não impede ninguém de trocar
    /// o parâmetro.
    ///
    /// Mesma linha de corte do `ServicoAtividades`, e escrita do mesmo jeito de propósito: duas
    /// formas diferentes da mesma regra divergem no dia em que uma delas muda.
    ///
    /// ⚠️ A VARIÁVEL SE CHAMAVA `ehVendedor`, E O NOME PASSOU A MENTIR NO PER-1. Com permissão por
    /// pessoa, um GESTOR sem `ver_numeros_da_equipe` entra neste ramo e um VENDEDOR com ela
    /// concedida sai dele. O papel deixou de ser a pergunta; o gesto é.
    /// ==============================================================</summary>
    private long? ResponsavelEfetivo(long? pedido)
    {
        var soVeOSeu = !contexto.Pode(Permissao.VerNumerosDaEquipe);

        return soVeOSeu ? contexto.UsuarioId : pedido;
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
        ("3b · vendas por canal", SqlVendasPorCanal),
        ("4 · funil (entradas)", SqlFunilEntradas),
        ("4 · funil (agora)", SqlFunilAgora),
        ("5 · tempo de resposta", SqlTempoResposta),
        ("6 · motivos de perda", SqlMotivos),
        ("7 · recorrentes", SqlRecorrentes)
    ];
}
