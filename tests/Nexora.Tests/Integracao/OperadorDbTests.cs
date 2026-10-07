using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core;
using Nexora.Core.Seguranca;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>OPE-1 — a área do operador.
///
/// ===================== O QUE ESTE ARQUIVO PROTEGE =====================
///   1. o PLANO É MOLDE. Atribuí-lo copia os limites; editá-lo depois não mexe em ninguém. É a
///      decisão central do bloco, e a que some numa refatoração bem-intencionada ("o plano deveria
///      ser a fonte da verdade");
///   2. a TRILHA diz `Operador`, e a linha existe na TABELA — não no objeto. Trocar a escrita por
///      `ExecuteUpdateAsync` é a otimização óbvia, e ela apaga a trilha em silêncio;
///   3. baixar um teto abaixo do uso EXIGE confirmação, e mesmo confirmado não desliga ninguém;
///   4. este serviço RECUSA rodar dentro de uma sessão de cliente.
/// ======================================================================</summary>
[Collection("banco")]
public class OperadorDbTests(BancoTeste banco)
{
    // ==================================================================== o plano é molde

    [Fact]
    public async Task ATRIBUIR_UM_PLANO_COPIA_OS_LIMITES_PARA_A_EMPRESA()
    {
        var (db, tx, amb) = await PrepararAsync("ope-atribui");
        using var _ = db; using var __ = tx;

        var plano = await amb.Operador.CriarPlanoAsync(new NovoPlano("Pro", 199m, 3, 10, 2), default);
        var depois = await amb.Operador.AtribuirPlanoAsync(amb.EmpresaId, plano, false, default);

        Assert.Equal((short)3, depois.LimiteConexoes);
        Assert.Equal((short)10, depois.LimiteUsuarios);
        Assert.Equal(plano, depois.PlanoId);
        Assert.Equal("Pro", depois.PlanoNome);
    }

    [Fact]
    public async Task EDITAR_O_PLANO_DEPOIS_NAO_MUDA_QUEM_JA_ESTA_NELE()
    {
        // ===================== A DECISÃO CENTRAL DESTE BLOCO =====================
        // Limite é CONTRATO. Contrato de quem assinou em março não muda porque a tabela de preços
        // mudou em agosto. Se este teste um dia falhar, alguém transformou o plano num ponteiro
        // vivo — e nesse dia uma edição de catálogo passa a mexer, de uma vez, no teto de todos os
        // clientes que estão nele, sem que ninguém peça.
        // ========================================================================
        var (db, tx, amb) = await PrepararAsync("ope-molde");
        using var _ = db; using var __ = tx;

        var plano = await amb.Operador.CriarPlanoAsync(new NovoPlano("Essencial", 99m, 1, 3, 1), default);
        await amb.Operador.AtribuirPlanoAsync(amb.EmpresaId, plano, false, default);

        await amb.Operador.AtualizarPlanoAsync(
            plano, new EditarPlano("Essencial", 149m, 5, 20, true, 1), default);

        var agora = await amb.Operador.LimitesAsync(amb.EmpresaId, default);

        Assert.Equal((short)1, agora.LimiteConexoes);   // continua o que foi COPIADO
        Assert.Equal((short)3, agora.LimiteUsuarios);
        Assert.Equal(plano, agora.PlanoId);             // e continua no plano
    }

    [Fact]
    public async Task AJUSTAR_OS_LIMITES_A_MAO_NAO_TIRA_A_EMPRESA_DO_PLANO()
    {
        // "Esse cliente negociou uma conexão a mais." Tirá-lo do plano apagaria o registro do que
        // foi contratado, e é justamente isso que alguém precisa saber ao renovar.
        var (db, tx, amb) = await PrepararAsync("ope-excecao");
        using var _ = db; using var __ = tx;

        var plano = await amb.Operador.CriarPlanoAsync(new NovoPlano("Basico", 79m, 1, 3, 1), default);
        await amb.Operador.AtribuirPlanoAsync(amb.EmpresaId, plano, false, default);

        var depois = await amb.Operador.AjustarLimitesAsync(
            amb.EmpresaId, new AjusteDeLimites(2, 3), default);

        Assert.Equal((short)2, depois.LimiteConexoes);
        Assert.Equal(plano, depois.PlanoId);
        Assert.Equal("Basico", depois.PlanoNome);
    }

