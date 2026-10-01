using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A área do operador: o catálogo de planos e os tetos de cada empresa.
///
/// ===================== ISTO NUNCA RODA DENTRO DE UMA SESSÃO =====================
/// Não há JWT aqui, então `EmpresaId` entra como 0. Se não entrar, alguém ligou este serviço num
/// caminho autenticado — e aí o `Assumir` abaixo trocaria o tenant de uma requisição de cliente no
/// meio do caminho.
///
/// Por isso `ExigirSemSessao()` lança alto em vez de seguir: a falha silenciosa aqui é leitura
/// cruzada de tenant, que é o único modo de falha que este sistema foi desenhado para não ter.
/// ================================================================================
///
/// ===================== POR QUE `Assumir`, E NÃO `IgnoreQueryFilters` =====================
/// Não é conveniência, é obrigação. A trilha de auditoria entra no MESMO `SaveChanges` do fato, e
/// `auditoria.empresa_id` vem do CONTEXTO, é `NOT NULL` e é FK `RESTRICT`. Com o contexto em 0 o
/// INSERT da trilha viola a chave estrangeira e a escrita inteira volta atrás, com um 23503 cru.
///
/// Ou seja: sem assumir a empresa, a área do operador simplesmente não grava. Há teste para isso em
/// `TrilhaDbTests`, para quem esbarrar no erro não precisar descobrir sozinho.
/// ========================================================================================</summary>
public class ServicoOperador(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    ContextoDeFundo fundo,
    ColetorAuditoria trilha) : IServicoOperador
{
    private const string SqlEmpresas = @"
        -- ===================== PAGINA PRIMEIRO, AGREGA DEPOIS =====================
        -- A pagina de `empresas` sai antes, e os agregados sao juntados A ELA por LATERAL. Assim o
        -- custo acompanha o TAMANHO DA PAGINA, e nao o da tabela -- e e a diferenca entre uma tela
        -- que funciona com 3 clientes e uma que funciona com 300.
        --
        -- O caminho ingenuo (agregar tudo e paginar no fim) le `mensagens` inteira para mostrar 25
        -- linhas, e so comeca a doer quando ja ha cliente para doer.
        -- =========================================================================
        WITH pagina AS (
            SELECT e.id, e.nome, e.ativo, e.demonstracao, e.plano_id,
                   e.limite_conexoes, e.limite_usuarios, e.criado_em, e.primeira_mensagem_em,
                   -- O total ANTES do LIMIT, na mesma ida ao banco. Janela roda antes do recorte.
                   COUNT(*) OVER () AS total
              FROM empresas e
             WHERE ($1 = '' OR e.nome ILIKE '%' || $1 || '%')
             ORDER BY e.id
             LIMIT $2 OFFSET $3
        )
        SELECT p.id, p.nome, p.ativo, p.demonstracao,
               p.plano_id, pl.nome AS plano_nome,
               p.limite_conexoes, p.limite_usuarios,
               (pl.id IS NOT NULL
                AND (pl.limite_conexoes <> p.limite_conexoes
                  OR pl.limite_usuarios <> p.limite_usuarios)) AS personalizados,
               p.criado_em, p.primeira_mensagem_em,
               COALESCE(u.ativos, 0)      AS usuarios_ativos,
               COALESCE(u.convidados, 0)  AS usuarios_convidados,
               u.ultimo_acesso,
               COALESCE(cx.total, 0)      AS conexoes,
               COALESCE(cx.conectadas, 0) AS conexoes_conectadas,
               COALESCE(ct.total, 0)      AS contatos,
               cv.ultima_mensagem,
               COALESCE(n.abertas, 0)     AS negociacoes_abertas,
               COALESCE(n.ganhas, 0)      AS ganhas_janela,
               COALESCE(n.valor, 0)       AS valor_janela,
               p.total
          FROM pagina p
          LEFT JOIN planos pl ON pl.id = p.plano_id
          -- Contam ativo + convidado, igual a regra de vaga do `ServicoEquipe`. Inativo fica fora.
          LEFT JOIN LATERAL (
              SELECT COUNT(*) FILTER (WHERE u.status = 'ativo')     AS ativos,
                     COUNT(*) FILTER (WHERE u.status = 'convidado') AS convidados,
                     MAX(u.ultimo_acesso_em)                        AS ultimo_acesso
                FROM usuarios u WHERE u.empresa_id = p.id
          ) u ON TRUE
          LEFT JOIN LATERAL (
              SELECT COUNT(*)                                          AS total,
                     COUNT(*) FILTER (WHERE c.status = 'conectado')     AS conectadas
                FROM conexoes c WHERE c.empresa_id = p.id
          ) cx ON TRUE
          LEFT JOIN LATERAL (
              SELECT COUNT(*) AS total
                FROM contatos ct WHERE ct.empresa_id = p.id AND ct.anonimizado_em IS NULL
          ) ct ON TRUE
          -- ⚠️ `ultima_mensagem_em` VEM DE `conversas`, NAO DE `mensagens`. Ela ja esta
          -- materializada, uma linha por conversa; o MAX sobre `mensagens` seria varredura da
          -- tabela de maior escrita do sistema para mostrar uma data numa lista.
          LEFT JOIN LATERAL (
              SELECT MAX(cv.ultima_mensagem_em) AS ultima_mensagem
                FROM conversas cv WHERE cv.empresa_id = p.id
          ) cv ON TRUE
          LEFT JOIN LATERAL (
              SELECT COUNT(*) FILTER (WHERE n.status = 'aberta')                  AS abertas,
                     COUNT(*) FILTER (WHERE n.ganha_em >= $4
                                        AND n.status <> 'cancelada')              AS ganhas,
                     COALESCE(SUM(n.valor) FILTER (WHERE n.ganha_em >= $4
                                        AND n.status <> 'cancelada'), 0)          AS valor
                FROM negociacoes n WHERE n.empresa_id = p.id
          ) n ON TRUE
         ORDER BY p.id;";

    // ==================================================================== os números

    /// <summary>A lista do operador: uma linha por empresa, só números.
    ///
    /// ⚠️ SQL CRU, e ATRAVESSA TODAS AS EMPRESAS de propósito — é a única leitura do sistema que
    /// faz isso a pedido de um humano. O que a mantém segura não é o SQL, são as quatro barreiras
    /// do cabeçalho da classe, e principalmente duas: a rota ignora o JWT, e `ExigirSemSessao`
    /// recusa rodar de dentro de uma sessão de cliente.
    ///
    /// Não dá para usar o EF aqui: o filtro global de `empresas` compara com o tenant do contexto,
    /// que nesta área é 0, e devolveria vazio em silêncio. `IgnoreQueryFilters` resolveria a
    /// leitura e não resolveria o custo — os agregados viriam em N+1 ou numa varredura.</summary>
    public async Task<Pagina<EmpresaNaLista>> ListarEmpresasAsync(
        FiltroEmpresas filtro, CancellationToken ct)
    {
        ExigirSemSessao();

        var tamanho = Math.Clamp(filtro.Tamanho, 1, 100);
        var numero = Math.Max(filtro.Pagina, 1);
        var dias = Math.Clamp(filtro.Dias, 1, 365);
        var desde = DateTime.UtcNow.AddDays(-dias);

        var itens = new List<EmpresaNaLista>();
        var total = 0;

        await LerAsync(SqlEmpresas,
        [
            new NpgsqlParameter { Value = (filtro.Busca ?? "").Trim() },
            new NpgsqlParameter { Value = tamanho },
            new NpgsqlParameter { Value = (numero - 1) * tamanho },
            new NpgsqlParameter { Value = desde, NpgsqlDbType = NpgsqlDbType.TimestampTz }
        ], l =>
        {
            var criada = l.GetDateTime(9);
            var primeira = l.IsDBNull(10) ? (DateTime?)null : l.GetDateTime(10);

            itens.Add(new EmpresaNaLista(
                l.GetInt64(0), l.GetString(1), l.GetBoolean(2), l.GetBoolean(3),
                l.IsDBNull(4) ? null : l.GetInt64(4),
                l.IsDBNull(5) ? null : l.GetString(5),
                l.GetInt16(6), l.GetInt16(7),
                !l.IsDBNull(8) && l.GetBoolean(8),
                criada,
                primeira is null ? null : (primeira.Value - criada).TotalHours,
                (int)l.GetInt64(11), (int)l.GetInt64(12),
                (int)l.GetInt64(11) + (int)l.GetInt64(12),
                (int)l.GetInt64(14), (int)l.GetInt64(15),
                (int)l.GetInt64(16),
                l.IsDBNull(13) ? null : l.GetDateTime(13),
                l.IsDBNull(17) ? null : l.GetDateTime(17),
                (int)l.GetInt64(18), (int)l.GetInt64(19), l.GetDecimal(20)));

            total = (int)l.GetInt64(21);
        }, ct);

        return new Pagina<EmpresaNaLista>(total, numero, tamanho, itens);
    }

    /// <summary>O leitor de SQL cru. Mesma forma do `ServicoRelatorios.LerAsync`, inclusive o
    /// detalhe que não é óbvio: a transação em curso precisa ser passada à mão, porque comando cru
    /// não se alista sozinho — sem isso o teste, que roda tudo numa transação revertida, não
    /// enxergaria as próprias linhas.</summary>
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
    /// O mesmo teste que varre as consultas do `ServicoRelatorios` atrás de função sobre coluna
    /// dentro de um `WHERE` passa a varrer esta. Expor SQL para teste é feio; a alternativa é uma
    /// regra que vale só enquanto alguém lembra dela na revisão.
    /// =====================================================================</summary>
    public static IReadOnlyList<(string Nome, string Sql)> ConsultasParaAuditoria =>
    [
        ("operador · empresas", SqlEmpresas)
    ];

    // ==================================================================== o catálogo

    public async Task<IReadOnlyList<PlanoDto>> ListarPlanosAsync(CancellationToken ct)
    {
        ExigirSemSessao();

        // `planos` não tem filtro de tenant (é a única tabela assim), então lê direto. A contagem
        // de empresas por plano precisa de `IgnoreQueryFilters`: `empresas` TEM filtro, e sem
        // tenant no contexto ela voltaria zero para todo mundo, em silêncio.
        var porPlano = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.PlanoId != null)
            .GroupBy(e => e.PlanoId!.Value)
            .Select(g => new { PlanoId = g.Key, Quantas = g.Count() })
            .ToDictionaryAsync(x => x.PlanoId, x => x.Quantas, ct);

        return await db.Planos.AsNoTracking()
            .OrderBy(p => p.Ordem).ThenBy(p => p.Preco)
            .Select(p => new PlanoDto(
                p.Id, p.Nome, p.Preco, p.LimiteConexoes, p.LimiteUsuarios, p.Ativo, p.Ordem,
                porPlano.ContainsKey(p.Id) ? porPlano[p.Id] : 0))
            .ToListAsync(ct);
    }

    public async Task<long> CriarPlanoAsync(NovoPlano novo, CancellationToken ct)
    {
        ExigirSemSessao();
        var nome = ValidarPlano(novo.Nome, novo.Preco, novo.LimiteConexoes, novo.LimiteUsuarios);

        await ExigirNomeLivreAsync(nome, ignorarId: null, ct);

        var plano = new Plano
        {
            Nome = nome,
            Preco = novo.Preco,
            LimiteConexoes = novo.LimiteConexoes,
            LimiteUsuarios = novo.LimiteUsuarios,
            Ordem = novo.Ordem <= 0 ? (short)1 : novo.Ordem
        };
        db.Planos.Add(plano);
        await db.SaveChangesAsync(ct);

        return plano.Id;
    }

    public async Task AtualizarPlanoAsync(long planoId, EditarPlano dados, CancellationToken ct)
    {
        ExigirSemSessao();
        var nome = ValidarPlano(dados.Nome, dados.Preco, dados.LimiteConexoes, dados.LimiteUsuarios);

        var plano = await db.Planos.FirstOrDefaultAsync(p => p.Id == planoId, ct)
            ?? throw new RegraDeNegocioException("Plano não encontrado.");

        await ExigirNomeLivreAsync(nome, ignorarId: planoId, ct);

        // ⚠️ ISTO NÃO TOCA EM EMPRESA NENHUMA, de propósito. Os limites já atribuídos foram COPIADOS
        // para a linha de cada empresa — limite é contrato, e contrato de quem assinou em março não
        // muda porque a tabela de preços mudou em agosto. Ver `Plano.cs`.
        plano.Nome = nome;
        plano.Preco = dados.Preco;
        plano.LimiteConexoes = dados.LimiteConexoes;
        plano.LimiteUsuarios = dados.LimiteUsuarios;
        plano.Ativo = dados.Ativo;
        plano.Ordem = dados.Ordem <= 0 ? (short)1 : dados.Ordem;

        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== a empresa

    public async Task<LimitesDaEmpresa> LimitesAsync(long empresaId, CancellationToken ct)
    {
        ExigirSemSessao();
        fundo.Assumir(empresaId, 0);
        return await MontarAsync(empresaId, ct);
    }

    public async Task<LimitesDaEmpresa> AtribuirPlanoAsync(
        long empresaId, long planoId, bool confirmarExcedente, CancellationToken ct)
    {
        ExigirSemSessao();
        fundo.Assumir(empresaId, 0);

        var plano = await db.Planos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planoId, ct)
            ?? throw new RegraDeNegocioException("Plano não encontrado.");

        if (!plano.Ativo)
            throw new RegraDeNegocioException(
                $"O plano \"{plano.Nome}\" está arquivado e não pode ser atribuído. "
                + "Reative-o no catálogo, ou escolha outro.");

        return await GravarAsync(
            empresaId, plano.LimiteConexoes, plano.LimiteUsuarios, planoId, ativa: null,
            confirmarExcedente, ct);
    }

    public Task<LimitesDaEmpresa> AjustarLimitesAsync(
        long empresaId, AjusteDeLimites ajuste, CancellationToken ct)
    {
        ExigirSemSessao();
        fundo.Assumir(empresaId, 0);

        // ⚠️ O PLANO NÃO SAI. Ajustar os tetos à mão é o caso "esse cliente negociou uma conexão a
        // mais" — ele continua no plano que foi vendido, e a tela mostra que os limites fugiram do
        // molde. Tirá-lo do plano aqui apagaria o registro do que foi contratado.
        return GravarAsync(
            empresaId, ajuste.LimiteConexoes, ajuste.LimiteUsuarios, planoId: null, ativa: null,
            ajuste.ConfirmarExcedente, ct);
    }

    public Task<LimitesDaEmpresa> DefinirAtivaAsync(long empresaId, bool ativa, CancellationToken ct)
    {
        ExigirSemSessao();
        fundo.Assumir(empresaId, 0);
        return GravarAsync(empresaId, null, null, null, ativa, confirmarExcedente: true, ct);
    }

    // ==================================================================== o miolo

    /// <summary>A única escrita em `empresas` desta área.
    ///
    /// ⚠️ CARREGA A ENTIDADE E USA `SaveChanges` — nunca `ExecuteUpdateAsync`. É um update de uma
    /// linha e duas colunas, o caso mais tentador que existe, e há cinco precedentes em `empresas`
    /// que o fazem parecer idiomático. Ele pula os DOIS interceptors: `atualizado_em` para de ser
    /// escrito e a trilha inteira some, sem erro nenhum. Há teste que lê a TABELA `auditoria`.</summary>
    private async Task<LimitesDaEmpresa> GravarAsync(
        long empresaId, short? conexoes, short? usuarios, long? planoId, bool? ativa,
        bool confirmarExcedente, CancellationToken ct)
    {
        var empresa = await db.Empresas.FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new RegraDeNegocioException("Empresa não encontrada.");

        var (conexoesUsadas, vagasUsadas) = await UsoAsync(ct);

        var novoConexoes = conexoes ?? empresa.LimiteConexoes;
        var novoUsuarios = usuarios ?? empresa.LimiteUsuarios;

        if (!confirmarExcedente)
            ExigirConfirmacaoSeEncolhe(novoConexoes, conexoesUsadas, novoUsuarios, vagasUsadas);

        var antes = new Dictionary<string, AlteracaoValor>();
        if (novoConexoes != empresa.LimiteConexoes)
            antes["limiteConexoes"] = new AlteracaoValor(empresa.LimiteConexoes, novoConexoes);
        if (novoUsuarios != empresa.LimiteUsuarios)
            antes["limiteUsuarios"] = new AlteracaoValor(empresa.LimiteUsuarios, novoUsuarios);
        if (planoId is not null && planoId != empresa.PlanoId)
            antes["planoId"] = new AlteracaoValor(empresa.PlanoId, planoId);
        if (ativa is not null && ativa != empresa.Ativo)
            antes["ativo"] = new AlteracaoValor(empresa.Ativo, ativa);

        // Nada mudou: não grava nem declara. Trilha com evento vazio é ruído que torna a de verdade
        // mais difícil de achar.
        if (antes.Count == 0) return await MontarAsync(empresaId, ct);

        empresa.LimiteConexoes = novoConexoes;
        empresa.LimiteUsuarios = novoUsuarios;
        if (planoId is not null) empresa.PlanoId = planoId;
        if (ativa is not null) empresa.Ativo = ativa.Value;

        // ⚠️ `AtorAuditoria.Operador` DITO NA MÃO. Sem isto o interceptor decidiria por eliminação —
        // sem usuário no contexto, `Sistema` — e a trilha diria que a rodada automática mudou o
        // plano desta empresa. Ver `AtorAuditoria`.
        trilha.Declarar(
            EntidadeAuditada.Empresa, empresaId,
            ativa is not null ? (ativa.Value ? AcaoAuditoria.Reativou : AcaoAuditoria.Desativou)
                              : AcaoAuditoria.Editou,
            AtorAuditoria.Operador, antes);

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        return await MontarAsync(empresaId, ct);
    }

    /// <summary>O teto é PORTEIRO, nunca executor: baixá-lo abaixo do uso não desliga ninguém e não
    /// apaga nada — só impede o crescimento. Mas é quase sempre erro de digitação, e por isso exige
    /// confirmação em vez de recusa definitiva.</summary>
    private static void ExigirConfirmacaoSeEncolhe(
        short limiteConexoes, int conexoesUsadas, short limiteUsuarios, int vagasUsadas)
    {
        var estouros = new List<string>();
        if (conexoesUsadas > limiteConexoes)
            estouros.Add($"{conexoesUsadas} conexões em uso para um limite de {limiteConexoes}");
        if (vagasUsadas > limiteUsuarios)
            estouros.Add($"{vagasUsadas} vagas ocupadas para um limite de {limiteUsuarios}");

        if (estouros.Count == 0) return;

        throw new RegraDeNegocioException(
            $"A empresa ficaria com {string.Join(" e ", estouros)}. "
            + "Ninguém perde acesso e nada é apagado — ela apenas não poderá incluir mais. "
            + "Reenvie com confirmação para aplicar.",
            conflito: true);
    }

    private async Task<LimitesDaEmpresa> MontarAsync(long empresaId, CancellationToken ct)
    {
        var e = await db.Empresas.AsNoTracking()
            .Where(x => x.Id == empresaId)
            .Select(x => new
            {
                x.Id, x.Nome, x.Ativo, x.PlanoId,
                PlanoNome = x.Plano != null ? x.Plano.Nome : null,
                x.LimiteConexoes, x.LimiteUsuarios
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Empresa não encontrada.");

        var (conexoes, vagas) = await UsoAsync(ct);

        return new LimitesDaEmpresa(
            e.Id, e.Nome, e.Ativo, e.PlanoId, e.PlanoNome,
            e.LimiteConexoes, conexoes, e.LimiteUsuarios, vagas);
    }

    /// <summary>O uso atual da empresa que está no contexto. Mesma regra de vaga do
    /// `ServicoEquipe`: contam ativo e convidado, inativo não.</summary>
    private async Task<(int Conexoes, int Vagas)> UsoAsync(CancellationToken ct) =>
    (
        await db.Conexoes.AsNoTracking().CountAsync(ct),
        await db.Usuarios.AsNoTracking().CountAsync(
            u => u.Status == StatusUsuario.Ativo || u.Status == StatusUsuario.Convidado, ct)
    );

    /// <summary>Checagem explícita antes do índice funcional: sem ela o operador levaria uma
    /// violação de índice crua, que não diz qual plano já existe nem que a comparação ignora a
    /// caixa.
    ///
    /// ⚠️ A MENSAGEM NOMEIA O PLANO QUE JÁ EXISTE, não o que foi digitado. Quem digitou
    /// "profissional" e lê 'já existe um plano chamado "profissional"' acha que o sistema está
    /// quebrado; lendo 'já existe "Profissional"' entende na hora que a comparação ignora a caixa.</summary>
    private async Task ExigirNomeLivreAsync(string nome, long? ignorarId, CancellationToken ct)
    {
        var existente = await db.Planos.AsNoTracking()
            .Where(p => p.Nome.ToLower() == nome.ToLower() && (ignorarId == null || p.Id != ignorarId))
            .Select(p => p.Nome)
            .FirstOrDefaultAsync(ct);

        if (existente is not null)
            throw new RegraDeNegocioException(
                $"Já existe um plano chamado \"{existente}\".", conflito: true);
    }

    private static string ValidarPlano(string? nome, decimal preco, short conexoes, short usuarios)
    {
        var limpo = (nome ?? "").Trim();
        if (limpo.Length == 0) throw new RegraDeNegocioException("Informe o nome do plano.");
        if (limpo.Length > 40) throw new RegraDeNegocioException("O nome do plano tem no máximo 40 caracteres.");
        if (preco < 0) throw new RegraDeNegocioException("O preço não pode ser negativo.");

        // Mesmas faixas dos CHECKs. Validar aqui é o que troca um erro cru do Postgres por uma
        // frase — e os tetos são freio de digitação, não capacidade técnica.
        if (conexoes is < 1 or > 20)
            throw new RegraDeNegocioException("O limite de conexões vai de 1 a 20.");
        if (usuarios is < 1 or > 50)
            throw new RegraDeNegocioException("O limite de usuários vai de 1 a 50.");

        return limpo;
    }

    /// <summary>⚠️ Lança alto em vez de ler errado. Ver o cabeçalho da classe.
    ///
    /// A condição é "o contexto tem tenant E ele NÃO veio do `Assumir`" — ou seja, veio de um JWT.
    /// Depois do primeiro `Assumir` numa requisição o contexto passa a ser não-zero legitimamente
    /// (o `ContextoEmpresaHttp` cai para o `ContextoDeFundo` quando não há claim), e uma checagem
    /// ingênua de `EmpresaId != 0` recusaria a segunda operação da mesma requisição.</summary>
    private void ExigirSemSessao()
    {
        var veioDeJwt = contexto.EmpresaId != 0 && fundo.EmpresaId == 0;
        if (veioDeJwt)
            throw new InvalidOperationException(
                "ServicoOperador foi alcançado de dentro de uma sessão de tenant. "
                + "Esta área não tem JWT por desenho — ver o cabeçalho da classe.");
    }
}
