using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Nexora.Core;
using Nexora.Core.Nps;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== O RELATORIO DA PESQUISA =====================
///
/// Servico proprio e nao mais um metodo no `ServicoRelatorios`, que ja passa de mil linhas: o que
/// os dois compartilham e o FILTRO e a `Comparacao`, e os dois sao reusados aqui. A consulta nao
/// tem nada em comum com as de venda.
///
/// Ver `IServicoRelatorioNps` para a decisao do eixo (`data_envio`, a coorte) e o preco dela.
/// ================================================================</summary>
public class ServicoRelatorioNps(
    NexoraDbContext db, IContextoEmpresa contexto, TimeProvider relogio) : IServicoRelatorioNps
{
    /// <summary>===================== OS CONTADORES DO PERIODO =====================
    ///
    /// ⚠️ TUDO AGREGA NO SQL, com `FILTER` — e o que `TODA_CONSULTA_QUE_AGREGA_AGREGA_NO_SQL` cobra.
    /// Trazer as linhas para contar em C# seria o mesmo numero com um custo que cresce com o tenant.
    ///
    /// ⚠️ O CORTE E SARGAVEL: `data_envio >= $2 AND < $3`, sem funcao sobre a coluna. O instante vem
    /// pronto de quem chama, convertido no fuso da empresa.
    ///
    /// ⚠️ `data_envio IS NOT NULL` E IMPLICITO no corte, e isso IMPORTA: pesquisa `agendada` e
    /// pesquisa cancelada ANTES de sair tem `data_envio` nulo e nao entram em nada. A coorte e do
    /// que de fato chegou ao cliente.
    ///
    /// ⚠️ `EXISTS` E NAO `JOIN`, E A ESCOLHA FOI MEDIDA. A negociacao e tocada so para o recorte por
    /// pessoa — a atribuicao mora lá, nunca em `contatos.responsavel_id`, que a `LiberacaoDeCiclo`
    /// zera ao concluir a venda. Com `JOIN` o Postgres visita a negociacao de CADA pesquisa ainda
    /// quando o filtro e nulo, que e o caso do dono e de todo gestor:
    ///
    ///   JOIN,   responsavel nulo:   5,3 ms   6149 buffers
    ///   EXISTS, responsavel nulo:   0,75 ms   143 buffers   ← sete vezes menos
    ///   JOIN,   responsavel dado:  19,5 ms    886 buffers
    ///   EXISTS, responsavel dado:  18,4 ms   1542 buffers   ← empate
    ///
    /// Com `$4` nulo o `OR` curto-circuita e a negociacao nao e aberta. Medido em 80 mil pesquisas
    /// (dois tenants de 40 mil, um ano cada) num clone do `nexora_dev`.
    ///
    /// ⚠️ E AS DUAS FORMAS CONTAM O MESMO — conferido, 3000 = 3000. A FK composta
    /// `fk_pesquisas_nps_negociacao` e NOT NULL, entao o join nunca descartava linha; trocar por
    /// semi-juncao nao muda numero nenhum. Fosse a FK anulavel, trocaria.
    /// ====================================================================</summary>
    private const string SqlTotais = """
        SELECT COUNT(*)                                                     AS enviadas,
               COUNT(*) FILTER (WHERE p.status = 'respondida')              AS respondidas,
               COUNT(*) FILTER (WHERE p.status = 'expirada')                AS expiradas,
               COUNT(*) FILTER (WHERE p.status = 'cancelada')               AS canceladas,
               COUNT(*) FILTER (WHERE p.status IN ('enviada', 'possivel_nota')) AS abertas,
               COUNT(*) FILTER (WHERE p.status = 'respondida' AND p.nota >= 9) AS promotores,
               COUNT(*) FILTER (WHERE p.status = 'respondida'
                                  AND p.nota BETWEEN 7 AND 8)               AS neutros,
               COUNT(*) FILTER (WHERE p.status = 'respondida' AND p.nota <= 6) AS detratores
          FROM pesquisas_nps p
         WHERE p.empresa_id = $1
           AND p.data_envio >= $2
           AND p.data_envio < $3
           AND ($4::bigint IS NULL OR EXISTS (
                 SELECT 1 FROM negociacoes n
                  WHERE n.id = p.negociacao_id
                    AND n.empresa_id = p.empresa_id
                    AND n.responsavel_id = $4))
        """;

    /// <summary>A distribuicao, so do periodo atual — o grafico de barras nao se compara com o
    /// anterior.
    ///
    /// ⚠️ O `GROUP BY` DEVOLVE SO AS NOTAS QUE APARECERAM, e e por isso que o C# completa as onze:
    /// omitir a nota 4 porque ninguem a deu faria as barras mudarem de posicao entre dois periodos,
    /// e o leitor compararia barras diferentes acreditando que sao a mesma.</summary>
    private const string SqlDistribuicao = """
        SELECT p.nota, COUNT(*) AS quantas
          FROM pesquisas_nps p
         WHERE p.empresa_id = $1
           AND p.data_envio >= $2
           AND p.data_envio < $3
           AND p.status = 'respondida'
           AND p.nota IS NOT NULL
           AND ($4::bigint IS NULL OR EXISTS (
                 SELECT 1 FROM negociacoes n
                  WHERE n.id = p.negociacao_id
                    AND n.empresa_id = p.empresa_id
                    AND n.responsavel_id = $4))
         GROUP BY p.nota
        """;

    /// <summary>===================== AS RESPOSTAS, UMA A UMA (NPS-1 3.3) =====================
    ///
    /// ⚠️ O MESMO EIXO (`data_envio`) E O MESMO RECORTE do cartao. Sem faixa, a lista de setembro tem
    /// exatamente `Respondidas` linhas; com "promotor", exatamente `Promotores`. Ha teste disso.
    ///
    /// ⚠️ "COMPRA" E `ganha` OU `concluida`. A negociacao cancelada guarda o `ganha_em` (medido no
    /// LPA-1: 5 contra 10 contra 15), e conta-la faria quem desistiu aparecer como quem voltou.
    ///
    /// "Comprou de novo" e `ganha_em` ESTRITAMENTE maior que o da compra avaliada — o que ja exclui
    /// a propria compra, sem precisar de `id <>`. Havia os dois; com os dois, sabotar um nao
    /// derrubaria nada, e o redundante apodreceria sem ninguem saber.
    ///
    /// ⚠️ "SEM RETORNO" E MENSAGEM HUMANA, `origem = 'humana'`. O agradecimento automatico ao
    /// detrator e uma saida da empresa, e nao e retorno de ninguem: conta-lo tiraria da lista
    /// justamente quem so recebeu o robo. A conversa e UMA por contato (`uq_conversas_contato`), e
    /// e por ela que se chega nas mensagens — `ix_msg_timeline` comeca em `empresa_id, conversa_id`,
    /// e `mensagens` nao tem indice por contato.
    ///
    /// O periodo vem SEMPRE preenchido, sem `IS NULL OR`: o atalho, que ignora a barra, manda limites
    /// largos fixos. Assim o corte continua sargavel nos dois caminhos.
    ///
    /// ⚠️ A EMPRESA E BARRADA UMA VEZ, em `p.empresa_id = $1` — a que usa o indice. O join em
    /// `contatos` tinha um `ct.empresa_id = $1` a mais, e com ele a sabotagem do filtro de verdade
    /// nao derrubava teste nenhum: o join segurava a outra empresa sozinho, e a linha que importa
    /// ficava sem guarda. A FK composta `fk_pesquisas_nps_contato` ja garante que o contato da
    /// pesquisa e da mesma empresa. Os `c2.empresa_id = $1` das subconsultas FICAM: estao la pelo
    /// indice `ix_negociacoes_contato (empresa_id, contato_id)`, nao como protecao.
    ///
    /// `COUNT(*) OVER ()` traz o total na mesma ida, como em `ServicoRelatorios.SqlRecorrentes`.
    /// ======================================================================================</summary>
    private const string SqlRespostas = """
        WITH base AS (
            SELECT p.id, p.contato_id, p.nota, p.data_resposta, p.comentario,
                   n.responsavel_id, n.ganha_em
              FROM pesquisas_nps p
              JOIN negociacoes n ON n.id = p.negociacao_id AND n.empresa_id = p.empresa_id
             WHERE p.empresa_id = $1
               AND p.status = 'respondida'
               AND p.data_envio >= $2
               AND p.data_envio < $3
               AND ($4::bigint IS NULL OR n.responsavel_id = $4)
               AND p.nota >= $5
               AND p.nota <= $6
               AND ($7::boolean = false OR NOT EXISTS (
                     SELECT 1
                       FROM conversas cv
                       JOIN mensagens m ON m.empresa_id = cv.empresa_id AND m.conversa_id = cv.id
                      WHERE cv.empresa_id = p.empresa_id
                        AND cv.contato_id = p.contato_id
                        AND m.direcao = 'saida'
                        AND m.origem = 'humana'
                        AND m.criado_em > p.data_resposta))
        ),
        compras AS (
            SELECT b.*,
                   (SELECT MAX(c2.ganha_em)
                      FROM negociacoes c2
                     WHERE c2.empresa_id = $1
                       AND c2.contato_id = b.contato_id
                       AND c2.status IN ('ganha', 'concluida')) AS ultima_compra_em,
                   (SELECT MIN(c2.ganha_em)
                      FROM negociacoes c2
                     WHERE c2.empresa_id = $1
                       AND c2.contato_id = b.contato_id
                       AND c2.status IN ('ganha', 'concluida')
                       AND c2.ganha_em > b.ganha_em) AS comprou_de_novo_em
              FROM base b
        )
        SELECT x.id, x.contato_id, ct.nome, x.nota, x.data_resposta, x.comentario,
               x.responsavel_id, u.nome, x.ultima_compra_em, x.comprou_de_novo_em,
               COUNT(*) OVER () AS total
          FROM compras x
          JOIN contatos ct ON ct.id = x.contato_id
          LEFT JOIN usuarios u ON u.id = x.responsavel_id AND u.empresa_id = $1
         WHERE ($8::boolean IS NULL OR (x.comprou_de_novo_em IS NOT NULL) = $8)
           AND ($9::timestamptz IS NULL OR x.ultima_compra_em < $9)
         ORDER BY x.data_resposta DESC, x.id DESC
         LIMIT $10 OFFSET $11
        """;

    /// <summary>Os limites largos do atalho. Fixos e nao `DateTime.MinValue`: o Npgsql traduz os
    /// extremos para `-infinity`/`infinity` ou recusa, conforme a configuracao — e um teste que
    /// dependesse disso passaria aqui e quebraria noutra maquina.</summary>
    private static readonly DateTime DesdeSempre = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AteSempre = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<RelatorioNps> LerAsync(FiltroRelatorio filtro, CancellationToken ct)
    {
        // Mesma recusa do `ServicoRelatorios.PrepararAsync`: invertido, o corte semiaberto
        // `>= inicio AND < fim` nao devolve linha nenhuma, e a tela mostraria zero como se o
        // periodo estivesse vazio.
        if (filtro.Ate < filtro.De)
            throw new RegraDeNegocioException("A data final não pode ser antes da inicial.");

        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new { e.FusoHorario })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Empresa não encontrada.");

        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);
        var hojeLocal = DateOnly.FromDateTime(FusoDeNegocio.AgoraNo(relogio, fuso));
        var recorte = ResponsavelEfetivo(filtro.ResponsavelId);

        var totais = await TotaisAsync(filtro.De, filtro.Ate, fuso, recorte, ct);
        var distribuicao = await DistribuicaoAsync(filtro.De, filtro.Ate, fuso, recorte, ct);

        // O periodo anterior sai da MESMA funcao pura do relatorio de vendas: mes inteiro compara
        // com o mes anterior, recorte solto compara com a mesma quantidade de dias imediatamente
        // antes. Reescrever a regra aqui a faria divergir.
        var janela = PeriodoAnterior.Calcular(filtro.De, filtro.Ate, hojeLocal);
        var antes = await TotaisAsync(janela.AnteriorDe, janela.AnteriorAte, fuso, recorte, ct);

        return new RelatorioNps(totais, distribuicao, Comparar(totais, antes, janela));
    }

    public async Task<Pagina<LinhaRespostaNps>> RespostasAsync(
        FiltroRelatorio periodo, FiltroRespostasNps filtro, int pagina, int tamanho, CancellationToken ct)
    {
        if (periodo.Ate < periodo.De)
            throw new RegraDeNegocioException("A data final não pode ser antes da inicial.");

        if (filtro.DiasSemCompra < 1 || filtro.DiasSemCompra > 730)
            throw new RegraDeNegocioException("Informe de 1 a 730 dias sem compra.");

        pagina = Math.Max(1, pagina);
        tamanho = Math.Clamp(tamanho, 1, ServicoRelatorios.TamanhoMaximoPagina);

        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new { e.FusoHorario })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Empresa não encontrada.");

        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);

        // ===================== O ATALHO DECIDE, E O RESTO OBEDECE =====================
        // Cada atalho FIXA a faixa e a sua condicao, e larga o periodo. Escolher "Promotores que nao
        // voltaram" e depois "faixa: detrator" nao faz sentido, e a regra aqui e que o atalho vence
        // — nao uma combinacao que devolveria vazio sem explicar por que.
        // ==============================================================================
        var faixa = filtro.Faixa;
        var comprouDeNovo = filtro.ComprouDeNovo;
        var semRetorno = false;
        DateTime? ultimaCompraAntesDe = null;

        DateTime inicio;
        DateTime fim;

        if (filtro.Atalho == AtalhoRespostas.PromotoresQueNaoVoltaram)
        {
            // ⚠️ A CONDICAO E SO "ULTIMA COMPRA MAIS VELHA QUE X DIAS", como o pedido define ("sem
            // nova venda ha X dias"). Ja houve aqui um `comprouDeNovo = false` a mais, e ele
            // EXCLUIA o promotor que voltou uma vez ha 100 dias e sumiu de novo — que e exatamente
            // quem a lista existe para achar. A sabotagem dele nao derrubava teste nenhum.
            faixa = FaixaNps.Promotor;
            comprouDeNovo = null;
            ultimaCompraAntesDe = relogio.GetUtcNow().UtcDateTime.AddDays(-filtro.DiasSemCompra);
            inicio = DesdeSempre;
            fim = AteSempre;
        }
        else if (filtro.Atalho == AtalhoRespostas.DetratoresSemRetorno)
        {
            faixa = FaixaNps.Detrator;
            comprouDeNovo = null;
            semRetorno = true;
            inicio = DesdeSempre;
            fim = AteSempre;
        }
        else
        {
            inicio = TimeZoneInfo.ConvertTimeToUtc(periodo.De.ToDateTime(TimeOnly.MinValue), fuso);
            fim = TimeZoneInfo.ConvertTimeToUtc(
                periodo.Ate.AddDays(1).ToDateTime(TimeOnly.MinValue), fuso);
        }

        var (notaMin, notaMax) = Faixa(faixa);

        // Uma função, e não um array: o comando roda duas vezes na página além do fim, e
        // parâmetro do Npgsql não se reaproveita entre comandos.
        NpgsqlParameter[] Parametros(int limite, int deslocamento) =>
        [
            new() { Value = contexto.EmpresaId },                                     // $1
            new() { Value = inicio },                                                 // $2
            new() { Value = fim },                                                    // $3
            Nulavel(ResponsavelEfetivo(periodo.ResponsavelId), NpgsqlDbType.Bigint),  // $4
            new() { Value = notaMin, NpgsqlDbType = NpgsqlDbType.Smallint },          // $5
            new() { Value = notaMax, NpgsqlDbType = NpgsqlDbType.Smallint },          // $6
            new() { Value = semRetorno },                                             // $7
            Nulavel(comprouDeNovo, NpgsqlDbType.Boolean),                             // $8
            Nulavel(ultimaCompraAntesDe, NpgsqlDbType.TimestampTz),                   // $9
            new() { Value = limite },                                                 // $10
            new() { Value = deslocamento }                                            // $11
        ];

        var itens = new List<LinhaRespostaNps>();
        var total = 0;

        await LerAsync(SqlRespostas, Parametros(tamanho, (pagina - 1) * tamanho), l =>
        {
            itens.Add(new LinhaRespostaNps(
                PesquisaId: l.GetInt64(0),
                ContatoId: l.GetInt64(1),
                Cliente: l.GetString(2),
                Nota: l.GetInt16(3),
                DataResposta: l.GetDateTime(4),
                Comentario: l.IsDBNull(5) ? null : l.GetString(5),
                ResponsavelId: l.IsDBNull(6) ? null : l.GetInt64(6),
                Responsavel: l.IsDBNull(7) ? null : l.GetString(7),
                UltimaCompraEm: l.IsDBNull(8) ? null : l.GetDateTime(8),
                ComprouDeNovoEm: l.IsDBNull(9) ? null : l.GetDateTime(9)));
            total = (int)l.GetInt64(10);
        }, ct);

        // ===================== A PÁGINA ALÉM DO FIM (AUD-XX, B5) =====================
        // O total vem de `COUNT(*) OVER ()`, lido de dentro das linhas — e uma página além do fim
        // não tem linha nenhuma: o total saía 0, e a tela dizia "nada aqui" com as páginas
        // anteriores cheias. A pergunta é refeita na MESMA consulta, do início e com uma linha só:
        // os filtros são os mesmos por construção, e o caso comum não paga nada a mais.
        // =============================================================================
        if (itens.Count == 0 && pagina > 1)
        {
            await LerAsync(SqlRespostas, Parametros(1, 0), l => { total = (int)l.GetInt64(10); }, ct);
        }

        return new Pagina<LinhaRespostaNps>(total, pagina, tamanho, itens);
    }

    /// <summary>As bordas das faixas saem das MESMAS constantes que as acoes da nota usam: um
    /// detrator na lista e quem recebeu o lembrete de detrator. Sem faixa, as onze notas.</summary>
    private static (short Min, short Max) Faixa(FaixaNps? faixa)
    {
        if (faixa == FaixaNps.Promotor) return (AcoesDaNota.PisoPromotor, 10);
        if (faixa == FaixaNps.Neutro) return ((short)(AcoesDaNota.TetoDetrator + 1), (short)(AcoesDaNota.PisoPromotor - 1));
        if (faixa == FaixaNps.Detrator) return (0, AcoesDaNota.TetoDetrator);
        return (0, 10);
    }

    /// <summary>`DBNull` COM TIPO DECLARADO — sem ele o Postgres nao resolve `$n::tipo IS NULL` e a
    /// consulta inteira falha por um filtro nao usado. Mesma razao do `ServicoRelatorios`.</summary>
    private static NpgsqlParameter Nulavel(object? valor, NpgsqlDbType tipo) =>
        new() { Value = valor ?? DBNull.Value, NpgsqlDbType = tipo };

    /// <summary>⚠️ O NPS NULO ENTRA NA COMPARACAO COMO ZERO, e a escolha e discutivel — deixo
    /// escrito. Sem resposta nenhuma, `Nps` e nulo de proposito (zero e um NPS real). Mas a
    /// comparacao precisa de um numero, e "0 contra 40" ao menos mostra a queda; esconder o cartao
    /// faria o periodo vazio parecer que nao houve periodo.
    ///
    /// A tela tem os dois: o cartao grande le `Totais.Nps`, que E nulo, e escreve "sem respostas".
    /// Esta comparacao e a setinha ao lado.</summary>
    private static ComparativoNps Comparar(TotaisNps atual, TotaisNps antes, PeriodoComparado j) =>
        new(
            Comparacao.De((decimal)(atual.Nps ?? 0), (decimal)(antes.Nps ?? 0),
                SentidoBom.Sobe, j.AnteriorDe, j.AnteriorAte),
            Comparacao.De(atual.Respondidas, antes.Respondidas,
                SentidoBom.Sobe, j.AnteriorDe, j.AnteriorAte),
            Comparacao.De((decimal)(atual.TaxaDeResposta ?? 0), (decimal)(antes.TaxaDeResposta ?? 0),
                SentidoBom.Sobe, j.AnteriorDe, j.AnteriorAte),
            Comparacao.De(atual.Promotores, antes.Promotores,
                SentidoBom.Sobe, j.AnteriorDe, j.AnteriorAte),
            j.De, j.Ate, j.EmAndamento);

    private async Task<TotaisNps> TotaisAsync(
        DateOnly de, DateOnly ate, TimeZoneInfo fuso, long? recorte, CancellationToken ct)
    {
        var saida = new TotaisNps(0, 0, 0, 0, 0, 0, 0, 0);

        await LerAsync(SqlTotais, Parametros(de, ate, fuso, recorte), l =>
            saida = new TotaisNps(
                Enviadas: (int)l.GetInt64(0),
                Respondidas: (int)l.GetInt64(1),
                Expiradas: (int)l.GetInt64(2),
                Canceladas: (int)l.GetInt64(3),
                AindaAbertas: (int)l.GetInt64(4),
                Promotores: (int)l.GetInt64(5),
                Neutros: (int)l.GetInt64(6),
                Detratores: (int)l.GetInt64(7)), ct);

        return saida;
    }

    private async Task<IReadOnlyList<FatiaDaNota>> DistribuicaoAsync(
        DateOnly de, DateOnly ate, TimeZoneInfo fuso, long? recorte, CancellationToken ct)
    {
        var contagem = new Dictionary<short, int>();

        await LerAsync(SqlDistribuicao, Parametros(de, ate, fuso, recorte),
            l => contagem[l.GetInt16(0)] = (int)l.GetInt64(1), ct);

        // As ONZE, sempre — ver o comentario do `SqlDistribuicao`.
        var fatias = new List<FatiaDaNota>(11);

        for (short nota = 0; nota <= 10; nota++)
        {
            fatias.Add(new FatiaDaNota(nota, contagem.GetValueOrDefault(nota)));
        }

        return fatias;
    }

    private NpgsqlParameter[] Parametros(
        DateOnly de, DateOnly ate, TimeZoneInfo fuso, long? recorte)
    {
        // ⚠️ O CORTE SEMI-ABERTO VEM DO MESMO LUGAR do relatorio de vendas, e escrito duas vezes ele
        // divergiria: um com `<= fim` e o outro com `< fim+1d` fariam o periodo anterior perder o
        // ultimo dia, com os dois numeros plausiveis na tela.
        var inicio = TimeZoneInfo.ConvertTimeToUtc(de.ToDateTime(TimeOnly.MinValue), fuso);
        var fim = TimeZoneInfo.ConvertTimeToUtc(ate.AddDays(1).ToDateTime(TimeOnly.MinValue), fuso);

        return
        [
            new() { Value = contexto.EmpresaId },
            new() { Value = inicio },
            new() { Value = fim },
            new()
            {
                Value = (object?)recorte ?? DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Bigint
            }
        ];
    }

    /// <summary>Mesma linha de corte do `ServicoRelatorios.ResponsavelEfetivo`, e escrita do mesmo
    /// jeito de proposito: duas formas da mesma regra divergem no dia em que uma delas muda. Quem
    /// NAO ve os numeros da equipe ve so os seus — o parametro do cliente e DESCARTADO.</summary>
    private long? ResponsavelEfetivo(long? pedido) =>
        contexto.Pode(Permissao.VerNumerosDaEquipe) ? pedido : contexto.UsuarioId;

    /// <summary>Gemeo do encanamento do `ServicoRelatorios` e do `ServicoLeadsParados`. A linha da
    /// transacao se denuncia sozinha: sem ela, todo teste de integracao deixa de ver as proprias
    /// linhas.</summary>
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
    /// A auditoria que varre as consultas atras de funcao sobre coluna dentro de um `WHERE` le uma
    /// LISTA MANUAL, e servico novo fora dela fica sem rede com o teste continuando verde. Entra nos
    /// DOIS testes que a varrem.
    /// ==================================================================</summary>
    public static IReadOnlyList<(string Nome, string Sql)> ConsultasParaAuditoria =>
    [
        ("nps totais", SqlTotais),
        ("nps distribuicao", SqlDistribuicao),
        ("nps respostas", SqlRespostas)
    ];
}
