using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Api.Controllers;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O vocabulário de etiquetas.
///
/// A regra que sustenta o resto é uma só: o nome é único por empresa, SEM diferenciar maiúscula
/// de minúscula. Ela é o que separa uma lista em que o dono confia de uma em que "Revendedor",
/// "revendedor" e "REVENDEDOR" convivem — e, quando o filtro da issue #3 chegar, é o que separa
/// uma busca que acha tudo de uma que acha um terço.
///
/// Ela é garantida em DOIS lugares de propósito, e há um teste para cada: o serviço, que devolve
/// mensagem legível, e o índice funcional `uq_etiquetas_nome`, que é quem de fato fecha a janela
/// entre a consulta e o `SaveChanges`.</summary>
[Collection("banco")]
public class EtiquetasDbTests(BancoTeste banco)
{
    // ==================================================================== papel
    [Fact]
    public void VENDEDOR_LE_A_LISTA_MAS_NAO_ESCREVE()
    {
        // ===================== ONDE ESTA REGRA VIVE =====================
        // O enforcement é o [Authorize(Roles="dono")] no controller, não no serviço. Testar sem
        // subir HTTP significa ler o ATRIBUTO — que é exatamente o artefato que decide.
        //
        // E a assimetria é o ponto deste bloco: criar etiqueta é configuração e define o
        // vocabulário da empresa; APLICAR é trabalho do dia, e para escolher qual aplicar o
        // vendedor precisa da lista. Fechar o GET tornaria a etiqueta inútil para quem a usa.
        // ===============================================================
        var tipo = typeof(EtiquetasController);

        foreach (var metodo in new[] { nameof(EtiquetasController.Criar),
                                       nameof(EtiquetasController.Atualizar),
                                       nameof(EtiquetasController.Remover) })
        {
            var atributo = tipo.GetMethod(metodo)!
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .FirstOrDefault();

            Assert.NotNull(atributo);
            Assert.Equal("dono", atributo!.Roles);
        }

        // O GET não restringe papel — e a ausência é deliberada, não esquecimento.
        var get = tipo.GetMethod(nameof(EtiquetasController.Listar))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
        Assert.DoesNotContain(get, a => a.Roles == "dono");
    }

    // ==================================================================== nome único
    [Fact]
    public async Task NOME_REPETIDO_EM_OUTRA_CAIXA_E_RECUSADO()
    {
        var (db, tx, s, _) = await PrepararAsync("nome-caixa");
        using var _1 = db; using var _2 = tx;

        await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);

        // "revendedor" e "Revendedor" são a MESMA etiqueta para quem lê a lista. Deixar as duas
        // entrarem é criar dois rótulos que parecem um só.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtiqueta("revendedor", null), default));

