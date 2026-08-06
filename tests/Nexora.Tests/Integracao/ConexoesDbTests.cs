using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>MULTI-NÚMERO (ARQ-2).
///
/// O que estes testes protegem são as quatro regras que passaram a existir quando
/// `uq_conexoes_empresa` saiu do schema:
///
///   1. o TETO vem do plano (`empresas.limite_conexoes`), e é a aplicação que o aplica;
///   2. o NOME é único dentro da empresa — a lista precisa dizer qual número é qual;
///   3. apagar só é permitido sem histórico, e NUNCA a última conexão;
///   4. `instance_name` é derivado, imutável e nunca reaproveitado.
///
/// A quarta é a mais silenciosa das quatro: reaproveitar o nome de uma conexão apagada faria a
/// conexão nova adotar a sessão da instância antiga na Evolution, sem erro em lugar nenhum.</summary>
[Collection("banco")]
public class ConexoesDbTests(BancoTeste banco)
{
    // ==================================================================== limite do plano
    [Fact]
    public async Task SEGUNDA_CONEXAO_E_RECUSADA_QUANDO_O_PLANO_PERMITE_UMA()
    {
        var (db, tx, s, _, _) = await PrepararAsync("limite-1");
        using var _1 = db; using var _2 = tx;

        // O cenário já vem com uma conexão, e `limite_conexoes` nasce em 1.
        var antes = await s.ListarAsync(default);
        Assert.Single(antes.Itens);
        Assert.Equal(1, antes.Limite);
        Assert.False(antes.PodeAdicionar);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaConexao("Segunda"), default));
        Assert.True(erro.Conflito);

        db.ChangeTracker.Clear();
        Assert.Single((await s.ListarAsync(default)).Itens);
    }

    [Fact]
    public async Task SUBIR_O_LIMITE_LIBERA_A_SEGUNDA_SEM_MIGRATION()
    {
        // ===================== O PONTO DA COLUNA =====================
        // O teto mudou por UPDATE numa coluna, não por alteração de índice. É exatamente isso que
        // se perderia se o limite tivesse ficado no schema: trocar de plano viraria migration.
        // =============================================================
        var (db, tx, s, cenario, _) = await PrepararAsync("limite-3");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 3);

        var id = await s.CriarAsync(new NovaConexao("Suporte"), default);
        db.ChangeTracker.Clear();

        var depois = await s.ListarAsync(default);
        Assert.Equal(2, depois.Itens.Count);
        Assert.Equal(3, depois.Limite);
        Assert.True(depois.PodeAdicionar);
        Assert.Equal("Suporte", depois.Itens.Single(c => c.Id == id).Nome);
    }

    [Fact]
    public async Task Conexao_nova_nasce_sem_numero_e_nao_criada()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("nova-crua");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 2);
        var id = await s.CriarAsync(new NovaConexao("Vendas"), default);
        db.ChangeTracker.Clear();

        var nova = (await s.ListarAsync(default)).Itens.Single(c => c.Id == id);

        // Criar NÃO pareia: quem pareia é o QR, e é por isso que a tela abre o painel logo depois.
        Assert.Null(nova.Numero);
        Assert.Equal("nao_criada", nova.Status);
        Assert.Equal(0, nova.Conversas);
        Assert.True(nova.PodeRemover);
        Assert.Null(nova.MotivoNaoRemove);
    }

    // ==================================================================== nome
    [Fact]
    public async Task NOME_REPETIDO_E_RECUSADO_INCLUSIVE_SO_COM_CAIXA_DIFERENTE()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("nome");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 4);

        // "Principal" é o nome que o semeador dá à conexão do cadastro.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaConexao("Principal"), default));

        // E a diferença SÓ de caixa também: `uq_conexoes_empresa_nome` deixaria passar, e o olho
        // de quem vai apagar um dos dois não distingue.
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaConexao("principal"), default));

        db.ChangeTracker.Clear();
        Assert.Single((await s.ListarAsync(default)).Itens);
    }

    [Fact]
    public async Task Renomear_troca_o_nome_e_NAO_toca_no_instance_name()
    {
        // ===================== A REGRA QUE NÃO TEM ENDPOINT =====================
        // `instance_name` é a identidade na Evolution e a chave pela qual o webhook acha o tenant.
        // Renomear a instância orfanaria a sessão e o sistema pararia de receber mensagem EM
        // SILÊNCIO — sem erro, sem log, até alguém reclamar que não foi respondido.
        // =======================================================================
        var (db, tx, s, cenario, _) = await PrepararAsync("renomear");
        using var _1 = db; using var _2 = tx;

        var antes = (await s.ListarAsync(default)).Itens.Single();

        await s.RenomearAsync(antes.Id, "  Loja do Centro  ", default);
        db.ChangeTracker.Clear();

        var depois = (await s.ListarAsync(default)).Itens.Single();
        Assert.Equal("Loja do Centro", depois.Nome);          // e com trim
        Assert.Equal(antes.InstanceName, depois.InstanceName);
    }

    [Fact]
    public async Task Renomear_para_o_nome_de_outra_conexao_e_recusado()
    {
        var (db, tx, s, cenario, _) = await PrepararAsync("renomear-colide");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 2);
        var id = await s.CriarAsync(new NovaConexao("Suporte"), default);
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RenomearAsync(id, "Principal", default));

        db.ChangeTracker.Clear();
        Assert.Equal("Suporte", (await s.ListarAsync(default)).Itens.Single(c => c.Id == id).Nome);
    }

    [Fact]
    public async Task Renomear_para_o_PROPRIO_nome_passa()
    {
        // O `ignorarId` existe para isto: salvar sem mudar nada não pode virar "nome já existe".
        var (db, tx, s, _, _) = await PrepararAsync("renomear-self");
        using var _1 = db; using var _2 = tx;

        var c = (await s.ListarAsync(default)).Itens.Single();
        await s.RenomearAsync(c.Id, c.Nome, default);

        db.ChangeTracker.Clear();
        Assert.Equal(c.Nome, (await s.ListarAsync(default)).Itens.Single().Nome);
    }

    // ==================================================================== instance_name
    [Fact]
    public async Task INSTANCE_NAME_E_DERIVADO_DO_ID_E_NUNCA_REAPROVEITADO()
    {
        // ===================== O VAZAMENTO QUE ISTO IMPEDE =====================
        // Se o nome da instância viesse de um contador ("empresa-2", "empresa-3"), apagar a
        // terceira e criar outra devolveria "empresa-3" — e a instância antiga pode ainda existir
        // do lado da Evolution. A conexão nova adotaria a sessão dela em silêncio.
        // ======================================================================
        var (db, tx, s, cenario, cliente) = await PrepararAsync("instancia");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 3);

        var idA = await s.CriarAsync(new NovaConexao("Primeira extra"), default);
        db.ChangeTracker.Clear();
        var nomeA = (await s.ListarAsync(default)).Itens.Single(c => c.Id == idA).InstanceName;

        Assert.Equal($"emp-{cenario.Id}-{idA}", nomeA);
        Assert.DoesNotContain("pendente-", nomeA);   // o provisório não pode sobreviver

        await s.RemoverAsync(idA, default);
        db.ChangeTracker.Clear();

        var idB = await s.CriarAsync(new NovaConexao("Segunda extra"), default);
        db.ChangeTracker.Clear();
        var nomeB = (await s.ListarAsync(default)).Itens.Single(c => c.Id == idB).InstanceName;

        Assert.NotEqual(nomeA, nomeB);
        Assert.Contains(nomeA, cliente.InstanciasRemovidas);   // e a instância foi apagada lá
    }

    // ==================================================================== remover
    [Fact]
    public async Task A_ULTIMA_CONEXAO_NAO_PODE_SER_APAGADA()
    {
        // ===================== A INVARIANTE QUE O BANCO NÃO GARANTE =====================
        // Sem nenhuma conexão o webhook não acha o tenant (ele casa por `instance_name`), o envio
        // não tem instância, e NADA no sistema recria uma — a criação só acontece no cadastro da
        // empresa. A conta ficaria sem caminho de volta.
        // ===============================================================================
        var (db, tx, s, _, cliente) = await PrepararAsync("ultima");
        using var _1 = db; using var _2 = tx;

        var unica = (await s.ListarAsync(default)).Itens.Single();
        Assert.False(unica.PodeRemover);
        Assert.Contains("única conexão", unica.MotivoNaoRemove);

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.RemoverAsync(unica.Id, default));

        db.ChangeTracker.Clear();
        Assert.Single((await s.ListarAsync(default)).Itens);
        // E a instância NÃO foi tocada na Evolution: a recusa acontece antes.
        Assert.Empty(cliente.InstanciasRemovidas);
    }

    [Fact]
    public async Task CONEXAO_COM_CONVERSA_NAO_PODE_SER_APAGADA()
    {
        var (db, tx, s, cenario, cliente) = await PrepararAsync("com-historico");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 2);
        var id = await s.CriarAsync(new NovaConexao("Com histórico"), default);

        // A conversa semeada passa a pertencer à nova conexão.
        await db.Conversas.IgnoreQueryFilters()
            .Where(c => c.Id == cenario.Conversa.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.ConexaoId, id));
        db.ChangeTracker.Clear();

        var alvo = (await s.ListarAsync(default)).Itens.Single(c => c.Id == id);
        Assert.Equal(1, alvo.Conversas);
        Assert.False(alvo.PodeRemover);
        Assert.Contains("histórico", alvo.MotivoNaoRemove);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.RemoverAsync(id, default));

        // O MESMO texto que a lista mostrou no botão. Duas mensagens diferentes para a mesma
        // recusa seriam duas cópias da regra, e elas divergiriam.
        Assert.Equal(alvo.MotivoNaoRemove, erro.Message);

        db.ChangeTracker.Clear();
        Assert.Equal(2, (await s.ListarAsync(default)).Itens.Count);
        Assert.Empty(cliente.InstanciasRemovidas);
    }

    [Fact]
    public async Task CONEXAO_COM_MENSAGEM_MAS_SEM_CONVERSA_TAMBEM_E_RECUSADA()
    {
        // A FK de `mensagens` é RESTRICT do mesmo jeito. Olhar só conversas deixaria passar o
        // caso em que a mensagem foi movida ou a conversa apagada — e o erro chegaria como 500.
        var (db, tx, s, cenario, _) = await PrepararAsync("so-mensagem");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 2);
        var id = await s.CriarAsync(new NovaConexao("Só mensagem"), default);

        await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.Id == cenario.Mensagem.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(m => m.ConexaoId, id));
        db.ChangeTracker.Clear();

        var alvo = (await s.ListarAsync(default)).Itens.Single(c => c.Id == id);
        Assert.Equal(0, alvo.Conversas);          // a conversa continua na outra
        Assert.False(alvo.PodeRemover);
        Assert.Contains("mensagens no histórico", alvo.MotivoNaoRemove);

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.RemoverAsync(id, default));
    }

    [Fact]
    public async Task CONEXAO_SEM_HISTORICO_E_APAGADA_DOS_DOIS_LADOS()
    {
        var (db, tx, s, cenario, cliente) = await PrepararAsync("apagar-ok");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 2);
        var id = await s.CriarAsync(new NovaConexao("Descartável"), default);
        db.ChangeTracker.Clear();

        var instancia = (await s.ListarAsync(default)).Itens.Single(c => c.Id == id).InstanceName;

        await s.RemoverAsync(id, default);
        db.ChangeTracker.Clear();

        Assert.Single((await s.ListarAsync(default)).Itens);
        Assert.Null(await s.ObterAsync(id, default));

        // A instância foi apagada NA EVOLUTION também. Sem isto ela ficaria viva, pareada, e
        // mandando webhook de uma instância que ninguém mais reconhece — e sem o nome guardado
        // em lugar nenhum para alguém limpar depois.
        Assert.Equal([instancia], cliente.InstanciasRemovidas);
    }

    // ==================================================================== saúde por conexão
    [Fact]
    public async Task SAUDE_E_DA_CONEXAO_E_NAO_DA_EMPRESA()
    {
        // ===================== O QUE O TOTAL ESCONDERIA =====================
        // Com N números, somar a empresa inteira faz a soma continuar parecendo saudável quando
        // UM dos números está falhando — que é justamente o que a tela existe para mostrar.
        // ====================================================================
        var (db, tx, s, cenario, _) = await PrepararAsync("saude");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 2);
        var outraId = await s.CriarAsync(new NovaConexao("Outra"), default);
        db.ChangeTracker.Clear();

        // Uma saída de hoje na conexão do cenário.
        db.Mensagens.Add(new Mensagem
        {
            EmpresaId = cenario.Id,
            ConversaId = cenario.Conversa.Id,
            ContatoId = cenario.Contato.Id,
            ConexaoId = cenario.Conexao.Id,
            InstanceName = cenario.Conexao.InstanceName,
            Direcao = DirecaoMensagem.Saida,
            Texto = "saiu por aqui",
            WaMessageId = "WA-SAUDE-1",
            EnviadaEm = DateTime.UtcNow,
            DataDisparo = DateOnly.FromDateTime(DateTime.UtcNow)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(1, (await s.SaudeAsync(cenario.Conexao.Id, default)).EnviadasHoje);
        Assert.Equal(0, (await s.SaudeAsync(outraId, default)).EnviadasHoje);
    }

    // ==================================================================== banner do painel
    [Fact]
    public async Task BANNER_ACENDE_SE_ALGUMA_PAREADA_CAIU_E_DIZ_QUAL()
    {
        // ===================== O QUE "ALGUMA" CONSERTA =====================
        // Com dois números, exigir que os DOIS caiam para avisar significa que o vendedor digita
        // resposta num número morto enquanto o painel diz que está tudo bem. E um banner que só
        // diz "WhatsApp desconectado" numa empresa com três números é um aviso que não diz o que
        // fazer — por isso vão os NOMES junto.
        // ==================================================================
        var (db, tx, s, cenario, _) = await PrepararAsync("banner");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 3);

        // Tudo no ar: a do cenário nasce conectada.
        var painel = new ServicoPainel(db, TimeProvider.System);
        Assert.True((await painel.StatusAsync(default)).WhatsappConectado);

        // Uma NOVA, nunca pareada: NÃO acende. Ela não caiu, ela ainda não subiu — e dizer o
        // contrário poria um alerta vermelho no topo de todas as telas de quem acabou de criar.
        var novaId = await s.CriarAsync(new NovaConexao("Suporte"), default);
        db.ChangeTracker.Clear();

        var comNova = await painel.StatusAsync(default);
        Assert.True(comNova.WhatsappConectado);
        Assert.Empty(comNova.ConexoesCaidas);

        // Agora a nova PAREIA e cai.
        await db.Conexoes.IgnoreQueryFilters().Where(c => c.Id == novaId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(c => c.Numero, "5584911110000")
                .SetProperty(c => c.Status, StatusConexao.Desconectado));
        db.ChangeTracker.Clear();

        var caiu = await painel.StatusAsync(default);
        Assert.False(caiu.WhatsappConectado);          // uma só basta
        Assert.Equal(["Suporte"], caiu.ConexoesCaidas);
    }

    [Fact]
    public async Task Troca_de_chip_em_QUALQUER_conexao_acende_o_aviso()
    {
        // Perguntar só à primeira esconderia a troca nas outras, e o aviso existe justamente para
        // o dono conferir que o número certo entrou.
        var (db, tx, s, cenario, _) = await PrepararAsync("troca");
        using var _1 = db; using var _2 = tx;

        await SubirLimiteAsync(db, cenario.Id, 2);
        var novaId = await s.CriarAsync(new NovaConexao("Suporte"), default);
        db.ChangeTracker.Clear();

        var painel = new ServicoPainel(db, TimeProvider.System);
        Assert.False((await painel.StatusAsync(default)).TrocouDeNumero);

        // A SEGUNDA trocou de chip — a primeira (que vem antes na lista) não.
        await db.Conexoes.IgnoreQueryFilters().Where(c => c.Id == novaId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(c => c.Numero, "5584911110000")
                .SetProperty(c => c.NumeroAnterior, "5584922220000")
                .SetProperty(c => c.Status, StatusConexao.Conectado));
        db.ChangeTracker.Clear();

        Assert.True((await painel.StatusAsync(default)).TrocouDeNumero);
    }

    // ==================================================================== isolamento
    [Fact]
    public async Task CONEXAO_DE_OUTRA_EMPRESA_NAO_EXISTE_POR_NENHUM_CAMINHO()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await Semeador.TenantAsync(db, "conex-isol-a");
        var outra = await Semeador.TenantAsync(db, "conex-isol-b");

        ctx.EmpresaId = minha.Id;
        ctx.UsuarioId = minha.Dono.Id;
        ctx.Papel = "dono";

        var cliente = new ClienteWhatsAppFalso();
        var s = new ServicoConexoes(db, cliente, ctx, TimeProvider.System);
        var alheia = outra.Conexao.Id;

        Assert.Null(await s.ObterAsync(alheia, default));

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.RenomearAsync(alheia, "Roubada", default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.RemoverAsync(alheia, default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.StatusAsync(alheia, default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.ConectarAsync(alheia, default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.DesconectarAsync(alheia, default));
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.ReconhecerTrocaAsync(alheia, default));

        // `SaudeAsync` é o que mais importa aqui: sem a checagem de dono ele devolveria ZEROS —
        // resposta que parece legítima e esconde que a pergunta era sobre outro tenant.
        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.SaudeAsync(alheia, default));

        db.ChangeTracker.Clear();
        Assert.Equal("Principal", (await db.Conexoes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.Id == alheia)).Nome);
        Assert.Empty(cliente.InstanciasRemovidas);

        // E a lista continua vendo só as próprias.
        Assert.All((await s.ListarAsync(default)).Itens, c => Assert.NotEqual(alheia, c.Id));
    }

    // ==================================================================== apoio

    /// <summary>Sobe o teto do plano. É um UPDATE de coluna de propósito — é exatamente o que o
    /// ARQ-2 comprou ao tirar `uq_conexoes_empresa` do schema.</summary>
    private static async Task SubirLimiteAsync(NexoraDbContext db, long empresaId, short limite)
    {
        await db.Empresas.IgnoreQueryFilters()
            .Where(e => e.Id == empresaId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LimiteConexoes, limite));
        db.ChangeTracker.Clear();
    }

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, IServicoConexoes Servico,
        Cenario Cenario, ClienteWhatsAppFalso Cliente)> PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"conex-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var cliente = new ClienteWhatsAppFalso();
        return (db, tx, new ServicoConexoes(db, cliente, ctx, TimeProvider.System), cenario, cliente);
    }
}
