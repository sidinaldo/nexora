using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Nexora.Core;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== A EVOLUÇÃO (EVO-1) =====================
///
/// `ServicoRelatorios` responde "quanto, neste recorte". Isto responde **"está melhorando?"**, que
/// é outra pergunta e — como o `IServicoEvolucao` explica em detalhe — exige outra definição de
/// conversão, porque a de lá muda o passado sozinha.
///
/// ⚠️ SERVIÇO SEPARADO, E NÃO UM MÉTODO A MAIS EM `ServicoRelatorios`. Duas definições de conversão
/// no mesmo arquivo seriam duas coisas chamadas igual a um `Ctrl+F` de distância uma da outra. A
/// fronteira entre elas é o que o nome do arquivo guarda.
///
/// TUDO NO SQL, pela razão do vizinho: a empresa com um ano de uso tem dezenas de milhares de
/// negociações, e doze meses de conversão por pessoa são oito linhas de resultado.
/// ======================================================================</summary>
public class ServicoEvolucao(NexoraDbContext db, IContextoEmpresa contexto, TimeProvider relogio)
    : IServicoEvolucao
{
    /// <summary>As janelas que a tela oferece. Lista FECHADA: o número entra numa conta de meses e
    /// um `meses=600` pedido na mão varreria a tabela inteira para desenhar um gráfico ilegível.</summary>
    public static readonly int[] JanelasEmMeses = [3, 6, 12];

    /// <summary>===================== UMA COORTE SÓ, DATADA PELO PRÓPRIO FECHAMENTO =====================
    ///
    /// Cada negociação decidida entra UMA vez, no mês em que foi decidida, atribuída a
    /// `negociacoes.responsavel_id` — a mesma coluna nas duas pontas.
    ///
    /// ⚠️ `UNION ALL` E NÃO DUAS AGREGAÇÕES JUNTADAS. Ganho e perda são datados por colunas
    /// DIFERENTES (`ganha_em` e `perdida_em`), e um `JOIN` entre dois agregados por mês perderia
    /// toda pessoa que num mês só ganhou ou só perdeu — exatamente os meses de conversão 0% e
    /// 100%, que são os que a tela precisa mostrar.
    ///
    /// ⚠️ NUNCA FUNÇÃO SOBRE COLUNA EM FILTRO, mesma regra do `ServicoRelatorios`: os cortes são
    /// `coluna >= $1 AND coluna < $2` com os limites calculados no C#. `date_trunc` aparece só no
    /// SELECT, sobre o conjunto já recortado. Há um teste que lê esta consulta e falha se mudar.
    ///
    /// ⚠️ O RECORTE DE DATA É GUARDADO EM DUPLA COM O C#, e nenhuma das duas metades sozinha é
    /// observável — isto foi MEDIDO, com três sabotagens:
    ///
    ///   · apagar só o `perdida_em >= $1`: suíte inteira VERDE. O C# percorre a lista de meses
    ///     pedida e ignora o que caiu fora;
    ///   · trocar só a regra da linha "Sem dono" pela varredura das chaves do agregado: VERDE
    ///     também. O recorte do SQL já não deixava chave de fora da janela chegar;
    ///   · as DUAS ao mesmo tempo: cai `SEM_DONO_FORA_DA_JANELA_NAO_CRIA_A_LINHA`.
    ///
    /// Então as duas são redundantes de propósito, e o aviso é para quem for "limpar" uma delas
    /// achando que a outra basta: basta mesmo, até alguém mexer na outra — e aí a tela ganha uma
    /// linha fantasma e nenhum teste reclama.
    ///
    /// E o recorte tem um segundo papel que teste nenhum alcança: sem ele a consulta varre TODA
    /// negociação já fechada pela empresa a cada carregamento, para o C# jogar quase tudo fora.
    ///
    /// `status IN ('ganha', 'concluida')` e não `ganha_em IS NOT NULL`: uma negociação CANCELADA
    /// mantém o `ganha_em` que tinha, e contá-la como ganho faria o cancelamento não ter efeito
    /// nenhum aqui — o oposto do que ele significa.
    /// ============================================================================================</summary>
    private const string SqlEvolucao = """
        WITH fechamentos AS (
            SELECT n.responsavel_id,
                   date_trunc('month', n.ganha_em AT TIME ZONE $3)::date AS mes,
                   1 AS ganho
              FROM negociacoes n
             WHERE n.empresa_id = $4
               AND n.status IN ('ganha', 'concluida')
               AND n.ganha_em >= $1 AND n.ganha_em < $2
               AND ($5::bigint IS NULL OR n.responsavel_id = $5)
            UNION ALL
            SELECT n.responsavel_id,
                   date_trunc('month', n.perdida_em AT TIME ZONE $3)::date,
                   0
              FROM negociacoes n
             WHERE n.empresa_id = $4
               AND n.status = 'perdida'
               AND n.perdida_em >= $1 AND n.perdida_em < $2
               AND ($5::bigint IS NULL OR n.responsavel_id = $5)
        )
        SELECT f.responsavel_id,
               f.mes,
               COUNT(*)      AS decididos,
               SUM(f.ganho)  AS ganhos
          FROM fechamentos f
         GROUP BY f.responsavel_id, f.mes
         ORDER BY f.responsavel_id, f.mes
        """;

    /// <summary>O nome da linha que junta o que ninguém assumiu. Igual ao do relatório de
    /// vendedores, de propósito: dois nomes para a mesma ausência fariam o cliente perguntar qual
    /// é qual.</summary>
    private const string SemDono = "Sem dono";

    public async Task<EvolucaoDaEquipe> ObterAsync(int meses, CancellationToken ct)
    {
        if (!JanelasEmMeses.Contains(meses))
            throw new RegraDeNegocioException(
                $"Janela inválida: {meses}. Use {string.Join(", ", JanelasEmMeses)}.");

        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new { e.FusoHorario })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Empresa não encontrada.");

        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);

        // ===== O NOME DO FUSO QUE VAI PARA O POSTGRES =====
        // NÃO se manda o id do fallback (`br-fixo`) nem um "UTC-03" montado à mão: em sintaxe POSIX
        // o sinal é INVERTIDO, e seriam seis horas de erro por ponto sem nenhuma exceção para
        // denunciar. Mesma razão e mesma linha do `ServicoRelatorios.PrepararAsync`.
        var nomeFuso = string.IsNullOrWhiteSpace(empresa.FusoHorario)
            ? FusoDeNegocio.PadraoBrasil
            : empresa.FusoHorario;

        // HOJE na hora da EMPRESA. É ele que decide qual mês está em andamento, e às 22h de
        // Brasília o servidor em UTC já virou o dia — num dia 31, já virou o MÊS.
        var hojeLocal = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(relogio.GetUtcNow().UtcDateTime, fuso));

        var mesCorrente = new DateOnly(hojeLocal.Year, hojeLocal.Month, 1);
        var primeiroMes = mesCorrente.AddMonths(-(meses - 1));

        // Corte EXCLUSIVO no fim, pelo primeiro instante do mês SEGUINTE: `<= último dia` perderia
        // tudo que acontecesse depois de 00h00 daquele dia.
        var inicioUtc = TimeZoneInfo.ConvertTimeToUtc(
            primeiroMes.ToDateTime(TimeOnly.MinValue), fuso);
        var fimUtc = TimeZoneInfo.ConvertTimeToUtc(
            mesCorrente.AddMonths(1).ToDateTime(TimeOnly.MinValue), fuso);

        var janela = Enumerable.Range(0, meses).Select(primeiroMes.AddMonths).ToList();

        var soVeOSeu = !contexto.Pode(Permissao.VerNumerosDaEquipe);
        var recorte = soVeOSeu ? contexto.UsuarioId : (long?)null;

        var contagens = await LerContagensAsync(inicioUtc, fimUtc, nomeFuso, recorte, ct);
        var pessoas = await NomesAsync(recorte, ct);

        var linhas = new List<EvolucaoDoVendedor>();

        foreach (var (id, nome, desde) in pessoas)
            linhas.Add(Montar(id, nome, desde, contagens, janela, mesCorrente));

        // A linha "Sem dono" só nasce quando tem o que mostrar NA JANELA: uma linha de zeros em
        // toda empresa que atribui tudo seria ruído permanente. Mesma regra do relatório de
        // vendedores.
        //
        // ⚠️ A PERGUNTA É SOBRE A LINHA MONTADA, e não sobre as chaves do agregado. A primeira
        // versão fazia `contagens.Keys.Any(k => k.Pessoa is null)`, que varre TUDO que voltou do
        // banco — inclusive mês fora da janela. Ela só acertava porque o SQL já recortava por
        // data, e isso fazia a correção da tela depender de um filtro que existe por INDICE.
        // Descoberto sabotando: tirar o recorte de `perdida_em` não derrubava teste nenhum.
        var semDono = Montar(null, SemDono, null, contagens, janela, mesCorrente);
        if (semDono.Decididos > 0) linhas.Add(semDono);

        // ⚠️ A MÉDIA DA EQUIPE É A MESMA CONTA SOBRE TODO MUNDO JUNTO, não a média das conversões
        // de cada um. Média de percentuais daria a quem decidiu 3 negócios o mesmo peso de quem
        // decidiu 80 — e a régua passaria a depender de quem tirou férias.
        //
        // Quem não vê os números da equipe não recebe régua nenhuma: entregá-la seria vazar pela
        // porta dos fundos o número que a permissão fecha pela frente.
        var equipe = soVeOSeu
            ? null
            : Montar(null, "Equipe (média)", null, contagens, janela, mesCorrente, todos: true);

        return new EvolucaoDaEquipe(
            equipe,
            [.. linhas.OrderBy(l => l.UsuarioId is null).ThenBy(l => l.Nome, StringComparer.CurrentCulture)],
            primeiroMes,
            mesCorrente.AddMonths(1).AddDays(-1));
    }

    /// <summary>A chave do agregado. Record por causa da igualdade estrutural — tupla num
    /// dicionário funcionaria igual, e o nome é o que faz `k.Pessoa is null` se ler.</summary>
    private sealed record Celula(long? Pessoa, DateOnly Mes);

    private sealed record Contagem(int Decididos, int Ganhos);

    private static EvolucaoDoVendedor Montar(
        long? id, string nome, DateTime? desde,
        Dictionary<Celula, Contagem> contagens, List<DateOnly> janela, DateOnly mesCorrente,
        bool todos = false)
    {
        var meses = new List<MesDaConversao>(janela.Count);

        foreach (var mes in janela)
        {
            var c = todos
                ? Somar(contagens, mes)
                : contagens.GetValueOrDefault(new Celula(id, mes), new Contagem(0, 0));

            // A fábrica, e não o construtor do record: conversão e amostra insuficiente são
            // DERIVADAS, e decidi-las aqui deixaria o limiar de 10 fora do alcance do teste puro.
            meses.Add(RegrasTendencia.Mes(
                mes.Year, mes.Month, c.Decididos, c.Ganhos, parcial: mes == mesCorrente));
        }

        var decididos = meses.Sum(m => m.Decididos);
        var ganhos = meses.Sum(m => m.Ganhos);
        var (variacao, tendencia) = RegrasTendencia.De(meses);

        return new EvolucaoDoVendedor(
            id, nome, desde, decididos, ganhos, RegrasTendencia.Conversao(ganhos, decididos),
            variacao, tendencia, meses);
    }

    private static Contagem Somar(Dictionary<Celula, Contagem> contagens, DateOnly mes)
    {
        var decididos = 0;
        var ganhos = 0;

        foreach (var (chave, valor) in contagens)
        {
            if (chave.Mes != mes) continue;
            decididos += valor.Decididos;
            ganhos += valor.Ganhos;
        }

        return new Contagem(decididos, ganhos);
    }

    private async Task<Dictionary<Celula, Contagem>> LerContagensAsync(
        DateTime inicioUtc, DateTime fimUtc, string nomeFuso, long? recorte, CancellationToken ct)
    {
        var contagens = new Dictionary<Celula, Contagem>();

        NpgsqlParameter[] parametros =
        [
            new() { Value = inicioUtc },                                        // $1
            new() { Value = fimUtc },                                           // $2
            new() { Value = nomeFuso },                                         // $3
            new() { Value = contexto.EmpresaId },                               // $4
            // `DBNull` COM TIPO DECLARADO: sem o `NpgsqlDbType` o driver manda `unknown` e o
            // Postgres não resolve `$5::bigint IS NULL` — a consulta inteira falha por causa de um
            // filtro que não estava sendo usado.
            new() { Value = (object?)recorte ?? DBNull.Value,
                    NpgsqlDbType = NpgsqlDbType.Bigint }                        // $5
        ];

        await LerAsync(SqlEvolucao, parametros, l =>
        {
            var pessoa = l.IsDBNull(0) ? (long?)null : l.GetInt64(0);
            var mes = DateOnly.FromDateTime(l.GetDateTime(1));

            // `COUNT` e `SUM` devolvem `bigint`. Ler como Int32 estoura com InvalidCastException
            // na primeira linha — não é truncamento silencioso, mas também não é mensagem útil.
            contagens[new Celula(pessoa, mes)] =
                new Contagem((int)l.GetInt64(2), (int)l.GetInt64(3));
        }, ct);

        return contagens;
    }

    /// <summary>Nome e tempo de casa vêm pelo EF, não pelo SQL cru: é busca por chave, o filtro
    /// global de `empresa_id` já se aplica, e trazer o nome na consulta de agregação obrigaria a
    /// um `GROUP BY` sobre texto sem nenhum ganho.</summary>
    private async Task<List<(long Id, string Nome, DateTime? Desde)>> NomesAsync(
        long? recorte, CancellationToken ct)
    {
        var consulta = db.Usuarios.AsNoTracking();

        if (recorte is not null) consulta = consulta.Where(u => u.Id == recorte);

        var pessoas = await consulta
            .Select(u => new { u.Id, u.Nome, u.CriadoEm })
            .ToListAsync(ct);

        return [.. pessoas.Select(p => (p.Id, p.Nome, (DateTime?)p.CriadoEm))];
    }

    /// <summary>Gêmeo do `ServicoRelatorios.LerAsync`, e duplicado de propósito: é encanamento, não
    /// regra. Extrair para um ajudante comum mexeria num arquivo de mil linhas sob teste para
    /// economizar dez — e a única linha sutil daqui, a da transação, se denuncia sozinha: sem ela
    /// todo teste de integração deixa de ver as próprias linhas, na primeira execução.</summary>
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
    /// LISTA MANUAL, não o assembly. Serviço novo que não aparece nela fica fora da rede sem
    /// nenhum sintoma — e o teste continua verde, o que é pior que vermelho.
    /// ==================================================================</summary>
    public static IReadOnlyList<(string Nome, string Sql)> ConsultasParaAuditoria =>
    [
        ("evolução · conversão por mês", SqlEvolucao)
    ];
}
