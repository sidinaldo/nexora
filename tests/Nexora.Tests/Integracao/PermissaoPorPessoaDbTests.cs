using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Auditoria;
using Nexora.Core.Email;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Email;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>===================== PER-1 — O DONO LIGA E DESLIGA PERMISSÃO POR PESSOA =====================
///
/// O papel é a BASE; esta tabela guarda só o que DIVERGE dele. Os testes aqui cobrem o lado da
/// ESCRITA: o que o `ServicoEquipe` aceita, o que recusa, e o que apaga.
///
/// A decisão de leitura — como a exceção chega à política e à tela — mora em `PermissoesTests`, que
/// faz a volta completa pelo token.
/// ============================================================================================</summary>
[Collection("banco")]
public class PermissaoPorPessoaDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ==================================================================== o portão por dentro

    /// <summary>⚠️ HOJE ISTO PASSARIA. Até o PER-1 o único portão de `gerenciar_equipe` era o
    /// atributo do controller — contra o que `Permissoes` promete, que é a checagem valer também
    /// quando outro código chama por dentro. Com a concessão de permissão morando no
    /// `AtualizarAsync`, o método mais perigoso do sistema ficaria com meia porta.</summary>
    [Fact]
    public async Task QUEM_NAO_GERENCIA_EQUIPE_NAO_MEXE_NA_EQUIPE_NEM_POR_DENTRO()
    {
        var (db, tx, amb) = await PrepararAsync("portao");
        using var _ = db; using var __ = tx;

        var vendedor = await NovoVendedorAsync(db, amb.EmpresaId, "vend");
        amb.Contexto.Papel = "vendedor";

        // Sem passar pelo controller: a chamada direta ao serviço.
        foreach (var chamada in new Func<Task>[]
        {
            () => amb.Equipe.ListarAsync(default),
            () => amb.Equipe.ConvidarAsync(new NovoConvite("X", "x@y.com", "vendedor"), default),
            () => amb.Equipe.ReenviarConviteAsync(vendedor, default),
            () => amb.Equipe.GerarResetSenhaAsync(vendedor, default),
            () => amb.Equipe.AtualizarAsync(vendedor, Editar("Novo nome", "vendedor"), default)
        })
        {
            var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(chamada);
            Assert.Equal("Só o dono pode gerenciar a equipe.", erro.Message);
        }
    }

    // ==================================================================== as recusas

    /// <summary>⚠️ O DONO NÃO RECEBE EXCEÇÃO, NEM POR API. A tela não mostra interruptor para ele,
    /// então uma lista chegando aqui é requisição forjada — e o que ela tentaria é trancar o dono
    /// fora da própria conta.</summary>
    [Fact]
    public async Task O_DONO_NAO_RECEBE_EXCECAO_NEM_POR_API()
    {
        var (db, tx, amb) = await PrepararAsync("dono-excecao");
        using var _ = db; using var __ = tx;

        var outroDono = await NovoVendedorAsync(db, amb.EmpresaId, "dono2", PapelUsuario.Dono);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Equipe.AtualizarAsync(outroDono, Editar("Dono 2", "dono", "cancelar_venda"), default));

        Assert.Equal("Dono pode tudo — não há permissão para ajustar.", erro.Message);
    }

    /// <summary>⚠️ OS DOIS INDELEGÁVEIS, RECUSADOS POR NOME. `gerenciar_equipe` daria a um vendedor
    /// o poder de promover um colega a Dono e pedir o favor de volta; `configurar_empresa` daria o
    /// webhook de saída, que manda a base de contatos para qualquer URL.</summary>
    [Theory]
    [InlineData("gerenciar_equipe")]
    [InlineData("configurar_empresa")]
    public async Task O_GESTO_INDELEGAVEL_NAO_SE_CONCEDE(string gesto)
    {
        var (db, tx, amb) = await PrepararAsync("indelegavel-" + gesto[..4]);
        using var _ = db; using var __ = tx;

        var vendedor = await NovoVendedorAsync(db, amb.EmpresaId, "vend");

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() =>
            amb.Equipe.AtualizarAsync(vendedor, Editar("Rafael", "vendedor", gesto), default));

        Assert.Contains("não se delega", erro.Message);
        Assert.Empty(await db.UsuariosPermissoes.ToListAsync());
    }

    // ==================================================================== o diff

    /// <summary>Só o que DIVERGE do papel vira linha. Um gestor marcado com as cinco que ele já tem
    /// não gera exceção nenhuma — senão a tabela encheria de linhas que não dizem nada, e o diff
    /// de quem mudou de acesso deixaria de ser legível.</summary>
    [Fact]
    public async Task SO_O_QUE_DIVERGE_DO_PAPEL_VIRA_LINHA()
    {
        var (db, tx, amb) = await PrepararAsync("diff");
        using var _ = db; using var __ = tx;

        var gestor = await NovoVendedorAsync(db, amb.EmpresaId, "gest", PapelUsuario.Gestor);

        // As cinco que o gestor já tem: nada a gravar.
        await amb.Equipe.AtualizarAsync(gestor, Editar("Bia", "gestor",
            "importar_contatos", "cancelar_venda", "ver_historico", "anonimizar_contato",
            "ver_numeros_da_equipe"), default);

        Assert.Empty(await db.UsuariosPermissoes.ToListAsync());

        // Tirando uma: UMA linha, de revogação.
        await amb.Equipe.AtualizarAsync(gestor, Editar("Bia", "gestor",
            "importar_contatos", "cancelar_venda", "ver_historico", "anonimizar_contato"), default);

        var linha = Assert.Single(await db.UsuariosPermissoes.ToListAsync());
        Assert.Equal("ver_numeros_da_equipe", linha.Permissao);
        Assert.False(linha.Concedida);
        Assert.Equal(amb.DonoId, linha.CriadoPor);

        // Devolvendo: a linha some, não fica como `concedida = true` redundante.
        await amb.Equipe.AtualizarAsync(gestor, Editar("Bia", "gestor",
            "importar_contatos", "cancelar_venda", "ver_historico", "anonimizar_contato",
            "ver_numeros_da_equipe"), default);

        Assert.Empty(await db.UsuariosPermissoes.ToListAsync());
    }

    /// <summary>===================== A TRAVA DO BUG INCOMPREENSÍVEL =====================
    ///
    /// O dono abre um VENDEDOR — interruptores desligados, porque vendedor não pode nada —, troca o
    /// seletor para Gestor e salva. Sem esta regra, o servidor receberia papel=gestor com dez
    /// desmarcados e gravaria DEZ NEGAÇÕES: "promovi para gestor e ele continua sem ver os
    /// números".
    ///
    /// ⚠️ É REGRA DE SERVIDOR, e não de tela. A tela desabilita os interruptores quando o papel
    /// muda, mas quem monta a requisição não decide a autorização — a mesma disciplina de
    /// `ServicoRelatorios`, que descarta o responsável que o vendedor manda.
    /// ======================================================================</summary>
    [Fact]
    public async Task TROCAR_O_PAPEL_DESCARTA_A_LISTA_SUBMETIDA()
    {
        var (db, tx, amb) = await PrepararAsync("troca-papel");
        using var _ = db; using var __ = tx;

        var vendedor = await NovoVendedorAsync(db, amb.EmpresaId, "vend");

        // Promovido a gestor com a lista de um vendedor (tudo desmarcado).
        await amb.Equipe.AtualizarAsync(vendedor, Editar("Rafael", "gestor"), default);

        Assert.Empty(await db.UsuariosPermissoes.ToListAsync());

        // E o efetivo é a base do GESTOR — as cinco —, não dez negações.
        var dele = (await amb.Equipe.ListarAsync(default)).Single(u => u.Id == vendedor);
        Assert.Equal(
            ["anonimizar_contato", "cancelar_venda", "importar_contatos", "ver_historico",
             "ver_numeros_da_equipe"],
            dele.Permissoes.Order());
    }

    /// <summary>E a troca de papel LIMPA o que havia. Um vendedor com `+cancelar_venda` promovido a
    /// gestor não fica com uma linha redundante pendurada.</summary>
    [Fact]
    public async Task TROCAR_O_PAPEL_LIMPA_AS_EXCECOES_QUE_HAVIA()
    {
        var (db, tx, amb) = await PrepararAsync("troca-limpa");
        using var _ = db; using var __ = tx;

        var vendedor = await NovoVendedorAsync(db, amb.EmpresaId, "vend");

        await amb.Equipe.AtualizarAsync(vendedor, Editar("Rafael", "vendedor", "cancelar_venda"), default);
        Assert.Single(await db.UsuariosPermissoes.ToListAsync());

        await amb.Equipe.AtualizarAsync(vendedor, Editar("Rafael", "gestor"), default);
        Assert.Empty(await db.UsuariosPermissoes.ToListAsync());
    }

    /// <summary>⚠️ LISTA AUSENTE NÃO É LISTA VAZIA. O atalho de inativar/reativar da tela manda só
    /// nome, papel e situação — se ausência significasse "nenhuma permissão", inativar alguém
    /// apagaria em silêncio tudo que o dono tinha marcado para ele.</summary>
    [Fact]
    public async Task INATIVAR_ALGUEM_NAO_APAGA_AS_PERMISSOES_DELE()
    {
        var (db, tx, amb) = await PrepararAsync("inativar");
        using var _ = db; using var __ = tx;

        var vendedor = await NovoVendedorAsync(db, amb.EmpresaId, "vend");
        await amb.Equipe.AtualizarAsync(vendedor, Editar("Rafael", "vendedor", "cancelar_venda"), default);

        // O atalho da tela: sem lista nenhuma.
        await amb.Equipe.AtualizarAsync(
            vendedor, new EditarUsuario("Rafael", "vendedor", "inativo"), default);

        var linha = Assert.Single(await db.UsuariosPermissoes.ToListAsync());
        Assert.Equal("cancelar_venda", linha.Permissao);
        Assert.True(linha.Concedida);
    }

    // ==================================================================== a trilha

    /// <summary>⚠️ ATÉ AQUI NÃO HAVIA TRILHA DE AUTORIZAÇÃO NENHUMA — `EntidadeAuditada.Usuario`
    /// estava declarado e nenhum serviço o citava. Numa feature cuja razão de existir é "quem pode
    /// o quê", *quem deu isso a ele e quando* não pode ser inferido do nada.</summary>
    [Fact]
    public async Task A_TRILHA_REGISTRA_QUEM_DEU_E_QUEM_TIROU()
    {
        var (db, tx, amb) = await PrepararAsync("trilha");
        using var _ = db; using var __ = tx;

        var vendedor = await NovoVendedorAsync(db, amb.EmpresaId, "vend");

        await amb.Equipe.AtualizarAsync(vendedor, Editar("Rafael", "vendedor", "cancelar_venda"), default);

        var evento = Assert.Single(await db.Auditoria
            .Where(a => a.Entidade == EntidadeAuditada.Usuario && a.EntidadeId == vendedor)
            .ToListAsync());

        Assert.Equal(AcaoAuditoria.Editou, evento.Acao);
        Assert.Equal(amb.DonoId, evento.UsuarioId);

        var dado = Alteracoes(evento.Alteracoes);
        Assert.Equal("o que o papel dá", dado["permissões"]["antes"]);
        Assert.Equal("+cancelar_venda", dado["permissões"]["depois"]);

        // E tirar também fica registrado, com o sentido invertido.
        db.ChangeTracker.Clear();
        await amb.Equipe.AtualizarAsync(vendedor, Editar("Rafael", "vendedor"), default);

        var ultimo = Alteracoes((await db.Auditoria
            .Where(a => a.Entidade == EntidadeAuditada.Usuario && a.EntidadeId == vendedor)
            .OrderByDescending(a => a.Id)
            .FirstAsync()).Alteracoes);

        Assert.Equal("+cancelar_venda", ultimo["permissões"]["antes"]);
        Assert.Equal("o que o papel dá", ultimo["permissões"]["depois"]);
    }

    /// <summary>⚠️ LÊ O JSON, NÃO PROCURA SUBSTRING. O `JsonSerializer` escapa com o encoder
    /// padrão, então o `+` de `+cancelar_venda` chega na coluna como `+` — um
    /// `Assert.Contains("+cancelar_venda", ...)` falha com a trilha PERFEITAMENTE correta, e manda
    /// quem for investigar atrás do bug errado.</summary>
    private static Dictionary<string, Dictionary<string, string>> Alteracoes(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)!;

    // ==================================================================== a permissão vale

    /// <summary>O irmão de `ImportacaoDbTests.VENDEDOR_NAO_IMPORTA`: com a exceção, ele importa. É
    /// o teste que prova que a feature chega aos oito pontos de `Exigir` dos serviços sem que
    /// nenhum deles tenha mudado uma letra.</summary>
    [Fact]
    public async Task O_VENDEDOR_COM_A_EXCECAO_PASSA_NO_EXIGIR_DO_SERVICO()
    {
        var ctx = new ContextoMutavel { Papel = "vendedor" };

        Assert.False(ctx.Pode(Permissao.ImportarContatos));

        ctx.ExcecoesDePermissao = new Dictionary<Permissao, bool>
        {
            [Permissao.ImportarContatos] = true
        };

        Assert.True(ctx.Pode(Permissao.ImportarContatos));

        // E o indelegável continua fora, mesmo com a exceção na mão.
        ctx.ExcecoesDePermissao = new Dictionary<Permissao, bool>
        {
            [Permissao.GerenciarEquipe] = true
        };

        Assert.False(ctx.Pode(Permissao.GerenciarEquipe));
        await Task.CompletedTask;
    }

    // ==================================================================== apoio

    private static EditarUsuario Editar(string nome, string papel, params string[] permissoes) =>
        new(nome, papel, "ativo", permissoes);

    private static async Task<long> NovoVendedorAsync(
        NexoraDbContext db, long empresaId, string sufixo,
        PapelUsuario papel = PapelUsuario.Vendedor)
    {
        var u = new Usuario
        {
            EmpresaId = empresaId,
            Nome = $"Pessoa {sufixo}",
            Email = $"{sufixo}-{Guid.NewGuid():N}@x.com",
            Papel = papel,
            Status = StatusUsuario.Ativo,
            SenhaHash = "pbkdf2$1$x$y"
        };
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return u.Id;
    }

    private sealed record Ambiente(
        long EmpresaId, long DonoId, ContextoMutavel Contexto, IServicoEquipe Equipe);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(QuintaDeManha);
        var trilha = new ColetorAuditoria();
        var db = banco.NovoContexto(ctx, relogio, trilha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var notificador = new NotificadorEmail(
            new RemetenteFalso(), db, new OpcoesEmail { BaseUrlPainel = "http://localhost:4200" },
            relogio, NullLogger<NotificadorEmail>.Instance);

        return (db, tx, new Ambiente(
            cenario.Id, cenario.Dono.Id, ctx,
            new ServicoEquipe(db, ctx, relogio, notificador, new FilaSegundoPlanoFalsa(), trilha)));
    }
}
