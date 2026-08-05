using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>Empresa sem etapa e empresa quebrada: o kanban nao renderiza e Contato.EtapaId e
/// NOT NULL. Por isso o seed nao pode ser um passo separado que alguem esquece.</summary>
[Collection("banco")]
public class CadastroEmpresaDbTests(BancoTeste banco)
{
    private static NovaEmpresa Nova(string sufixo) => new(
        Nome: $"Padaria {sufixo}",
        Documento: "12.345.678/0001-99",
        NomeDono: "Ana Souza",
        EmailDono: $"ana-{sufixo}@padaria.com",
        Senha: "senha-forte-123");

    [Fact]
    public async Task Cadastrar_cria_as_5_etapas_na_ordem_certa_com_uma_de_ganho()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var empresaId = await new ServicoCadastroEmpresa(db).CadastrarAsync(Nova("etapas"), default);

        ctx.EmpresaId = empresaId;
        db.ChangeTracker.Clear();

        var etapas = await db.EtapasFunil.AsNoTracking().OrderBy(e => e.Ordem).ToListAsync();

        Assert.Equal(5, etapas.Count);
        Assert.Equal(
            ["Novo Lead", "Primeiro Atendimento", "Proposta", "Negociação", "Venda"],
            etapas.Select(e => e.Nome));
        Assert.Equal([1, 2, 3, 4, 5], etapas.Select(e => (int)e.Ordem));

        // Exatamente uma terminal de ganho, e e a ultima.
        Assert.Single(etapas.Where(e => e.EGanho));
        Assert.Equal("Venda", etapas.Single(e => e.EGanho).Nome);

        // Cor preenchida em todas — o kanban depende dela para renderizar a coluna.
        Assert.All(etapas, e => Assert.StartsWith("#", e.Cor));
    }

    [Fact]
    public async Task Cadastrar_cria_o_usuario_dono_com_senha_utilizavel()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var empresaId = await new ServicoCadastroEmpresa(db).CadastrarAsync(Nova("dono"), default);

        ctx.EmpresaId = empresaId;
        db.ChangeTracker.Clear();

        var usuarios = await db.Usuarios.AsNoTracking().ToListAsync();
        var dono = Assert.Single(usuarios);

        Assert.Equal("Ana Souza", dono.Nome);
        Assert.Equal(PapelUsuario.Dono, dono.Papel);
        Assert.Equal(StatusUsuario.Ativo, dono.Status);
        Assert.True(HashSenha.Confere("senha-forte-123", dono.SenhaHash));
    }

    [Fact]
    public async Task Cadastrar_cria_a_conexao_com_instancia_derivada_do_id()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var empresaId = await new ServicoCadastroEmpresa(db).CadastrarAsync(Nova("conex"), default);

        ctx.EmpresaId = empresaId;
        db.ChangeTracker.Clear();

        var conexao = Assert.Single(await db.Conexoes.AsNoTracking().ToListAsync());
        Assert.Equal($"emp-{empresaId}", conexao.InstanceName);
        Assert.Equal(StatusConexao.NaoCriada, conexao.Status);
        Assert.Null(conexao.Numero);   // so o pareamento preenche
    }

    [Fact]
    public async Task Documento_e_gravado_so_com_digitos()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var empresaId = await new ServicoCadastroEmpresa(db).CadastrarAsync(Nova("doc"), default);

        ctx.EmpresaId = empresaId;
        db.ChangeTracker.Clear();

        var empresa = await db.Empresas.AsNoTracking().SingleAsync();
        Assert.Equal("12345678000199", empresa.Documento);
    }

    [Fact]
    public async Task E_mail_ja_usado_em_outro_tenant_e_recusado()
    {
        // O e-mail e unico GLOBALMENTE (o login busca por ele sem tenant no contexto). A
        // checagem roda sem tenant e depende de IgnoreQueryFilters — sem ele voltaria vazia e
        // o cadastro so estouraria no indice unico, com erro ilegivel.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var servico = new ServicoCadastroEmpresa(db);

        await servico.CadastrarAsync(Nova("dup"), default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.CadastrarAsync(Nova("dup"), default));
        Assert.True(erro.Conflito);
        Assert.Contains("e-mail", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Senha_curta_e_recusada_antes_de_gravar_qualquer_coisa()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var nova = Nova("curta") with { Senha = "1234" };
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => new ServicoCadastroEmpresa(db).CadastrarAsync(nova, default));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Empresas.IgnoreQueryFilters()
            .Where(e => e.Nome == "Padaria curta").ToListAsync());
    }

    [Fact]
    public async Task Duas_empresas_cadastradas_ficam_com_funis_separados()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var servico = new ServicoCadastroEmpresa(db);

        var idA = await servico.CadastrarAsync(Nova("sep-a"), default);
        var idB = await servico.CadastrarAsync(Nova("sep-b"), default);
        db.ChangeTracker.Clear();

        ctx.EmpresaId = idA;
        var etapasA = await db.EtapasFunil.AsNoTracking().ToListAsync();
        Assert.Equal(5, etapasA.Count);
        Assert.All(etapasA, e => Assert.Equal(idA, e.EmpresaId));

        ctx.EmpresaId = idB;
        var etapasB = await db.EtapasFunil.AsNoTracking().ToListAsync();
        Assert.Equal(5, etapasB.Count);
        Assert.All(etapasB, e => Assert.Equal(idB, e.EmpresaId));

        // Nenhum id de etapa se repete entre os dois funis.
        Assert.Empty(etapasA.Select(e => e.Id).Intersect(etapasB.Select(e => e.Id)));
    }

    [Fact]
    public async Task Contato_novo_ja_tem_etapa_valida_logo_apos_o_cadastro()
    {
        // O ponto do seed: sem as etapas, este INSERT seria impossivel (EtapaId e NOT NULL) e
        // a empresa nasceria inutilizavel.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var empresaId = await new ServicoCadastroEmpresa(db).CadastrarAsync(Nova("prim"), default);
        ctx.EmpresaId = empresaId;
        db.ChangeTracker.Clear();

        var primeira = await db.EtapasFunil.OrderBy(e => e.Ordem).FirstAsync();
        db.Contatos.Add(new Contato
        {
            EmpresaId = empresaId, Nome = "Lead do WhatsApp",
            Telefone = "5584988887777", EtapaId = primeira.Id
        });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var contato = await db.Contatos.AsNoTracking().SingleAsync();
        Assert.Equal("Novo Lead", (await db.EtapasFunil.AsNoTracking()
            .SingleAsync(e => e.Id == contato.EtapaId)).Nome);
        Assert.Equal(OrigemLead.Whatsapp, contato.Origem);   // default do banco
    }
}
