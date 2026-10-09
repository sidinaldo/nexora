using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== LEADS PARADOS (LPA-1) =====================
///
/// Acha quem parou de ser trabalhado. A regra e o porquê de cada metade estão em
/// `IServicoLeadsParados`; aqui mora a consulta.
///
/// ⚠️ A GRANULARIDADE É A NEGOCIAÇÃO ABERTA, NÃO O CONTATO. Uma pessoa pode ter um negócio aberto
/// em dois funis (`uq_negociacoes_card_por_funil` permite um por funil), e os dois podem estar
/// parados por motivos diferentes. Agrupar por contato esconderia um dos dois — e as ações em lote
/// das entregas seguintes agem sobre o NEGÓCIO, não sobre a pessoa.
///
/// Contato sem negociação nenhuma rende uma linha com funil e etapa nulos. É o lead frio mais
/// comum — entrou por formulário ou importação e ninguém abriu negócio.
/// ==============================================================</summary>
public class ServicoLeadsParados(
    NexoraDbContext db, IContextoEmpresa contexto, TimeProvider relogio, ColetorAuditoria trilha,
    IServicoContatos contatos, ILogger<ServicoLeadsParados> log)
    : IServicoLeadsParados
{
    /// <summary>===================== POR QUE UNION, E NÃO UM COALESCE =====================
    ///
    /// A forma óbvia era `COALESCE(cv.ultima_mensagem_em, c.criado_em) &lt; $1` num LEFT JOIN. Ela
    /// é correta e foi MEDIDA contra o banco:
    ///
    ///     Hash Right Join
    ///       Filter: (COALESCE(cv.ultima_mensagem_em, c.criado_em) &lt; ...)
    ///
    /// A data cai como `Filter`, depois de juntar TODO contato da empresa com TODA conversa dela.
    /// É exatamente o que a regra da casa proíbe — função sobre coluna em filtro descarta o índice
    /// — e ⚠️ **o teste de auditoria NÃO pegaria**: ele proíbe `date_trunc(`, `lower(`, `upper(`,
    /// `cast(` e `::date`, e `COALESCE` não está na lista.
    ///
    /// Em dois ramos, cada predicado volta a ser sobre UMA tabela e UMA coluna — sargável:
    ///
    ///   · quem NUNCA conversou → `ix_contatos_criado (empresa_id, criado_em DESC)`, medido com as
    ///     DUAS condições dentro do `Index Cond`;
    ///   · quem TEM conversa → `empresa_id` entra no `Index Cond` e a data é avaliada sobre a
    ///     fatia do tenant. ⚠️ NÃO é Index Only: `ix_conversas_lista` não carrega `contato_id`, e
    ///     o planejador prefere um índice menor. Medi as duas formas — o ganho sobre o `COALESCE`
    ///     é o predicado ter voltado para uma tabela só, não o índice ficar perfeito.
    ///
    /// Se o volume crescer, o conserto é um índice em `conversas (empresa_id, ultima_mensagem_em)`
    /// com `INCLUDE (contato_id)`. Não vale agora: o maior tenant daqui tem centenas de conversas.
    ///
    /// ⚠️ QUEM ENTRA E QUEM SAI, e a regra não é "status da negociação" por engano:
    ///
    ///   · entra o contato com pelo menos uma negociação ABERTA — é o negócio parado;
    ///   · entra o contato sem nenhuma negociação RESOLVIDA (`ganha`, `concluida`, `perdida`) —
    ///     cobre tanto o lead que ninguém abriu quanto aquele cuja única venda foi CANCELADA, que
    ///     é desfazer um registro, não perder o cliente;
    ///   · sai quem tem `ganha`/`concluida` e nada aberto — é cliente, não lead frio;
    ///   · sai quem tem `perdida` — é a aba "Perdidos", que vem numa entrega própria, com o
    ///     índice que falta.
    ///
    /// ⚠️ A SEGUNDA CONDIÇÃO ERA "NENHUMA NEGOCIAÇÃO" E ESTAVA ERRADA. Uma venda cancelada deixa a
    /// linha no banco, então o contato não tinha `aberta` nem "nenhuma" — sumia da lista. Quem
    /// marcou venda por engano e desfez ficava invisível justamente para quem precisava retomá-lo.
    /// O teste `NEGOCIO_CANCELADO_DEVOLVE_O_CONTATO_A_LISTA` é quem pegou.
    ///
    /// O cliente recorrente com um pós-venda aberto e parado APARECE — e tem de aparecer: aquele
    /// negócio está parado de verdade.
    /// ==============================================================</summary>
    private const string SqlParados = """
        WITH parados AS (
            -- UMA LINHA POR CONTATO (CONV-XX): com uma conversa por número, quem conversa no
            -- número B não está parado só porque o A ficou quieto. Vale a mais recente.
            SELECT cv.contato_id, MAX(cv.ultima_mensagem_em) AS parado_desde
              FROM conversas cv
             WHERE cv.empresa_id = $2
             GROUP BY cv.contato_id
            HAVING MAX(cv.ultima_mensagem_em) < $1
            UNION ALL
            SELECT c.id, c.criado_em
              FROM contatos c
             WHERE c.empresa_id = $2
               AND c.criado_em < $1
               AND NOT EXISTS (SELECT 1 FROM conversas cv WHERE cv.contato_id = c.id)
        ),
        elegiveis AS (
            SELECT p.contato_id, p.parado_desde
              FROM parados p
              JOIN contatos c ON c.id = p.contato_id
             WHERE c.anonimizado_em IS NULL
               AND (
                     EXISTS (SELECT 1 FROM negociacoes n
                              WHERE n.contato_id = c.id AND n.status = 'aberta')
                 OR NOT EXISTS (SELECT 1 FROM negociacoes n
                                 WHERE n.contato_id = c.id
                                   AND n.status IN ('ganha', 'concluida', 'perdida'))
               )
        )
        SELECT COUNT(*) OVER ()            AS total,
               c.id, c.nome, c.telefone, c.origem::text,
               n.id, n.valor, d.responsavel_id,
               u.nome, pi.nome, et.nome,
               e.parado_desde,
               -- Mesma posicao do `motivo_perda` dos Perdidos: as duas consultas tem o mesmo
               -- formato, e a leitura das linhas e uma so.
               NULL::text AS motivo_perda,
               -- ===================== AS ETIQUETAS DA LINHA =====================
               -- Uma subconsulta por linha, sobre no maximo 50: o indice de `negociacoes_etiquetas`
               -- comeca em `negociacao_id`. Sem negocio, `n.id` e nulo e vem `[]`. O tenant e
               -- garantido pela FK composta `fk_negociacoes_etiquetas_negociacao`.
               (SELECT COALESCE(json_agg(json_build_object('id', tg.id, 'nome', tg.nome, 'cor', tg.cor)
                                         ORDER BY tg.nome), '[]')
                  FROM negociacoes_etiquetas ne2
                  JOIN etiquetas tg ON tg.id = ne2.etiqueta_id
                 WHERE ne2.negociacao_id = n.id)::text AS etiquetas
          FROM elegiveis e
          JOIN contatos c ON c.id = e.contato_id
          LEFT JOIN negociacoes n ON n.contato_id = c.id AND n.status = 'aberta'
          -- ===================== DE QUEM E O LEAD =====================
          -- Com negocio aberto, do NEGOCIO — a `LiberacaoDeCiclo` zera o do contato ao concluir a
          -- venda, e e no negocio que a acao em lote escreve. SEM negocio, do CONTATO: e o unico
          -- dono que existe.
          --
          -- ⚠️ ERA SO `n.responsavel_id`, e o lead sem negocio — o lead frio mais comum, que chegou
          -- pelo WhatsApp e ninguem abriu card — tinha `n` nulo: sumia da lista PROPRIA do vendedor
          -- que o atendeu, e a coluna dizia "sem responsavel" para um lead que tinha dono. A
          -- interface promete "so os PROPRIOS leads parados", e entregava menos.
          -- ============================================================
          CROSS JOIN LATERAL (
            SELECT CASE WHEN n.id IS NULL THEN c.responsavel_id ELSE n.responsavel_id END
                   AS responsavel_id
          ) d
          LEFT JOIN usuarios u    ON u.id = d.responsavel_id
          LEFT JOIN pipelines pi  ON pi.id = n.pipeline_id
          LEFT JOIN etapas_funil et ON et.id = n.etapa_id
         WHERE ($3::bigint IS NULL OR d.responsavel_id = $3)
           AND ($6::bigint IS NULL OR n.pipeline_id = $6)
           AND ($7::bigint IS NULL OR n.etapa_id = $7)
           AND ($8::text IS NULL OR c.origem::text = $8)
           AND ($9::bigint IS NULL OR EXISTS (
                     SELECT 1 FROM negociacoes_etiquetas ne
                      WHERE ne.negociacao_id = n.id AND ne.etiqueta_id = $9))
           AND ($10::numeric IS NULL OR n.valor >= $10)
           AND ($11::numeric IS NULL OR n.valor <= $11)
         ORDER BY e.parado_desde, c.id
         LIMIT $4 OFFSET $5
        """;

    /// <summary>===================== A ABA "PERDIDOS" (LPA-1) =====================
    ///
    /// ⚠️ CONSULTA SEPARADA, E NAO UM `status` A MAIS NO `SqlParados`. Os dois eixos de tempo sao
    /// colunas de tabelas diferentes — `conversas.ultima_mensagem_em` la, `negociacoes.perdida_em`
    /// aqui — e unifica-las exigiria um `CASE` no filtro, que e funcao sobre coluna: o mesmo
    /// descarte de indice que o `COALESCE` provocou na entrega 1.
    ///
    /// Aqui nao ha UNION: perder exige ter havido negocio, entao a consulta parte de
    /// `negociacoes` e o predicado cai direto no indice parcial
    /// `ix_negociacoes_perdidas (empresa_id, perdida_em) WHERE status = 'perdida'`, criado nesta
    /// entrega. Ele e o espelho do `ix_negociacoes_ganhas`.
    ///
    /// ⚠️ `NOT EXISTS (aberta)` E O QUE FAZ AS DUAS ABAS SEREM DISJUNTAS. Sem ele, o contato com
    /// uma perda em Vendas e um negocio aberto em Pos-venda apareceria nas duas, e reabrir em lote
    /// cairia sobre alguem que ja esta sendo trabalhado — e `AbrirNegociacaoAsync` responderia 409
    /// para metade do lote.
    ///
    /// ⚠️ UMA LINHA POR PERDA, nao por contato: `uq_negociacoes_card_por_funil` nao conta perda,
    /// entao a mesma pessoa pode ter perdido em dois funis, por motivos diferentes. Reabrir
    /// deduplica por contato, igual ao lembrete.
    /// ==============================================================</summary>
    private const string SqlPerdidos = """
        SELECT COUNT(*) OVER ()            AS total,
               c.id, c.nome, c.telefone, c.origem::text,
               n.id, n.valor, n.responsavel_id,
               u.nome, pi.nome, et.nome,
               n.perdida_em, n.motivo_perda,
               -- ===================== AS ETIQUETAS DA LINHA =====================
               -- Uma subconsulta por linha, sobre no maximo 50: o indice de `negociacoes_etiquetas`
               -- comeca em `negociacao_id`. Sem negocio, `n.id` e nulo e vem `[]`. O tenant e
               -- garantido pela FK composta `fk_negociacoes_etiquetas_negociacao`.
               (SELECT COALESCE(json_agg(json_build_object('id', tg.id, 'nome', tg.nome, 'cor', tg.cor)
                                         ORDER BY tg.nome), '[]')
                  FROM negociacoes_etiquetas ne2
                  JOIN etiquetas tg ON tg.id = ne2.etiqueta_id
                 WHERE ne2.negociacao_id = n.id)::text AS etiquetas
          FROM negociacoes n
          JOIN contatos c ON c.id = n.contato_id AND c.empresa_id = n.empresa_id
          LEFT JOIN usuarios u     ON u.id = n.responsavel_id
          LEFT JOIN pipelines pi   ON pi.id = n.pipeline_id
          LEFT JOIN etapas_funil et ON et.id = n.etapa_id
         WHERE n.empresa_id = $2
           AND n.status = 'perdida'
           AND n.perdida_em < $1
           AND c.anonimizado_em IS NULL
           AND NOT EXISTS (SELECT 1 FROM negociacoes a
                            WHERE a.contato_id = c.id AND a.status = 'aberta')
           AND ($3::bigint IS NULL OR n.responsavel_id = $3)
           AND ($6::bigint IS NULL OR n.pipeline_id = $6)
           AND ($7::bigint IS NULL OR n.etapa_id = $7)
           AND ($8::text IS NULL OR c.origem::text = $8)
           AND ($9::bigint IS NULL OR EXISTS (
                     SELECT 1 FROM negociacoes_etiquetas ne
                      WHERE ne.negociacao_id = n.id AND ne.etiqueta_id = $9))
           AND ($10::numeric IS NULL OR n.valor >= $10)
           AND ($11::numeric IS NULL OR n.valor <= $11)
         ORDER BY n.perdida_em ASC, n.id ASC
         LIMIT $4 OFFSET $5
        """;

    public async Task<PaginaComTotal<LeadParado>> ListarAsync(FiltroLeadsParados filtro, CancellationToken ct)
    {
        if (!JanelasDeParada.EmDias.Contains(filtro.Dias))
            throw new RegraDeNegocioException(
                $"Janela inválida: {filtro.Dias}. Use {string.Join(", ", JanelasDeParada.EmDias)}.");

        var tamanho = Math.Clamp(filtro.Tamanho, 1, JanelasDeParada.TamanhoMaximoPagina);
        var pagina = Math.Max(1, filtro.Pagina);

        var fuso = await FusoAsync(ct);

        // ⚠️ O CORTE É À MEIA-NOITE LOCAL, não "agora menos N dias". Com "agora", a mesma lista
        // muda de tamanho entre dois carregamentos no mesmo dia, e o dono que marcou dez leads às
        // 9h voltaria às 15h com a página diferente. Meia-noite dá uma lista estável por dia.
        var hojeLocal = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(relogio.GetUtcNow().UtcDateTime, fuso));

        var limite = TimeZoneInfo.ConvertTimeToUtc(
            hojeLocal.AddDays(-filtro.Dias).ToDateTime(TimeOnly.MinValue), fuso);

        var recorte = ResponsavelEfetivo(filtro.ResponsavelId);

        // Uma função, e não um array: o comando roda duas vezes na página além do fim, e
        // parâmetro do Npgsql não se reaproveita entre comandos.
        NpgsqlParameter[] Parametros(int quantos, int deslocamento) =>
        [
            new() { Value = limite },                                           // $1
            new() { Value = contexto.EmpresaId },                               // $2
            new() { Value = (object?)recorte ?? DBNull.Value,
                    NpgsqlDbType = NpgsqlDbType.Bigint },                       // $3
            new() { Value = quantos },                                          // $4
            new() { Value = deslocamento },                                     // $5
            Nulavel(filtro.PipelineId, NpsqlBigint),                            // $6
            Nulavel(filtro.EtapaId, NpsqlBigint),                               // $7
            Nulavel(filtro.Origem, NpgsqlDbType.Text),                          // $8
            Nulavel(filtro.EtiquetaId, NpsqlBigint),                            // $9
            Nulavel(filtro.ValorMin, NpgsqlDbType.Numeric),                     // $10
            Nulavel(filtro.ValorMax, NpgsqlDbType.Numeric)                      // $11
        ];

        var itens = new List<LeadParado>();
        var total = 0;

        // ⚠️ AS DUAS CONSULTAS TEM A MESMA LISTA DE PARAMETROS E AS MESMAS 12 PRIMEIRAS COLUNAS,
        // e e por isso que a leitura e uma. O que muda e a coluna 12: `Perdidos` traz o motivo da
        // perda, e `Parados` nao tem motivo nenhum para trazer.
        var perdidos = filtro.Aba == AbaDeLeads.Perdidos;

        var sql = perdidos ? SqlPerdidos : SqlParados;

        await LerAsync(sql, Parametros(tamanho, (pagina - 1) * tamanho), l =>
        {
            total = (int)l.GetInt64(0);

            var paradoDesde = l.GetDateTime(11);

            itens.Add(new LeadParado(
                ContatoId: l.GetInt64(1),
                Nome: l.GetString(2),
                Telefone: l.GetString(3),
                Origem: l.GetString(4),
                ResponsavelId: l.IsDBNull(7) ? null : l.GetInt64(7),
                ResponsavelNome: l.IsDBNull(8) ? null : l.GetString(8),
                NegociacaoId: l.IsDBNull(5) ? null : l.GetInt64(5),
                PipelineNome: l.IsDBNull(9) ? null : l.GetString(9),
                EtapaNome: l.IsDBNull(10) ? null : l.GetString(10),
                Valor: l.IsDBNull(6) ? null : l.GetDecimal(6),
                ParadoDesde: paradoDesde,
                // A conta fica no C#, sobre a data que voltou: `now()` dentro do SQL faria o corte
                // virar função sobre coluna, que é o que o UNION acima existe para evitar.
                DiasParado: DiasEntre(paradoDesde, hojeLocal, fuso),
                MesesParado: MesesCompletos.Entre(DiaLocal(paradoDesde, fuso), hojeLocal),
                MotivoPerda: perdidos && !l.IsDBNull(12) ? l.GetString(12) : null,
                Etiquetas: LerEtiquetas(l.GetString(13))));
        }, ct);

        // ===================== A PÁGINA ALÉM DO FIM (AUD-XX, B5) =====================
        // O total vem de `COUNT(*) OVER ()`, lido de dentro das linhas — e uma página além do fim
        // não tem linha nenhuma: o total saía 0, e a tela dizia "nada aqui" com as páginas
        // anteriores cheias. A pergunta é refeita na MESMA consulta, do início e com uma linha só:
        // os filtros são os mesmos por construção, e o caso comum não paga nada a mais.
        // =============================================================================
        if (itens.Count == 0 && pagina > 1)
        {
            await LerAsync(sql, Parametros(1, 0), l => { total = (int)l.GetInt64(0); }, ct);
        }

        return PaginaComTotal<LeadParado>.De(itens, total, pagina, tamanho);
    }

    private const NpgsqlDbType NpsqlBigint = NpgsqlDbType.Bigint;

    private static readonly JsonSerializerOptions JsonDoBanco = new() { PropertyNameCaseInsensitive = true };

    /// <summary>O `json_agg` da consulta, ja em ordem de nome. `[]` quando nao ha negocio ou etiqueta.</summary>
    private static IReadOnlyList<EtiquetaDto> LerEtiquetas(string json) =>
        JsonSerializer.Deserialize<List<EtiquetaDto>>(json, JsonDoBanco) ?? [];

    /// <summary>`DBNull` COM TIPO DECLARADO. Sem o `NpgsqlDbType` o driver manda `unknown` e o
    /// Postgres nao consegue resolver `$6::bigint IS NULL` — o erro sai como "could not determine
    /// data type" e a consulta inteira falha por causa de um filtro que nem estava em uso. Mesmo
    /// ajudante e mesma razao do `Juncao.Nulavel` do `ServicoRelatorios`.</summary>
    private static NpgsqlParameter Nulavel(object? valor, NpgsqlDbType tipo) =>
        new() { Value = valor ?? DBNull.Value, NpgsqlDbType = tipo };

    public async Task<ResultadoEmLote> CriarLembretesAsync(
        LembreteEmLote pedido, CancellationToken ct)
    {
        // ⚠️ `Exigir` E NAO UM `[Authorize]` NO CONTROLLER. A rota de LISTAGEM nao tem guarda de
        // proposito — ver nao e agir —, entao a trava precisa ser da acao, nao do caminho.
        contexto.Exigir(Permissao.AgirEmLote,
            "Você não pode agir sobre vários leads de uma vez. Peça ao dono.");

        var titulo = (pedido.Titulo ?? "").Trim();
        if (titulo.Length == 0) throw new RegraDeNegocioException("Dê um título ao lembrete.");

        // Lista nula vale como vazia, como no `ReabrirAsync` (revisao LPA-1). Pela API a validacao
        // implicita do ASP.NET ja recusa a lista ausente com 400; isto e para quem chama o servico
        // direto, que recebia um `NullReferenceException` no lugar de "nada a fazer".
        var ids = (pedido.ContatoIds ?? []).Distinct().ToList();
        if (ids.Count == 0) return new ResultadoEmLote(0, 0, 0);

        if (ids.Count > JanelasDeParada.TamanhoMaximoPagina)
            throw new RegraDeNegocioException(
                $"Selecione no máximo {JanelasDeParada.TamanhoMaximoPagina} leads por vez.");

        // ⚠️ "HOJE" NO FUSO DA EMPRESA, e nao em UTC (revisao LPA-1). Era `GetUtcNow().UtcDateTime`, e
        // as 22h de Brasilia o servidor em UTC ja esta no dia seguinte: o lembrete "para hoje" era
        // recusado como "no passado" toda noite, das 21h a meia-noite. O resto deste arquivo ja
        // corta pelo `FusoAsync` — so este ponto tinha ficado para tras.
        var hoje = DateOnly.FromDateTime(FusoDeNegocio.AgoraNo(relogio, await FusoAsync(ct)));
        if (pedido.DataAlvo < hoje)
            throw new RegraDeNegocioException("A data do lembrete não pode ser no passado.");

        // ⚠️ O FILTRO GLOBAL DE EMPRESA VALE AQUI, e e o que impede um id de outra empresa de
        // entrar pela lista que o cliente monta: ele simplesmente nao volta desta consulta.
        // Ver `SoOsProprios`: com negocio aberto, o dono e o do negocio; sem, o do contato — ou o do
        // negocio perdido, que e como a aba Perdidos mostra.
        var soMeus = SoOsProprios();

        var alvos = await db.Contatos.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.AnonimizadoEm == null
                     && (soMeus == null
                         || c.Negociacoes.Any(n => n.Status == StatusNegociacao.Aberta
                                                && n.ResponsavelId == soMeus)
                         || (!c.Negociacoes.Any(n => n.Status == StatusNegociacao.Aberta)
                             && (c.ResponsavelId == soMeus
                                 || c.Negociacoes.Any(n => n.Status == StatusNegociacao.Perdida
                                                        && n.ResponsavelId == soMeus)))))
            .Select(c => new
            {
                c.Id,
                ConversaId = db.Conversas.Where(v => v.ContatoId == c.Id)
                    .Where(RegrasConversa.Principal)
                    .Select(v => (long?)v.Id).FirstOrDefault(),
                // O dono do NEGOCIO, nunca o do contato: a `LiberacaoDeCiclo` zera o do contato
                // ao concluir a venda, e a tarefa cairia no Meu Dia de ninguem.
                Responsavel = db.Negociacoes
                    .Where(n => n.ContatoId == c.Id && n.Status == StatusNegociacao.Aberta)
                    .Select(n => n.ResponsavelId).FirstOrDefault(),
                // ⚠️ MESMA REGRA DO MOTOR DE FOLLOW-UP: contato com lembrete pendente nao ganha
                // outro, "senao o vendedor recebe a mesma tarefa todo dia ate fazer".
                JaTem = db.Lembretes.Any(
                    l => l.ContatoId == c.Id && l.Status == StatusLembrete.Pendente)
            })
            .ToListAsync(ct);

        var quemPediu = contexto.UsuarioId == 0 ? (long?)null : contexto.UsuarioId;
        var criados = 0;
        var pulados = 0;

        foreach (var alvo in alvos)
        {
            if (alvo.JaTem) { pulados++; continue; }

            var lembrete = new Lembrete
            {
                EmpresaId = contexto.EmpresaId,
                ContatoId = alvo.Id,
                ConversaId = alvo.ConversaId,
                // MANUAL, nao `Automatico`: `uq_lembrete_teto_diario` so cobre o automatico que
                // envia mensagem, e este nao envia nada. Marcar como automatico o poria num teto
                // que nao e dele e barraria o segundo lote do dia em silencio.
                Origem = OrigemLembrete.Manual,
                Status = StatusLembrete.Pendente,
                DataAlvo = pedido.DataAlvo,
                Titulo = titulo,
                Observacao = pedido.Observacao,
                EnviaMensagem = false,
                ResponsavelId = alvo.Responsavel ?? quemPediu,
                CriadoPor = quemPediu
            };

            db.Lembretes.Add(lembrete);

            // Um registro de trilha POR ENTIDADE, que e o padrao do projeto. Um agregado diria
            // "trinta lembretes criados" e nao responderia "quem mexeu NESTE contato".
            trilha.Declarar(EntidadeAuditada.Contato, alvo.Id, AcaoAuditoria.Criou);

            criados++;
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        // Os que nao voltaram da consulta: id de outra empresa, inexistente, ou anonimizado.
        return new ResultadoEmLote(criados, pulados, ids.Count - alvos.Count);
    }

    /// <summary>===================== O QUE A REATIVACAO RENDEU =====================
    ///
    /// ⚠️ `n.status &lt;&gt; 'cancelada'` NAO E REDUNDANTE COM `ganha_em IS NOT NULL`, e e o erro
    /// mais facil de cometer aqui. `ServicoVendas.CancelarAsync` deixa o `ganha_em` no lugar de
    /// proposito — "o `ganha_em` fica, e quem tira do relatorio e o filtro do indice
    /// (`status &lt;&gt; 'cancelada'`), nao o carimbo em branco". Sem esta linha, uma venda marcada
    /// por engano e desfeita apareceria como reativacao bem-sucedida.
    ///
    /// ⚠️ `ganha_em &gt; ne.criado_em` E A METRICA. Sem a comparacao, um negocio que ja estava
    /// ganho quando alguem colou a etiqueta contaria como reativado por ela.
    ///
    /// A janela e sobre `ne.criado_em` — quando a marca foi colada —, com os cortes sargaveis de
    /// sempre (`&gt;= $3 AND &lt; $4`), sem funcao sobre a coluna.
    ///
    /// ⚠️ `ne.da_reativacao` E A CAMPANHA. So conta a etiqueta colada pela etiqueta em lote desta
    /// tela. A mesma etiqueta posta a mao num card, para organizar o funil, nao e campanha: sem
    /// este filtro, um negocio que nunca esteve parado entrava como "reativado".
    ///
    /// Agrega no SQL com `FILTER`, e nao em memoria: sao tres numeros, e trazer as linhas para
    /// contar em C# e o que o teste `TODA_CONSULTA_QUE_AGREGA_AGREGA_NO_SQL` proibe.
    /// ==============================================================</summary>
    private const string SqlReativacao = """
        SELECT COUNT(*)                                          AS marcados,
               COUNT(*) FILTER (WHERE x.ganhou)                  AS ganhos,
               COALESCE(SUM(x.valor) FILTER (WHERE x.ganhou), 0) AS valor_ganho
          FROM (
            SELECT n.valor,
                   (n.ganha_em IS NOT NULL
                    AND n.ganha_em > ne.criado_em
                    AND n.status <> 'cancelada') AS ganhou
              FROM negociacoes_etiquetas ne
              JOIN negociacoes n
                ON n.id = ne.negociacao_id
               AND n.empresa_id = ne.empresa_id
             WHERE ne.empresa_id = $1
               AND ne.etiqueta_id = $2
               AND ne.da_reativacao
               AND ne.criado_em >= $3
               AND ne.criado_em < $4
               AND ($5::bigint IS NULL OR n.responsavel_id = $5)
          ) x
        """;

    public async Task<Reativacao> ReativacaoAsync(
        FiltroReativacao filtro, CancellationToken ct)
    {
        if (filtro.Ate < filtro.De)
            throw new RegraDeNegocioException("A data final não pode ser antes da inicial.");

        var fuso = await FusoAsync(ct);

        // A janela fecha no FIM do dia `Ate`: `< meia-noite do dia seguinte`. Com `<= Ate` em
        // timestamp, tudo que foi marcado durante o ultimo dia ficaria de fora.
        var de = TimeZoneInfo.ConvertTimeToUtc(filtro.De.ToDateTime(TimeOnly.MinValue), fuso);
        var ate = TimeZoneInfo.ConvertTimeToUtc(
            filtro.Ate.AddDays(1).ToDateTime(TimeOnly.MinValue), fuso);

        NpgsqlParameter[] parametros =
        [
            new() { Value = contexto.EmpresaId },                      // $1
            new() { Value = filtro.EtiquetaId },                       // $2
            new() { Value = de },                                      // $3
            new() { Value = ate },                                     // $4
            Nulavel(ResponsavelEfetivo(filtro.ResponsavelId), NpsqlBigint)  // $5
        ];

        var saida = new Reativacao(0, 0, 0, null);

        await LerAsync(SqlReativacao, parametros, l =>
        {
            var marcados = (int)l.GetInt64(0);
            var ganhos = (int)l.GetInt64(1);

            saida = new Reativacao(marcados, ganhos, l.GetDecimal(2), Percentual.De(ganhos, marcados));
        }, ct);

        return saida;
    }

    public async Task<ResultadoEmLote> AplicarEtiquetaAsync(
        EtiquetaEmLote pedido, CancellationToken ct)
    {
        contexto.Exigir(Permissao.AgirEmLote,
            "Você não pode agir sobre vários leads de uma vez. Peça ao dono.");

        // Lista nula vale como vazia — ver `CriarLembretesAsync`.
        var ids = (pedido.NegociacaoIds ?? []).Distinct().ToList();
        if (ids.Count == 0) return new ResultadoEmLote(0, 0, 0);

        if (ids.Count > JanelasDeParada.TamanhoMaximoPagina)
            throw new RegraDeNegocioException(
                $"Selecione no máximo {JanelasDeParada.TamanhoMaximoPagina} leads por vez.");

        // O filtro global recorta: etiqueta de outra empresa nao aparece, e a mensagem e a mesma
        // de `ServicoEtiquetas` — "nao existe mais" cobre apagada e de outro tenant sem vazar qual.
        var existe = await db.Etiquetas.AsNoTracking()
            .AnyAsync(e => e.Id == pedido.EtiquetaId, ct);

        if (!existe) throw new RegraDeNegocioException("Essa etiqueta não existe mais.");

        // ⚠️ SO ABERTA. Marcar um negocio ganho ou perdido diria que ele foi reativado hoje, e a
        // metrica compararia `criado_em` com um `ganha_em` que e anterior.
        // Ver `SoOsProprios`: quem nao ve a equipe so etiqueta o negocio que e dele.
        var soMeus = SoOsProprios();

        var alvos = await db.Negociacoes.AsNoTracking()
            .Where(n => ids.Contains(n.Id) && n.Status == StatusNegociacao.Aberta
                     && (soMeus == null || n.ResponsavelId == soMeus))
            .Select(n => new
            {
                n.Id,
                n.ContatoId,
                JaTem = db.NegociacoesEtiquetas
                    .Any(x => x.NegociacaoId == n.Id && x.EtiquetaId == pedido.EtiquetaId),
                Quantas = db.NegociacoesEtiquetas.Count(x => x.NegociacaoId == n.Id)
            })
            .ToListAsync(ct);

        var quemPediu = contexto.UsuarioId == 0 ? (long?)null : contexto.UsuarioId;
        var criados = 0;
        var pulados = 0;

        foreach (var alvo in alvos)
        {
            // Quem ja tem a etiqueta fica como esta: reinserir perderia o `criado_em` do primeiro
            // dia, que e exatamente a data que a metrica de reativados le.
            // E o card no teto de oito e pulado, nao derruba o lote: recusar a chamada inteira
            // faria o operador perder os outros quarenta e nove.
            if (alvo.JaTem || alvo.Quantas >= ServicoEtiquetas.MaximoPorNegociacao)
            {
                pulados++;
                continue;
            }

            // A marca da CAMPANHA: e so ela que o resultado da reativacao conta.
            db.NegociacoesEtiquetas.Add(new NegociacaoEtiqueta
            {
                EmpresaId = contexto.EmpresaId,
                NegociacaoId = alvo.Id,
                EtiquetaId = pedido.EtiquetaId,
                CriadoPor = quemPediu,
                DaReativacao = true
            });

            // Um registro por ENTIDADE, o padrao do projeto. A entidade auditada e o contato,
            // igual ao lembrete em lote: a trilha e lida por pessoa.
            trilha.Declarar(EntidadeAuditada.Contato, alvo.ContatoId, AcaoAuditoria.Editou);

            criados++;
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        // Os que nao voltaram: id de outra empresa, inexistente, ou negocio que nao esta aberto.
        return new ResultadoEmLote(criados, pulados, ids.Count - alvos.Count);
    }

    /// <summary>===================== AS TRES COLUNAS DE DONO, NA MESMA TRANSACAO =====================
    ///
    /// O porque de serem tres esta em `RedistribuicaoEmLote`. Aqui o que importa e que as tres
    /// mudam JUNTAS: meia redistribuicao e pior que nenhuma, porque a tela de leads parados diria
    /// Ana e a caixa diria Bruno, e nada na interface explicaria a diferenca.
    ///
    /// ⚠️ `AtribuidoEm` ACOMPANHA `ResponsavelId` NA CONVERSA. Dono sem data de atribuicao e um
    /// estado que o semeador ja documenta nao existir (`AtribuidoEm = contato.ResponsavelId is
    /// null ? null : ...`), e a caixa usa a data para ordenar o que cada um assumiu.
    /// ==========================================================================================</summary>
    public async Task<ResultadoEmLote> RedistribuirAsync(
        RedistribuicaoEmLote pedido, CancellationToken ct)
    {
        contexto.Exigir(Permissao.AgirEmLote,
            "Você não pode agir sobre vários leads de uma vez. Peça ao dono.");

        // Lista nula vale como vazia — ver `CriarLembretesAsync`.
        var ids = (pedido.NegociacaoIds ?? []).Distinct().ToList();
        if (ids.Count == 0) return new ResultadoEmLote(0, 0, 0);

        if (ids.Count > JanelasDeParada.TamanhoMaximoPagina)
            throw new RegraDeNegocioException(
                $"Selecione no máximo {JanelasDeParada.TamanhoMaximoPagina} leads por vez.");

        // ⚠️ ATIVO, NAO SO EXISTENTE. Atribuir a quem foi desativado esconde o lead de todos: ele
        // nao aparece na lista de responsaveis que as telas oferecem, e ninguem mais o ve na
        // propria carteira. O filtro global cobre a empresa; o status e a parte que falta.
        if (pedido.ResponsavelId is { } alvo)
        {
            var ativo = await db.Usuarios.AsNoTracking()
                .AnyAsync(u => u.Id == alvo && u.Status == StatusUsuario.Ativo, ct);

            if (!ativo)
                throw new RegraDeNegocioException(
                    "Escolha alguém da equipe que esteja ativo.");
        }

        // ⚠️ SO ABERTA, como a etiqueta em lote. A consulta pegava negociacao de QUALQUER estado, e
        // a aba Perdidos manda o id do negocio PERDIDO: "Mudar responsavel" ali reescrevia o dono de
        // perdas antigas — e, pela API, de vendas ja fechadas. Os relatorios atribuem por essa
        // coluna, entao as perdas da Ana viravam do Bruno e a venda de tres meses atras mudava de
        // credito. E sobrescrevia o dono do contato que a `LiberacaoDeCiclo` tinha zerado.
        //
        // O que nao esta aberto conta como "nao encontrado" no resultado, como na etiqueta.
        // ⚠️ E QUEM NAO VE A EQUIPE SO REDISTRIBUI O QUE E DELE (ver `SoOsProprios`). Sem isto, o
        // vendedor com o gesto delegado tomava a carteira de um colega mandando os ids dela.
        var soMeus = SoOsProprios();

        var alvos = await db.Negociacoes
            .Where(n => ids.Contains(n.Id) && n.Status == StatusNegociacao.Aberta
                     && (soMeus == null || n.ResponsavelId == soMeus))
            .ToListAsync(ct);

        var mudados = 0;
        var pulados = 0;

        // ⚠️ COLETADO DENTRO DO LACO, SO PARA QUEM MUDOU. Montar a lista depois, filtrando por
        // "ja e do alvo", traria tambem os PULADOS — e reescrever o `AtribuidoEm` deles mudaria a
        // ordem da caixa de um lead que ninguem tocou.
        var contatosAfetados = new HashSet<long>();

        foreach (var negociacao in alvos)
        {
            // Quem ja e do alvo nao conta como trabalho: e o numero que explica "marquei quinze,
            // mudaram doze" sem mandar o operador procurar defeito.
            if (negociacao.ResponsavelId == pedido.ResponsavelId) { pulados++; continue; }

            var antes = negociacao.ResponsavelId;

            negociacao.ResponsavelId = pedido.ResponsavelId;

            // O VALOR ANTIGO E O NOVO NA TRILHA: "quem mexeu neste lead" sem o de/para nao
            // responde a pergunta que se faz depois — para QUEM ele foi.
            trilha.Declarar(
                EntidadeAuditada.Contato, negociacao.ContatoId, AcaoAuditoria.Atribuiu,
                new Dictionary<string, AlteracaoValor>
                {
                    ["responsavel"] = new(antes, pedido.ResponsavelId)
                });

            contatosAfetados.Add(negociacao.ContatoId);
            mudados++;
        }

        // ===================== AS TRES COLUNAS NUM SAVECHANGES SO =====================
        // As outras duas sao por CONTATO: a negociacao e do negocio, estas sao da pessoa.
        //
        // ⚠️ ERAM TRES ESCRITAS SEPARADAS — o `SaveChanges` das negociacoes e dois `ExecuteUpdate`
        // —, cada uma com o proprio commit, e o comentario do metodo prometia "na mesma transacao"
        // sem que houvesse transacao nenhuma. Uma falha na segunda ou na terceira (timeout,
        // deadlock, requisicao cancelada) deixava o negocio com a Ana e o contato e a caixa com o
        // Bruno: a redistribuicao pela metade que este metodo diz ser pior que nenhuma.
        //
        // Agora as entidades sao CARREGADAS e o `SaveChanges` e um so. O EF o embrulha numa
        // transacao — e, dentro de uma que ja exista, num savepoint que ele desfaz se algo falhar.
        // Ou mudam as tres, ou nenhuma. De quebra, `atualizado_em` passa a ser carimbado nos dois:
        // o `ExecuteUpdate` passava por fora do interceptor. O lote tem no maximo 50 leads, entao
        // carregar custa duas consultas.
        // ============================================================================
        if (contatosAfetados.Count > 0)
        {
            var contatosDoLote = await db.Contatos
                .Where(c => contatosAfetados.Contains(c.Id))
                .ToListAsync(ct);

            foreach (var c in contatosDoLote) c.ResponsavelId = pedido.ResponsavelId;

            var conversasDoLote = await db.Conversas
                .Where(v => contatosAfetados.Contains(v.ContatoId))
                .ToListAsync(ct);

            var agora = relogio.GetUtcNow().UtcDateTime;

            foreach (var v in conversasDoLote)
            {
                v.ResponsavelId = pedido.ResponsavelId;
                v.AtribuidoEm = pedido.ResponsavelId == null ? null : agora;
            }
        }

        await db.SaveChangesAsync(ct);

        db.ChangeTracker.Clear();

        // Os que nao voltaram: id de outra empresa, inexistente, ou negocio que nao esta aberto.
        return new ResultadoEmLote(mudados, pulados, ids.Count - alvos.Count);
    }

    /// <summary>===================== REABRIR EM LOTE DELEGA =====================
    ///
    /// ⚠️ CHAMA `IServicoContatos.AbrirNegociacaoAsync` UM POR UM, de proposito, e isto nao e
    /// ingenuidade de desempenho: aquele metodo carrega a precedencia de funil, a etapa
    /// preservada, o `vendas` nao ser tocado, a trilha e o `lead.movido` do webhook. Uma segunda
    /// implementacao em lote seria a segunda porta para o mesmo fato.
    ///
    /// ⚠️ `SaveChanges` E DE LA, UM POR ITEM, e por isso um conflito no quinto nao desfaz os
    /// quatro primeiros. A recusa de `AbrirNegociacaoAsync` acontece ANTES de qualquer `Add`, o
    /// que mantem o rastreador limpo para a volta seguinte — conferido lendo o metodo, nao
    /// suposto.
    ///
    /// ⚠️ CONFLITO E `Pulados`. Quem ja tem negocio em todos os funis volta 409 ali; aqui e um
    /// item que nao deu. Abortar faria o operador perder o lote por causa de um contato.
    /// ==============================================================</summary>
    public async Task<ResultadoEmLote> ReabrirAsync(
        IReadOnlyList<long> contatoIds, CancellationToken ct)
    {
        contexto.Exigir(Permissao.AgirEmLote,
            "Você não pode agir sobre vários leads de uma vez. Peça ao dono.");

        var ids = contatoIds.Distinct().ToList();
        if (ids.Count == 0) return new ResultadoEmLote(0, 0, 0);

        if (ids.Count > JanelasDeParada.TamanhoMaximoPagina)
            throw new RegraDeNegocioException(
                $"Selecione no máximo {JanelasDeParada.TamanhoMaximoPagina} leads por vez.");

        var criados = 0;
        var pulados = 0;
        var falhou = 0;

        // Ver `SoOsProprios`: quem nao ve a equipe so reabre a perda que e dele — a mesma regra que
        // monta a aba Perdidos dele (o dono do negocio PERDIDO). O resto conta como nao encontrado.
        var soMeus = SoOsProprios();

        if (soMeus != null)
        {
            var dele = await db.Contatos.AsNoTracking()
                .Where(c => ids.Contains(c.Id)
                         && c.Negociacoes.Any(n => n.Status == StatusNegociacao.Perdida
                                                && n.ResponsavelId == soMeus))
                .Select(c => c.Id)
                .ToListAsync(ct);

            falhou += ids.Count - dele.Count;
            ids = dele;
        }

        // ⚠️ O ANONIMIZADO FALHA, NAO "PULA" (revisao LPA-1). `AbrirNegociacaoAsync` o recusa com
        // `conflito: true` — que e a recusa certa para quem tenta editar a ficha —, e aqui o
        // conflito vira "pulado", que a tela explica como "ja tem negocio em todos os funis". O
        // contato anonimizado nao tem negocio em funil nenhum: ele nao pode mais ser reaberto.
        var anonimizados = await db.Contatos.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.AnonimizadoEm != null)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (anonimizados.Count > 0)
        {
            falhou += anonimizados.Count;
            ids = ids.Except(anonimizados).ToList();
        }

        foreach (var id in ids)
        {
            try
            {
                // `null` no funil: deixa a precedencia de la decidir, que para uma perda e
                // reviver no proprio funil, na etapa onde ela morreu.
                await contatos.AbrirNegociacaoAsync(id, null, ct);
                criados++;
            }
            catch (RegraDeNegocioException e) when (e.Conflito)
            {
                // Ja ha negocio aberto, ou nao ha funil livre: nao deu, e nao e erro do lote.
                pulados++;
            }
            catch (RegraDeNegocioException)
            {
                // Contato inexistente ou de outra empresa.
                falhou++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ⚠️ ERRO INESPERADO NUM ITEM NAO DERRUBA O LOTE (revisao LPA-1). Uma corrida no
                // `uq_negociacoes_card_por_funil` ou uma falha de gravacao no quinto item devolvia
                // 500 com os quatro primeiros JA reabertos — e o operador ficava sem saber quantos
                // foram. Agora conta como falha, vai para o log, e o rastreador e LIMPO: a entidade
                // que nao gravou ficaria pendurada e entraria no `SaveChanges` do item seguinte.
                log.LogWarning(ex, "Reabrir em lote: o contato {Id} falhou.", id);
                db.ChangeTracker.Clear();
                falhou++;
            }
        }

        db.ChangeTracker.Clear();

        return new ResultadoEmLote(criados, pulados, falhou);
    }

    /// <summary>O fuso do negocio. Num lugar so porque duas leituras desta tela cortam o tempo —
    /// a janela de dias parados e a janela da metrica — e as duas tem de cortar no MESMO
    /// meia-noite, senao a lista e o numero discordam no mesmo dia.</summary>
    private async Task<TimeZoneInfo> FusoAsync(CancellationToken ct)
    {
        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new { e.FusoHorario })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Empresa não encontrada.");

        return FusoDeNegocio.Resolver(empresa.FusoHorario);
    }

    private static int DiasEntre(DateTime paradoDesdeUtc, DateOnly hojeLocal, TimeZoneInfo fuso)
    {
        var local = DiaLocal(paradoDesdeUtc, fuso);

        return Math.Max(0, hojeLocal.DayNumber - local.DayNumber);
    }

    /// <summary>O dia, no fuso da empresa, de um instante em UTC.</summary>
    private static DateOnly DiaLocal(DateTime instanteUtc, TimeZoneInfo fuso) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(instanteUtc, DateTimeKind.Utc), fuso));

    /// <summary>Mesma linha de corte do `ServicoRelatorios.ResponsavelEfetivo`, e escrita do mesmo
    /// jeito de propósito: duas formas diferentes da mesma regra divergem no dia em que uma delas
    /// muda. Quem NÃO vê os números da equipe vê só os seus — o parâmetro do cliente é DESCARTADO.
    ///
    /// ⚠️ ATRIBUI POR `negociacoes.responsavel_id`, nunca por `contatos.responsavel_id`. A
    /// `LiberacaoDeCiclo` zera a coluna do contato ao concluir a venda, e foi esse detalhe que
    /// tornou instável a conversão do relatório antigo.</summary>
    private long? ResponsavelEfetivo(long? pedido)
    {
        var soVeOSeu = !contexto.Pode(Permissao.VerNumerosDaEquipe);

        return soVeOSeu ? contexto.UsuarioId : pedido;
    }

    /// <summary>===================== QUEM SO VE O SEU, SO AGE SOBRE O SEU =====================
    ///
    /// ⚠️ AS QUATRO ACOES EM LOTE SO CONFERIAM `AgirEmLote`. A lista recorta por pessoa, mas os ids
    /// chegam do cliente: um vendedor com o gesto delegado — o uso que `Permissoes` descreve — e
    /// sem `VerNumerosDaEquipe` mandava ao `/leads-parados/responsavel` os ids da carteira de um
    /// colega e a tomava inteira. E etiquetava ou criava lembrete em lead que nao podia nem ver. So
    /// o filtro de EMPRESA limitava os ids.
    ///
    /// Agora quem nao ve a equipe so age sobre o que a PROPRIA lista mostraria, pela mesma regra de
    /// dono dela: com negocio aberto, o do negocio; sem, o do contato; na aba Perdidos, o do negocio
    /// perdido. O que nao for dele conta como "nao encontrado" — que e o que ele ve do lado dele.
    ///
    /// Nulo = ve a equipe, sem recorte. Mesma linha de corte do `ResponsavelEfetivo`.
    /// =========================================================================================</summary>
    private long? SoOsProprios() => ResponsavelEfetivo(null);

    /// <summary>Gêmeo do `ServicoRelatorios.LerAsync` e do `ServicoEvolucao.LerAsync`: encanamento,
    /// não regra. A linha da transação se denuncia sozinha — sem ela, todo teste de integração
    /// deixa de ver as próprias linhas na primeira execução.</summary>
    private async Task LerAsync(
        string sql, NpgsqlParameter[] parametros, Action<NpgsqlDataReader> ler, CancellationToken ct)
    {
        var conexao = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conexao.State != ConnectionState.Open) await conexao.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conexao);

        cmd.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.Parameters.AddRange(parametros);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct)) ler(leitor);
    }

    /// <summary>===================== EXPOSTA PARA O TESTE LER =====================
    /// A auditoria que varre as consultas atrás de função sobre coluna dentro de um `WHERE` lê uma
    /// LISTA MANUAL. Serviço novo fora dela fica sem rede e o teste continua verde.
    ///
    /// ⚠️ E ELA NÃO BASTA AQUI. `COALESCE` não está entre os tokens proibidos, então a primeira
    /// versão desta consulta passaria na auditoria descartando o índice. O que guarda esta regra é
    /// o comentário do `SqlParados` e o teste de desempenho ao lado dos de comportamento.
    /// ==================================================================</summary>
    public static IReadOnlyList<(string Nome, string Sql)> ConsultasParaAuditoria =>
    [
        ("leads parados", SqlParados),
        ("leads perdidos", SqlPerdidos),
        ("reativacao", SqlReativacao)
    ];
}
