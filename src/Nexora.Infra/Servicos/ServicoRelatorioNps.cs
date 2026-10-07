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
        ("nps distribuicao", SqlDistribuicao)
    ];
}
