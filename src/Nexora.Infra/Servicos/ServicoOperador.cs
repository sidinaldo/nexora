using Microsoft.EntityFrameworkCore;
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