    // ==================================================================== o excedente

    [Fact]
    public async Task BAIXAR_ABAIXO_DO_USO_EXIGE_CONFIRMACAO_E_EXPLICA_QUE_NINGUEM_PERDE_ACESSO()
    {
        var (db, tx, amb) = await PrepararAsync("ope-excedente");
        using var _ = db; using var __ = tx;

        await CriarUsuarioAsync(db, amb.EmpresaId, "sobra-a");
        await CriarUsuarioAsync(db, amb.EmpresaId, "sobra-b");   // dono + 2 = 3 vagas

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Operador.AjustarLimitesAsync(amb.EmpresaId, new AjusteDeLimites(1, 1), default));

        Assert.True(erro.Conflito);
        Assert.Contains("3 vagas ocupadas", erro.Message);
        // A frase que impede o operador de achar que vai desligar gente.
        Assert.Contains("Ninguém perde acesso", erro.Message);
    }

    [Fact]
    public async Task CONFIRMADO_O_EXCEDENTE_APLICA_E_NINGUEM_E_DESATIVADO()
    {
        var (db, tx, amb) = await PrepararAsync("ope-confirma");
        using var _ = db; using var __ = tx;

        await CriarUsuarioAsync(db, amb.EmpresaId, "fica-a");
        await CriarUsuarioAsync(db, amb.EmpresaId, "fica-b");

        var depois = await amb.Operador.AjustarLimitesAsync(
            amb.EmpresaId, new AjusteDeLimites(1, 1, ConfirmarExcedente: true), default);

        Assert.Equal((short)1, depois.LimiteUsuarios);
        Assert.Equal(3, depois.VagasUsadas);   // o uso aparece AO LADO do limite, e está acima dele

        // O teto é porteiro, nunca executor.
        Assert.Equal(3, await db.Usuarios.IgnoreQueryFilters()
            .CountAsync(u => u.EmpresaId == amb.EmpresaId && u.Status == StatusUsuario.Ativo));
    }

    // ==================================================================== a trilha

    [Fact]
    public async Task A_MUDANCA_GRAVA_TRILHA_NA_TABELA_COM_ATOR_OPERADOR()
    {
        // ⚠️ LÊ A TABELA `auditoria`, não o objeto. É o que derruba a "otimização" de trocar a
        // escrita por `ExecuteUpdateAsync` — ela pula os dois interceptors e a trilha some sem erro.
        var (db, tx, amb) = await PrepararAsync("ope-trilha");
        using var _ = db; using var __ = tx;

        await amb.Operador.AjustarLimitesAsync(amb.EmpresaId, new AjusteDeLimites(2, 9), default);

        var evento = Assert.Single(await db.Auditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Entidade == EntidadeAuditada.Empresa && a.EntidadeId == amb.EmpresaId)
            .ToListAsync());

        Assert.Equal(AtorAuditoria.Operador, evento.Ator);
        Assert.Null(evento.UsuarioId);
        Assert.Contains("limiteUsuarios", evento.Alteracoes);
    }

    [Fact]
    public async Task MANDAR_OS_MESMOS_VALORES_NAO_GRAVA_EVENTO_NENHUM()
    {
        // Trilha com evento vazio é ruído que torna a de verdade mais difícil de achar.
        var (db, tx, amb) = await PrepararAsync("ope-sem-mudanca");
        using var _ = db; using var __ = tx;

        var antes = await amb.Operador.LimitesAsync(amb.EmpresaId, default);
        await amb.Operador.AjustarLimitesAsync(
            amb.EmpresaId, new AjusteDeLimites(antes.LimiteConexoes, antes.LimiteUsuarios), default);

        Assert.Empty(await db.Auditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Entidade == EntidadeAuditada.Empresa && a.EntidadeId == amb.EmpresaId)
            .ToListAsync());
    }

    // ==================================================================== o catálogo

    [Fact]
    public async Task PLANO_ARQUIVADO_NAO_PODE_SER_ATRIBUIDO()
    {
        var (db, tx, amb) = await PrepararAsync("ope-arquivado");
        using var _ = db; using var __ = tx;

        var plano = await amb.Operador.CriarPlanoAsync(new NovoPlano("Antigo", 49m, 1, 3, 1), default);
        await amb.Operador.AtualizarPlanoAsync(
            plano, new EditarPlano("Antigo", 49m, 1, 3, false, 1), default);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Operador.AtribuirPlanoAsync(amb.EmpresaId, plano, false, default));

        Assert.Contains("arquivado", erro.Message);
    }

    [Fact]
    public async Task DOIS_PLANOS_COM_O_MESMO_NOME_SAO_RECUSADOS_COM_FRASE_E_NAO_COM_ERRO_DE_INDICE()
    {
        var (db, tx, amb) = await PrepararAsync("ope-nome");
        using var _ = db; using var __ = tx;

        await amb.Operador.CriarPlanoAsync(new NovoPlano("Profissional", 199m, 3, 10, 1), default);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Operador.CriarPlanoAsync(new NovoPlano("profissional", 199m, 3, 10, 1), default));

        Assert.True(erro.Conflito);
        Assert.Contains("Profissional", erro.Message);
    }

    [Fact]
    public async Task O_CATALOGO_DIZ_QUANTAS_EMPRESAS_ESTAO_EM_CADA_PLANO()
    {
        // É o número que responde "posso arquivar este plano sem deixar ninguém órfão". A tela não
        // tem esse dado, e deduzi-lo ali seria uma segunda cópia da regra.
        var (db, tx, amb) = await PrepararAsync("ope-contagem");
        using var _ = db; using var __ = tx;

        var usado = await amb.Operador.CriarPlanoAsync(new NovoPlano("Usado", 99m, 1, 3, 1), default);
        await amb.Operador.CriarPlanoAsync(new NovoPlano("Vazio", 59m, 1, 3, 2), default);
        await amb.Operador.AtribuirPlanoAsync(amb.EmpresaId, usado, false, default);

        var catalogo = await amb.Operador.ListarPlanosAsync(default);

        Assert.Equal(1, catalogo.Single(p => p.Nome == "Usado").EmpresasNoPlano);
        Assert.Equal(0, catalogo.Single(p => p.Nome == "Vazio").EmpresasNoPlano);
    }

    // ==================================================================== os números

    [Fact]
    public async Task A_LISTA_ATRAVESSA_AS_EMPRESAS_E_TRAZ_UMA_LINHA_DE_CADA()
    {
        // É a única leitura do sistema que atravessa tenants a pedido de um humano. O que a mantém
        // segura não é o SQL — é a rota ignorar o JWT e o `ExigirSemSessao` recusar sessão.
        var (db, tx, amb) = await PrepararAsync("ope-lista-a");
        using var _ = db; using var __ = tx;

        var segunda = await SegundaEmpresaAsync(db, amb, "ope-lista-b");

        var pagina = await amb.Operador.ListarEmpresasAsync(new FiltroEmpresas(Tamanho: 100), default);
        var ids = pagina.Itens.Select(i => i.Id).ToList();

        Assert.Contains(amb.EmpresaId, ids);
        Assert.Contains(segunda, ids);
    }

    /// <summary>A página ALÉM DO FIM traz o total certo, e não zero (AUD-XX, B5). O total vinha de
    /// `COUNT(*) OVER ()`, lido de dentro das linhas — e essa página não tem linha nenhuma.</summary>
    [Fact]
    public async Task A_LISTA_DE_EMPRESAS_ALEM_DO_FIM_TRAZ_O_TOTAL_CERTO()
    {
        var (db, tx, amb) = await PrepararAsync("ope-alem");
        using var _ = db; using var __ = tx;

        var todas = await amb.Operador.ListarEmpresasAsync(new FiltroEmpresas(Tamanho: 100), default);
        Assert.True(todas.Total > 0);

        var alemDoFim = await amb.Operador.ListarEmpresasAsync(
            new FiltroEmpresas(Pagina: 100_000, Tamanho: 1), default);

        Assert.Empty(alemDoFim.Itens);
        Assert.Equal(todas.Total, alemDoFim.Total);
    }

    [Fact]
    public async Task OS_NUMEROS_DE_CAPACIDADE_BATEM_COM_O_QUE_A_EMPRESA_TEM()
    {
        var (db, tx, amb) = await PrepararAsync("ope-numeros");
        using var _ = db; using var __ = tx;

        await CriarUsuarioAsync(db, amb.EmpresaId, "num-a");               // + 1 ativo
        await ConvidadoAsync(db, amb.EmpresaId, "num-conv");               // + 1 convidado
        await InativoAsync(db, amb.EmpresaId, "num-inat");                 // não conta

        var linha = (await amb.Operador.ListarEmpresasAsync(
            new FiltroEmpresas(Tamanho: 100), default)).Itens.Single(i => i.Id == amb.EmpresaId);

        Assert.Equal(2, linha.UsuariosAtivos);        // o dono + o criado
        Assert.Equal(1, linha.UsuariosConvidados);
        Assert.Equal(3, linha.VagasUsadas);           // inativo fica de fora, como na cota
        Assert.Equal(1, linha.Conexoes);              // a do Semeador
        Assert.Equal(1, linha.Contatos);
    }

    [Fact]
    public async Task LIMITES_PERSONALIZADOS_APARECEM_QUANDO_FOGEM_DO_MOLDE()
    {
        // É o que diz ao operador "esta empresa não é mais o plano dela". Sem isso, dois clientes no
        // mesmo plano com tetos diferentes seriam indistinguíveis na lista.
        var (db, tx, amb) = await PrepararAsync("ope-personalizado");
        using var _ = db; using var __ = tx;

        var plano = await amb.Operador.CriarPlanoAsync(new NovoPlano("Padrao", 99m, 1, 3, 1), default);
        await amb.Operador.AtribuirPlanoAsync(amb.EmpresaId, plano, false, default);

        var antes = (await amb.Operador.ListarEmpresasAsync(new FiltroEmpresas(Tamanho: 100), default))
            .Itens.Single(i => i.Id == amb.EmpresaId);
        Assert.False(antes.LimitesPersonalizados);

        await amb.Operador.AjustarLimitesAsync(amb.EmpresaId, new AjusteDeLimites(2, 3), default);

        var depois = (await amb.Operador.ListarEmpresasAsync(new FiltroEmpresas(Tamanho: 100), default))
            .Itens.Single(i => i.Id == amb.EmpresaId);
        Assert.True(depois.LimitesPersonalizados);
    }

    [Fact]
    public async Task A_PAGINACAO_DEVOLVE_O_TOTAL_DE_TODAS_E_NAO_O_DA_PAGINA()
    {
        // O total vem de `COUNT(*) OVER ()` na mesma ida ao banco. Se ele passar a contar só a
        // página, a tela diz "1 empresa" e some com o resto da carteira.
        var (db, tx, amb) = await PrepararAsync("ope-pag-a");
        using var _ = db; using var __ = tx;

        await SegundaEmpresaAsync(db, amb, "ope-pag-b");

        var pagina = await amb.Operador.ListarEmpresasAsync(
            new FiltroEmpresas(Pagina: 1, Tamanho: 1), default);

        Assert.Single(pagina.Itens);
        Assert.True(pagina.Total >= 2, $"total veio {pagina.Total}");
    }

    // ==================================================================== o isolamento

    [Fact]
    public async Task O_SERVICO_RECUSA_RODAR_DENTRO_DE_UMA_SESSAO_DE_CLIENTE()
    {
        // ===================== A BARREIRA QUE IMPORTA =====================
        // Alcançado de dentro de uma requisição autenticada, o `Assumir` trocaria o tenant daquela
        // requisição no meio do caminho — e o resto dela passaria a ler e escrever na empresa
        // errada, sem erro nenhum. Lança alto em vez de ler errado.
        // =================================================================
        var fundo = new ContextoDeFundo();
        var ctx = new ContextoMutavel();                 // este simula um JWT de cliente
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var c = await Semeador.TenantAsync(db, "ope-sessao");
        ctx.EmpresaId = c.Id;                            // sessão de cliente ativa
        ctx.UsuarioId = c.Dono.Id;

        var servico = new ServicoOperador(db, ctx, fundo, new ColetorAuditoria(), new IdentidadeDoOperador());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => servico.ListarPlanosAsync(default));
    }

    // ==================================================================== auxiliares

    private async Task<long> SegundaEmpresaAsync(NexoraDbContext db, Ambiente amb, string sufixo)
    {
        amb.Fundo.Assumir(0, 0);
        var outra = await Semeador.TenantAsync(db, sufixo);
        amb.Fundo.Assumir(0, 0);
        return outra.Id;
    }

    private static Task ConvidadoAsync(NexoraDbContext db, long empresaId, string marca) =>
        AdicionarAsync(db, empresaId, marca, StatusUsuario.Convidado);

    private static Task InativoAsync(NexoraDbContext db, long empresaId, string marca) =>
        AdicionarAsync(db, empresaId, marca, StatusUsuario.Inativo);

    private static async Task AdicionarAsync(
        NexoraDbContext db, long empresaId, string marca, StatusUsuario status)
    {
        db.Usuarios.Add(new Usuario
        {
            EmpresaId = empresaId,
            Nome = marca,
            Email = $"{marca}-{empresaId}@teste.local",
            Papel = PapelUsuario.Vendedor,
            Status = status,
            SenhaHash = status == StatusUsuario.Convidado ? null : "pbkdf2$1$x$y"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task CriarUsuarioAsync(NexoraDbContext db, long empresaId, string marca)
    {
        db.Usuarios.Add(new Usuario
        {
            EmpresaId = empresaId,
            Nome = marca,
            Email = $"{marca}-{empresaId}@teste.local",
            Papel = PapelUsuario.Vendedor,
            Status = StatusUsuario.Ativo,
            SenhaHash = "pbkdf2$1$x$y"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>O contexto da área do operador, igual ao de produção: sem claim nenhum, ele CAI
    /// para o `ContextoDeFundo`.
    ///
    /// ⚠️ Isto não é conveniência de teste — é o acoplamento real. `ContextoEmpresaHttp.EmpresaId`
    /// devolve o `fundo.EmpresaId` quando não há claim, e é disso que o `Assumir` depende para o
    /// filtro global passar a enxergar a empresa. Um teste com `ContextoMutavel` puro não
    /// reproduziria isso, e passaria a medir outra coisa.</summary>
    private sealed class ContextoDoOperador(ContextoDeFundo fundo) : IContextoEmpresa
    {
        public long EmpresaId => fundo.EmpresaId;
        public long UsuarioId => fundo.UsuarioId;
        public string? Papel => null;

        /// <summary>O operador interno não tem papel nem exceção: ele não é um `usuarios`. Ver
        /// `AtorAuditoria.Operador` — é um rótulo de trilha, e nada mais.</summary>
        public IReadOnlyDictionary<Permissao, bool>? ExcecoesDePermissao => null;

        public bool EstaAutenticado => EmpresaId != 0;
    }

    private sealed record Ambiente(
        long EmpresaId, IServicoOperador Operador, ContextoDeFundo Fundo,
        IdentidadeDoOperador Identidade);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var fundo = new ContextoDeFundo();
        var ctx = new ContextoDoOperador(fundo);
        var coletor = new ColetorAuditoria();
        var db = banco.NovoContexto(ctx, coletor: coletor);
        var tx = await db.Database.BeginTransactionAsync();

        // A semente precisa de tenant no contexto para as leituras dela; depois volta a zero,
        // que é como uma requisição do operador começa.
        fundo.Assumir(0, 0);
        var cenario = await Semeador.TenantAsync(db, sufixo);
        fundo.Assumir(0, 0);

        var identidade = new IdentidadeDoOperador();

        return (db, tx, new Ambiente(
            cenario.Id, new ServicoOperador(db, ctx, fundo, coletor, identidade), fundo, identidade));
    }
}
