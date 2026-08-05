using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>A entrega central do bloco 2: provar que o isolamento multi-tenant vale para TODA
/// entidade nova, nas quatro operacoes que importam.
///
/// O isolamento inteiro depende de UM mecanismo — o HasQueryFilter global — e ele falha do
/// jeito mais perigoso possivel: em silencio, sem erro, devolvendo dados demais ou de menos.
/// Por isso os testes rodam contra Postgres REAL: provider in-memory nao reproduz query
/// filter combinado com SQL, indice parcial nem ON CONFLICT.</summary>
[Collection("banco")]
public class IsolamentoDominioDbTests(BancoTeste banco)
{
    // ----------------------------------------------------------------- 1. leitura
    [Fact]
    public async Task Consulta_do_tenant_A_nao_retorna_linha_do_tenant_B()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var (a, b) = await DoisTenantsAsync(db);

        ctx.EmpresaId = a.Id;

        await SoDoTenantAsync(db.Conexoes, a.Id);
        await SoDoTenantAsync(db.EtapasFunil, a.Id);
        await SoDoTenantAsync(db.Contatos, a.Id);
        await SoDoTenantAsync(db.Conversas, a.Id);
        await SoDoTenantAsync(db.Mensagens, a.Id);

        // Lembrete nao entra no cenario padrao: criado aqui nos dois tenants.
        db.Lembretes.Add(NovoLembrete(a));
        db.Lembretes.Add(NovoLembrete(b));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await SoDoTenantAsync(db.Lembretes, a.Id);