        Assert.Contains("Já existe", erro.Message);
    }

    [Fact]
    public async Task RENOMEAR_MANTENDO_O_PROPRIO_NOME_E_PERMITIDO()
    {
        // O `ignorarId` existe para isto: trocar só a cor não pode esbarrar no próprio nome.
        var (db, tx, s, _) = await PrepararAsync("nome-proprio");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta("Urgente", "#B4552F"), default);
        await s.AtualizarAsync(id, new EditarEtiqueta("Urgente", "#2E7A56"), default);

        var etiqueta = await db.Etiquetas.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal("Urgente", etiqueta.Nome);
        Assert.Equal("#2E7A56", etiqueta.Cor);
    }

    /// <summary>⚠️ ESTE É O TESTE QUE IMPORTA DOS DOIS. O serviço checa o nome em memória, e entre
    /// a consulta dele e o `SaveChanges` cabe outra requisição criando o mesmo nome. Quem fecha
    /// essa janela é o índice — e é ele que este teste exercita, passando POR CIMA do serviço.
    ///
    /// Se algum dia o índice sumir, o teste do serviço acima continuaria verde e a duplicata
    /// entraria em produção sob concorrência.</summary>
    [Fact]
    public async Task QUEM_GARANTE_O_NOME_UNICO_E_O_BANCO_NAO_O_SERVICO()
    {
        var (db, tx, _, cenario) = await PrepararAsync("nome-banco");
        using var _1 = db; using var _2 = tx;

        db.Etiquetas.Add(new Etiqueta { EmpresaId = cenario.Id, Nome = "VIP" });
        await db.SaveChangesAsync();

        db.Etiquetas.Add(new Etiqueta { EmpresaId = cenario.Id, Nome = "vip" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ==================================================================== tenant
    [Fact]
    public async Task O_MESMO_NOME_EM_OUTRA_EMPRESA_E_ACEITO()
    {
        // O índice é (empresa_id, lower(nome)). Duas padarias podem ter "Revendedor" — o
        // vocabulário é de cada uma.
        var (db, tx, s, primeira) = await PrepararAsync("tenant-a");
        using var _1 = db; using var _2 = tx;

        await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);

        var outra = await Semeador.TenantAsync(db, "etiquetas-tenant-b");
        db.Etiquetas.Add(new Etiqueta { EmpresaId = outra.Id, Nome = "Revendedor" });

        // Não estoura: o índice é por empresa.
        await db.SaveChangesAsync();

        var quantas = await db.Etiquetas.IgnoreQueryFilters()
            .CountAsync(e => e.Nome == "Revendedor"
                          && (e.EmpresaId == primeira.Id || e.EmpresaId == outra.Id));
        Assert.Equal(2, quantas);
    }

    [Fact]
    public async Task A_EMPRESA_NAO_ENXERGA_ETIQUETA_DE_OUTRA()
    {
        var (db, tx, s, _) = await PrepararAsync("tenant-isolado");
        using var _1 = db; using var _2 = tx;

        var outra = await Semeador.TenantAsync(db, "etiquetas-vizinha");
        db.Etiquetas.Add(new Etiqueta { EmpresaId = outra.Id, Nome = "Da vizinha" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var lista = await s.ListarAsync(default);
        Assert.DoesNotContain(lista, e => e.Nome == "Da vizinha");
    }

    // ==================================================================== validação
    [Theory]
    [InlineData(null, "#2F5D3A")]        // vazia vira o padrão
    [InlineData("", "#2F5D3A")]
    [InlineData("#b4552f", "#B4552F")]   // normaliza para maiúscula
    public async Task A_COR_VAZIA_VIRA_O_PADRAO_E_A_VALIDA_E_NORMALIZADA(string? informada, string esperada)
    {
        var (db, tx, s, _) = await PrepararAsync($"cor-{informada?.Length ?? 9}");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta("Teste", informada), default);

        var etiqueta = await db.Etiquetas.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal(esperada, etiqueta.Cor);
    }

    [Theory]
    [InlineData("vermelho")]
    [InlineData("#GGG")]
    [InlineData("#12345")]
    [InlineData("red; background: url(x)")]
    public async Task COR_QUE_NAO_E_HEXADECIMAL_E_RECUSADA(string cor)
    {
        // A cor vai direto para o `style` do chip. Texto livre aqui é o dono escrevendo CSS na
        // tela de todo mundo da empresa dele.
        var (db, tx, s, _) = await PrepararAsync($"cor-ruim-{cor.Length}");
        using var _1 = db; using var _2 = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtiqueta("Teste", cor), default));
    }

    [Fact]
    public async Task NOME_CURTO_DEMAIS_E_RECUSADO()
    {
        var (db, tx, s, _) = await PrepararAsync("nome-curto");
        using var _1 = db; using var _2 = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtiqueta(" a ", null), default));
    }

    // ==================================================================== lista e remoção
    [Fact]
    public async Task A_LISTA_VEM_ORDENADA_POR_NOME()
    {
        // Não há ordem manual: etiqueta não é etapa de funil e não tem sequência. Quem procura
        // "Urgente" numa lista procura em ordem alfabética.
        var (db, tx, s, _) = await PrepararAsync("ordem");
        using var _1 = db; using var _2 = tx;

        foreach (var nome in new[] { "Urgente", "Antigo", "Revendedor" })
            await s.CriarAsync(new NovaEtiqueta(nome, null), default);

        var lista = await s.ListarAsync(default);
        Assert.Equal(new[] { "Antigo", "Revendedor", "Urgente" }, lista.Select(e => e.Nome));
    }

    [Fact]
    public async Task APAGAR_NAO_DEIXA_RESTO()
    {
        var (db, tx, s, _) = await PrepararAsync("apagar");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta("Descartável", null), default);
        await s.RemoverAsync(id, default);
        db.ChangeTracker.Clear();

        Assert.False(await db.Etiquetas.AnyAsync(e => e.Id == id));

        // E o nome fica livre de novo — apagar não pode deixar o índice segurando um fantasma.
        await s.CriarAsync(new NovaEtiqueta("Descartável", null), default);
    }

    [Fact]
    public async Task APAGAR_O_QUE_NAO_EXISTE_DA_MENSAGEM_LEGIVEL()
    {
        var (db, tx, s, _) = await PrepararAsync("apagar-fantasma");
        using var _1 = db; using var _2 = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.RemoverAsync(999_999, default));
    }

    // ====================================================================
    private async Task<(NexoraDbContext, IDbContextTransaction, ServicoEtiquetas, Cenario)>
        PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"etiquetas-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new ServicoEtiquetas(db, ctx), cenario);
    }
}
