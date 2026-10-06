using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    NexoraDbContext db, IContextoEmpresa contexto, TimeProvider relogio, ColetorAuditoria trilha)
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
            SELECT cv.contato_id, cv.ultima_mensagem_em AS parado_desde
              FROM conversas cv
             WHERE cv.empresa_id = $2
               AND cv.ultima_mensagem_em < $1
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
               n.id, n.valor, n.responsavel_id,
               u.nome, pi.nome, et.nome,
               e.parado_desde
          FROM elegiveis e
          JOIN contatos c ON c.id = e.contato_id
          LEFT JOIN negociacoes n ON n.contato_id = c.id AND n.status = 'aberta'
          LEFT JOIN usuarios u    ON u.id = n.responsavel_id
          LEFT JOIN pipelines pi  ON pi.id = n.pipeline_id
          LEFT JOIN etapas_funil et ON et.id = n.etapa_id
         WHERE ($3::bigint IS NULL OR n.responsavel_id = $3)
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

    public async Task<PaginaLeadsParados> ListarAsync(FiltroLeadsParados filtro, CancellationToken ct)
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

        NpgsqlParameter[] parametros =
        [
            new() { Value = limite },                                           // $1
            new() { Value = contexto.EmpresaId },                               // $2
            new() { Value = (object?)recorte ?? DBNull.Value,
                    NpgsqlDbType = NpgsqlDbType.Bigint },                       // $3
            new() { Value = tamanho },                                          // $4
            new() { Value = (pagina - 1) * tamanho },                           // $5
            Nulavel(filtro.PipelineId, NpsqlBigint),                            // $6
            Nulavel(filtro.EtapaId, NpsqlBigint),                               // $7
            Nulavel(filtro.Origem, NpgsqlDbType.Text),                          // $8
            Nulavel(filtro.EtiquetaId, NpsqlBigint),                            // $9
            Nulavel(filtro.ValorMin, NpgsqlDbType.Numeric),                     // $10
            Nulavel(filtro.ValorMax, NpgsqlDbType.Numeric)                      // $11
        ];

        var itens = new List<LeadParado>();
        var total = 0;

        await LerAsync(SqlParados, parametros, l =>
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
                DiasParado: DiasEntre(paradoDesde, hojeLocal, fuso)));
        }, ct);

        return new PaginaLeadsParados(itens, total);
    }

    private const NpgsqlDbType NpsqlBigint = NpgsqlDbType.Bigint;

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

        var ids = pedido.ContatoIds.Distinct().ToList();
        if (ids.Count == 0) return new ResultadoEmLote(0, 0, 0);

        if (ids.Count > JanelasDeParada.TamanhoMaximoPagina)
            throw new RegraDeNegocioException(
                $"Selecione no máximo {JanelasDeParada.TamanhoMaximoPagina} leads por vez.");

        var hoje = DateOnly.FromDateTime(relogio.GetUtcNow().UtcDateTime);
        if (pedido.DataAlvo < hoje)
            throw new RegraDeNegocioException("A data do lembrete não pode ser no passado.");

        // ⚠️ O FILTRO GLOBAL DE EMPRESA VALE AQUI, e e o que impede um id de outra empresa de
        // entrar pela lista que o cliente monta: ele simplesmente nao volta desta consulta.
        var alvos = await db.Contatos.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.AnonimizadoEm == null)
            .Select(c => new
            {
                c.Id,
                ConversaId = db.Conversas.Where(v => v.ContatoId == c.Id)
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

        var saida = new Reativacao(0, 0, 0);

        await LerAsync(SqlReativacao, parametros, l =>
            saida = new Reativacao(
                (int)l.GetInt64(0), (int)l.GetInt64(1), l.GetDecimal(2)), ct);

        return saida;
    }

    public async Task<ResultadoEmLote> AplicarEtiquetaAsync(
        EtiquetaEmLote pedido, CancellationToken ct)
    {
        contexto.Exigir(Permissao.AgirEmLote,
            "Você não pode agir sobre vários leads de uma vez. Peça ao dono.");

        var ids = pedido.NegociacaoIds.Distinct().ToList();
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
        var alvos = await db.Negociacoes.AsNoTracking()
            .Where(n => ids.Contains(n.Id) && n.Status == StatusNegociacao.Aberta)
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

            db.NegociacoesEtiquetas.Add(new NegociacaoEtiqueta
            {
                EmpresaId = contexto.EmpresaId,
                NegociacaoId = alvo.Id,
                EtiquetaId = pedido.EtiquetaId,
                CriadoPor = quemPediu
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
        var local = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(paradoDesdeUtc, DateTimeKind.Utc), fuso));

        return Math.Max(0, hojeLocal.DayNumber - local.DayNumber);
    }

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
        ("reativacao", SqlReativacao)
    ];
}