        // E o simetrico, para nao passar por acidente (ex.: se tudo voltasse vazio).
        ctx.EmpresaId = b.Id;
        await SoDoTenantAsync(db.Contatos, b.Id);
        await SoDoTenantAsync(db.Mensagens, b.Id);
    }

    // ----------------------------------------------------------------- 2. busca por id
    [Fact]
    public async Task Buscar_por_id_do_tenant_B_devolve_null_para_o_tenant_A()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var (a, b) = await DoisTenantsAsync(db);

        db.Lembretes.Add(NovoLembrete(b));
        await db.SaveChangesAsync();
        var lembreteDeB = await db.Lembretes.IgnoreQueryFilters()
            .Where(l => l.EmpresaId == b.Id).Select(l => l.Id).FirstAsync();
        db.ChangeTracker.Clear();

        ctx.EmpresaId = a.Id;

        // FindAsync respeita query filter? NAO — FindAsync pode devolver do cache do
        // ChangeTracker. Por isso a busca aqui e por consulta, que e o caminho real da
        // aplicacao. (O ChangeTracker foi limpo acima justamente para nao mascarar isso.)
        Assert.Null(await db.Conexoes.FirstOrDefaultAsync(x => x.Id == b.Conexao.Id));
        Assert.Null(await db.EtapasFunil.FirstOrDefaultAsync(x => x.Id == b.PrimeiraEtapa.Id));
        Assert.Null(await db.Contatos.FirstOrDefaultAsync(x => x.Id == b.Contato.Id));
        Assert.Null(await db.Conversas.FirstOrDefaultAsync(x => x.Id == b.Conversa.Id));
        Assert.Null(await db.Mensagens.FirstOrDefaultAsync(x => x.Id == b.Mensagem.Id));
        Assert.Null(await db.Lembretes.FirstOrDefaultAsync(x => x.Id == lembreteDeB));

        // ... e a mesma busca no proprio tenant acha.
        Assert.NotNull(await db.Contatos.FirstOrDefaultAsync(x => x.Id == a.Contato.Id));
    }

    // ----------------------------------------------------------------- 3. navegacao
    [Fact]
    public async Task Navegacao_a_partir_do_tenant_A_nunca_alcanca_linha_do_tenant_B()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var (a, b) = await DoisTenantsAsync(db);

        ctx.EmpresaId = a.Id;

        // Include atravessa a relacao; o filtro tem que valer na ponta tambem.
        var mensagens = await db.Mensagens.AsNoTracking()
            .Include(m => m.Conversa).Include(m => m.Contato).Include(m => m.Conexao)
            .ToListAsync();

        Assert.NotEmpty(mensagens);
        Assert.All(mensagens, m =>
        {
            Assert.Equal(a.Id, m.EmpresaId);
            Assert.Equal(a.Id, m.Conversa.EmpresaId);
            Assert.Equal(a.Id, m.Contato.EmpresaId);
            Assert.Equal(a.Id, m.Conexao.EmpresaId);
        });

        var contatos = await db.Contatos.AsNoTracking()
            .Include(c => c.Etapa).Include(c => c.Responsavel).ToListAsync();
        Assert.All(contatos, c =>
        {
            Assert.Equal(a.Id, c.Etapa.EmpresaId);
            Assert.Equal(a.Id, c.Responsavel!.EmpresaId);
        });

        // Projecao com subconsulta: o filtro precisa alcancar tambem o SELECT interno.
        var porContato = await db.Contatos.AsNoTracking()
            .Select(c => new { c.Id, Mensagens = db.Mensagens.Count(m => m.ContatoId == c.Id) })
            .ToListAsync();
        Assert.Single(porContato);
        Assert.Equal(1, porContato[0].Mensagens);

        // Prova que existe mesmo linha do outro tenant para ser encontrada, se o filtro falhar.
        Assert.Equal(2, await db.Contatos.IgnoreQueryFilters().CountAsync());
        Assert.NotEqual(a.Id, b.Id);
    }

    // ----------------------------------------------------------------- 4. escrita
    [Fact]
    public async Task Update_em_id_do_tenant_B_a_partir_do_tenant_A_nao_afeta_linha_nenhuma()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var (a, b) = await DoisTenantsAsync(db);

        ctx.EmpresaId = a.Id;

        // ExecuteUpdate roda no SQL, com o query filter aplicado: alvo de outro tenant nao e
        // encontrado e zero linhas mudam. E o caminho que mais assusta, porque um UPDATE que
        // "funciona" mas nao afeta nada nao levanta excecao.
        var afetadas = await db.Contatos
            .Where(c => c.Id == b.Contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Nome, "invadido"));
        Assert.Equal(0, afetadas);

        var afetadasConversa = await db.Conversas
            .Where(c => c.Id == b.Conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.NaoLidas, 99));
        Assert.Equal(0, afetadasConversa);

        var afetadasMensagem = await db.Mensagens
            .Where(m => m.Id == b.Mensagem.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Texto, "invadido"));
        Assert.Equal(0, afetadasMensagem);

        // Delete tambem nao alcanca.
        Assert.Equal(0, await db.Mensagens.Where(m => m.Id == b.Mensagem.Id).ExecuteDeleteAsync());

        // A linha do tenant B continua intacta.
        var intacta = await db.Contatos.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(c => c.Id == b.Contato.Id);
        Assert.Equal(b.Contato.Nome, intacta.Nome);

        // E no proprio tenant o mesmo UPDATE funciona — senao o teste passaria por engano.
        Assert.Equal(1, await db.Contatos.Where(c => c.Id == a.Contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Nome, "renomeado")));
    }

    // ----------------------------------------------------------------- 5. a armadilha
    [Fact]
    public async Task Tenant_zero_devolve_vazio_em_silencio_e_o_antidoto_e_filtro_explicito()
    {
        // ESTE TESTE E A REDE DE SEGURANCA DOS PROXIMOS BLOCOS.
        //
        // Webhook da Evolution e job de fundo rodam SEM requisicao autenticada: EmpresaId = 0.
        // O filtro global entao compara com 0 e a consulta volta VAZIA, sem erro nenhum. Quem
        // esquecer o IgnoreQueryFilters vai depurar "o banco esta vazio" por horas.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var (a, _) = await DoisTenantsAsync(db);

        ctx.EmpresaId = 0;
        Assert.False(ctx.EstaAutenticado);

        // Vazio. Nao lanca, nao avisa.
        Assert.Empty(await db.Conexoes.ToListAsync());
        Assert.Empty(await db.EtapasFunil.ToListAsync());
        Assert.Empty(await db.Contatos.ToListAsync());
        Assert.Empty(await db.Conversas.ToListAsync());
        Assert.Empty(await db.Mensagens.ToListAsync());
        Assert.Empty(await db.Lembretes.ToListAsync());

        // O ANTIDOTO, e o padrao que todo caminho sem tenant tem que seguir:
        // IgnoreQueryFilters() MAIS filtro explicito por empresaId.
        var conexoesDeA = await db.Conexoes.IgnoreQueryFilters()
            .Where(c => c.EmpresaId == a.Id).ToListAsync();
        Assert.Single(conexoesDeA);

        // O caso REAL do webhook: descobrir o tenant a partir do instance_name, que e unico
        // globalmente. Aqui o IgnoreQueryFilters sem Where e correto justamente porque a chave
        // ja e global — e o unico caso em que isso vale.
        var porInstancia = await db.Conexoes.IgnoreQueryFilters()
            .SingleAsync(c => c.InstanceName == a.Conexao.InstanceName);
        Assert.Equal(a.Id, porInstancia.EmpresaId);

        // E o contra-exemplo: IgnoreQueryFilters SEM filtro nenhum varre os dois tenants.
        Assert.Equal(2, await db.Contatos.IgnoreQueryFilters().CountAsync());
    }

    // ----------------------------------------------------------------- apoio
    private static async Task SoDoTenantAsync<T>(DbSet<T> conjunto, long empresaId)
        where T : class
    {
        var linhas = await conjunto.AsNoTracking().ToListAsync();
        Assert.NotEmpty(linhas);
        Assert.All(linhas, l =>
        {
            var prop = typeof(T).GetProperty("EmpresaId")!;
            Assert.Equal(empresaId, (long)prop.GetValue(l)!);
        });
    }

    private static Lembrete NovoLembrete(Cenario c) => new()
    {
        EmpresaId = c.Id,
        ContatoId = c.Contato.Id,
        ConversaId = c.Conversa.Id,
        Origem = OrigemLembrete.Manual,
        DataAlvo = DateOnly.FromDateTime(DateTime.UtcNow),
        Titulo = $"Ligar para {c.Contato.Nome}",
        ResponsavelId = c.Dono.Id
    };

    private static async Task<(Cenario A, Cenario B)> DoisTenantsAsync(NexoraDbContext db) =>
        (await Semeador.TenantAsync(db, "alfa"), await Semeador.TenantAsync(db, "beta"));
}
