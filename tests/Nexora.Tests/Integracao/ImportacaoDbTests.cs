using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Auditoria;
using Nexora.Core.Csv;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>IMPORTAR LEAD (issue #8).
///
/// O cliente novo chega com a base dele numa planilha. Sem isto ele começa do zero e espera o
/// WhatsApp encher — que é o bloqueio de adoção inteiro para quem já tem 800 clientes.
///
/// As três decisões do bloco estão cada uma com o seu teste, porque cada uma tem um jeito
/// específico de dar errado em produção e nenhum deles aparece em compilação.</summary>
[Collection("banco")]
public class ImportacaoDbTests(BancoTeste banco)
{
    private static byte[] Csv(params string[] linhas) =>
        CsvBrasileiro.Gerar(linhas.Select(l => l.Split('|')));

    // ==================================================================== a previa
    /// <summary>⚠️ A PRÉVIA NÃO GRAVA NADA. É o passo que existe porque importar é quase
    /// irreversível: o dono confere "612 novos · 173 já existem · 15 inválidos" antes de decidir.
    ///
    /// Se ela gravasse, o botão "cancelar" da tela seria uma mentira.</summary>
    [Fact]
    public async Task A_PREVIA_CONTA_E_NAO_GRAVA()
    {
        var (db, tx, servico, c) = await PrepararAsync("previa");
        using var _1 = db; using var _2 = tx;

        var antes = await db.Contatos.CountAsync();

        var resumo = await servico.PreverAsync(Csv(
            "nome|telefone",
            "Maria Silva|84988887777",
            "João Souza|84999996666",
            "|84911112222",                       // sem nome
            "Sem Numero|",                        // sem telefone
            "Curto|123"), default);               // telefone inválido

        Assert.Equal(5, resumo.Total);
        Assert.Equal(2, resumo.Novas);
        Assert.Equal(0, resumo.Repetidas);
        Assert.Equal(3, resumo.Invalidas);

        // ⚠️ O NÚMERO DA LINHA É O DO EXCEL: cabeçalho é a 1, o primeiro contato é a 2. Quem vai
        // corrigir a planilha procura ESTE número lá — "linha 3 do corpo" não existe na tela dele.
        var semNome = Assert.Single(resumo.Amostra, l => l.Motivo == "Sem nome.");
        Assert.Equal(4, semNome.Linha);

        db.ChangeTracker.Clear();
        Assert.Equal(antes, await db.Contatos.CountAsync());
    }

    // ==================================================================== decisao 1
    /// <summary>⚠️ IMPORTAR NÃO ENCHE O QUADRO, e isto contraria o cadastro manual de propósito.
    ///
    /// `ServicoContatos.CriarAsync` cria a negociação junto com o contato. Numa planilha de 800
    /// clientes isso seriam 800 cards na primeira etapa do funil padrão — o quadro do vendedor
    /// inutilizável no dia do import, e desfazer é apagar 800 linhas na mão.
    ///
    /// Desde o E6, contato com ZERO negociações é o estado normal.</summary>
    [Fact]
    public async Task SEM_FUNIL_ESCOLHIDO_NINGUEM_ENTRA_NO_QUADRO()
    {
        var (db, tx, servico, c) = await PrepararAsync("sem-funil");
        using var _1 = db; using var _2 = tx;

        var negociacoesAntes = await db.Negociacoes.CountAsync();

        await servico.ImportarAsync(Csv(
            "nome|telefone",
            "Maria Silva|84988887777",
            "João Souza|84999996666"), null, default);

        db.ChangeTracker.Clear();

        var maria = await db.Contatos.SingleAsync(x => x.Telefone == "5584988887777");
        Assert.Equal("Maria Silva", maria.Nome);

        // Nenhuma negociação nova: as duas pessoas existem, e o quadro não mudou.
        Assert.Equal(negociacoesAntes, await db.Negociacoes.CountAsync());
        Assert.False(await db.Negociacoes.AnyAsync(n => n.ContatoId == maria.Id));
    }

    /// <summary>E quem QUER os cards pede: o funil vem por parâmetro, e aí sim nasce negociação —
    /// na primeira etapa dele, uma por contato.</summary>
    [Fact]
    public async Task COM_FUNIL_ESCOLHIDO_CADA_UM_VIRA_UM_CARD()
    {
        var (db, tx, servico, c) = await PrepararAsync("com-funil");
        using var _1 = db; using var _2 = tx;

        await servico.ImportarAsync(Csv(
            "nome|telefone",
            "Maria Silva|84988887777",
            "João Souza|84999996666"), c.Pipeline.Id, default);

        db.ChangeTracker.Clear();

        var importados = await db.Contatos.AsNoTracking()
            .Where(x => x.Telefone == "5584988887777" || x.Telefone == "5584999996666")
            .Select(x => x.Id).ToListAsync();

        var negocios = await db.Negociacoes.AsNoTracking()
            .Where(n => importados.Contains(n.ContatoId)).ToListAsync();

        Assert.Equal(2, negocios.Count);
        Assert.All(negocios, n => Assert.Equal(c.PrimeiraEtapa.Id, n.EtapaId));
        Assert.All(negocios, n => Assert.Equal(StatusNegociacao.Aberta, n.Status));

        // ⚠️ POSIÇÕES DIFERENTES na coluna. Todos com a mesma `OrdemKanban` empilhariam os cards
        // numa ordem que o banco escolhe, e arrastar um deles mexeria na vizinhança errada.
        Assert.Equal(2, negocios.Select(n => n.OrdemKanban).Distinct().Count());
    }

    // ==================================================================== decisao 2
    /// <summary>⚠️ REPETIDO PULA, E NÃO ATUALIZA. Atualizar sobrescreveria com uma planilha velha o
    /// que o vendedor escreveu no atendimento de ontem — silencioso, e irreversível.
    ///
    /// O teste afirma as duas metades: o de fora não entra duas vezes, e o que já existia continua
    /// EXATAMENTE como estava.</summary>
    [Fact]
    public async Task TELEFONE_QUE_JA_EXISTE_PULA_SEM_TOCAR_NO_CONTATO()
    {
        var (db, tx, servico, c) = await PrepararAsync("repetido");
        using var _1 = db; using var _2 = tx;

        var nomeOriginal = c.Contato.Nome;
        var telefone = c.Contato.Telefone;

        var resumo = await servico.ImportarAsync(Csv(
            "nome|telefone|observacoes",
            $"NOME DA PLANILHA|{telefone}|veio da planilha",
            "Gente Nova|84911112222"), null, default);

        Assert.Equal(1, resumo.Novas);
        Assert.Equal(1, resumo.Repetidas);

        db.ChangeTracker.Clear();

        // O de dentro não foi tocado — nem o nome, nem a observação.
        var existente = await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == c.Contato.Id);
        Assert.Equal(nomeOriginal, existente.Nome);
        Assert.Null(existente.Observacoes);

        // E não nasceu um segundo com o mesmo telefone.
        Assert.Equal(1, await db.Contatos.CountAsync(x => x.Telefone == telefone));
    }

    /// <summary>⚠️ O REPETIDO DENTRO DO PRÓPRIO ARQUIVO, que é o que derruba o import inteiro.
    ///
    /// A checagem contra o banco não pega: nenhuma das duas linhas existe ainda. As duas passariam,
    /// e o `SaveChanges` único estouraria em `uq_contatos_telefone` levando as outras 799 junto —
    /// tudo por um cliente digitado duas vezes na planilha.</summary>
    [Fact]
    public async Task O_MESMO_TELEFONE_DUAS_VEZES_NO_ARQUIVO_NAO_DERRUBA_O_RESTO()
    {
        var (db, tx, servico, c) = await PrepararAsync("duplicado-no-arquivo");
        using var _1 = db; using var _2 = tx;

        var resumo = await servico.ImportarAsync(Csv(
            "nome|telefone",
            "Maria Silva|84988887777",
            "Maria S.|(84) 98888-7777",           // a MESMA pessoa, escrita de outro jeito
            "João Souza|84999996666"), null, default);

        // Duas entram, uma é recusada — e o "outro jeito" é pego porque a comparação é do telefone
        // CANONICALIZADO, não do texto da planilha.
        Assert.Equal(2, resumo.Novas);
        Assert.Equal(1, resumo.Invalidas);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Contatos.CountAsync(x => x.Telefone == "5584988887777"));
        Assert.Equal(1, await db.Contatos.CountAsync(x => x.Telefone == "5584999996666"));
    }

    // ==================================================================== o que o arquivo traz
    [Fact]
    public async Task EMAIL_ORIGEM_E_OBSERVACOES_ENTRAM_QUANDO_VEM()
    {
        var (db, tx, servico, c) = await PrepararAsync("colunas");
        using var _1 = db; using var _2 = tx;

        await servico.ImportarAsync(Csv(
            "nome|celular|e-mail|origem|obs",
            "Maria Silva|84988887777|m@x.com|indicacao|cliente da obra"), null, default);

        db.ChangeTracker.Clear();
        var maria = await db.Contatos.AsNoTracking().SingleAsync(x => x.Telefone == "5584988887777");

        Assert.Equal("m@x.com", maria.Email);
        Assert.Equal(OrigemLead.Indicacao, maria.Origem);
        Assert.Equal("cliente da obra", maria.Observacoes);
    }

    /// <summary>Origem em branco ou desconhecida vira `manual` — a planilha do cliente não conhece
    /// o nosso enum, e recusar a linha por causa disso seria perder o contato por um rótulo.</summary>
    [Fact]
    public async Task ORIGEM_DESCONHECIDA_VIRA_MANUAL()
    {
        var (db, tx, servico, c) = await PrepararAsync("origem");
        using var _1 = db; using var _2 = tx;

        await servico.ImportarAsync(Csv(
            "nome|telefone|origem",
            "Maria|84988887777|panfleto da esquina",
            "João|84999996666|"), null, default);

        db.ChangeTracker.Clear();
        var todos = await db.Contatos.AsNoTracking()
            .Where(x => x.Telefone == "5584988887777" || x.Telefone == "5584999996666")
            .ToListAsync();

        Assert.All(todos, x => Assert.Equal(OrigemLead.Manual, x.Origem));
    }

    // ==================================================================== os limites e as recusas
    /// <summary>⚠️ O NOME DA COLUNA QUE FALTA, e não "nenhum contato válido". A segunda mensagem
    /// faz a pessoa desistir: ela não tem como adivinhar que o problema é a primeira linha.</summary>
    [Fact]
    public async Task CABECALHO_SEM_TELEFONE_DIZ_QUAL_COLUNA_FALTA()
    {
        var (db, tx, servico, c) = await PrepararAsync("cabecalho");
        using var _1 = db; using var _2 = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.PreverAsync(Csv("nome|email", "Maria|m@x.com"), default));

        Assert.Contains("telefone", erro.Message);
        Assert.Contains("cabeçalho", erro.Message);
    }

    [Fact]
    public async Task ACIMA_DO_TETO_DE_LINHAS_A_RECUSA_DIZ_O_TAMANHO()
    {
        var (db, tx, servico, c) = await PrepararAsync("teto");
        using var _1 = db; using var _2 = tx;

        var linhas = new List<string> { "nome|telefone" };
        for (var i = 0; i < IServicoImportacao.MaximoLinhas + 1; i++)
            linhas.Add($"Pessoa {i}|8490000{i:D4}");

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.PreverAsync(Csv([.. linhas]), default));

        Assert.Contains(IServicoImportacao.MaximoLinhas.ToString(), erro.Message);
        Assert.Contains("Divida", erro.Message);
    }

    /// <summary>Escrita em massa que aparece no quadro de todo mundo é de quem responde pelo
    /// número — mesma linha de corte de cancelar venda.</summary>
    [Fact]
    public async Task VENDEDOR_NAO_IMPORTA()
    {
        var (db, tx, servico, c, ctx) = await PrepararComContextoAsync("papel");
        using var _1 = db; using var _2 = tx;

        ctx.Papel = "vendedor";

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.ImportarAsync(Csv("nome|telefone", "Maria|84988887777"), null, default));

        Assert.Contains("dono ou um gestor", erro.Message);

        db.ChangeTracker.Clear();
        Assert.False(await db.Contatos.AnyAsync(x => x.Telefone == "5584988887777"));
    }

    /// <summary>⚠️ MAS A PRÉVIA É DE QUALQUER PAPEL, de propósito: ela não grava nada, e o vendedor
    /// que recebeu a planilha do dono precisa poder conferir antes de pedir o import.</summary>
    [Fact]
    public async Task O_VENDEDOR_AINDA_PODE_CONFERIR_A_PREVIA()
    {
        var (db, tx, servico, c, ctx) = await PrepararComContextoAsync("papel-previa");
        using var _1 = db; using var _2 = tx;

        ctx.Papel = "vendedor";

        var resumo = await servico.PreverAsync(Csv("nome|telefone", "Maria|84988887777"), default);
        Assert.Equal(1, resumo.Novas);
    }

    // ==================================================================== a trilha
    /// <summary>Cada contato importado ganha o seu "criou" — a linha do tempo é DE CADA UM, então
    /// 800 contatos não enchem a de nenhum. E sem isso o contato apareceria na base sem nada
    /// dizendo de onde veio.</summary>
    [Fact]
    public async Task CADA_IMPORTADO_GANHA_O_PROPRIO_EVENTO_NA_TRILHA()
    {
        var (db, tx, servico, c) = await PrepararAsync("trilha");
        using var _1 = db; using var _2 = tx;

        await servico.ImportarAsync(Csv(
            "nome|telefone",
            "Maria Silva|84988887777",
            "João Souza|84999996666"), null, default);

        db.ChangeTracker.Clear();

        var maria = await db.Contatos.AsNoTracking().SingleAsync(x => x.Telefone == "5584988887777");

        var eventos = await db.Auditoria.AsNoTracking()
            .Where(e => e.Entidade == EntidadeAuditada.Contato && e.EntidadeId == maria.Id)
            .Select(e => e.Acao).ToListAsync();

        Assert.Contains(AcaoAuditoria.Criou, eventos);
    }

    // ==================================================================== o ciclo fechado
    /// <summary>⚠️ O TESTE QUE PROVA QUE OS DOIS LADOS DO CSV FALAM A MESMA LÍNGUA.
    ///
    /// Importa, exporta o que entrou no MESMO formato do produto, reimporta — e exige que NADA
    /// entre na segunda vez. Se o leitor e o escritor divergirem, o cliente reimporta a própria
    /// lista e vê tudo duplicado; é o pior defeito possível numa importação, e o mais fácil de
    /// criar sem perceber.</summary>
    [Fact]
    public async Task REIMPORTAR_O_QUE_ACABOU_DE_ENTRAR_NAO_DUPLICA_NINGUEM()
    {
        var (db, tx, servico, c) = await PrepararAsync("ida-e-volta");
        using var _1 = db; using var _2 = tx;

        var arquivo = Csv(
            "nome|telefone|observacoes",
            "Maria Silva|84988887777|cliente antigo",
            "Silva; João|84999996666|disse \"volto amanhã\"",
            "Ana Paula|84911112222|");

        var primeira = await servico.ImportarAsync(arquivo, null, default);
        Assert.Equal(3, primeira.Novas);

        db.ChangeTracker.Clear();

        // Agora o caminho de volta: o que está no banco, escrito com o exportador do produto.
        var dentro = await db.Contatos.AsNoTracking()
            .Where(x => x.Telefone.StartsWith("55849"))
            .OrderBy(x => x.Id)
            .Select(x => new { x.Nome, x.Telefone, x.Observacoes })
            .ToListAsync();

        var exportado = CsvBrasileiro.Gerar(
            new[] { new[] { "nome", "telefone", "observações" } }
                .Concat(dentro.Select(x => new[] { x.Nome, x.Telefone, x.Observacoes ?? "" })));

        var segunda = await servico.ImportarAsync(exportado, null, default);

        Assert.Equal(0, segunda.Novas);
        Assert.Equal(dentro.Count, segunda.Repetidas);
        Assert.Equal(0, segunda.Invalidas);
    }

    // ====================================================================
    private async Task<(NexoraDbContext, IDbContextTransaction, ServicoImportacao, Cenario)>
        PrepararAsync(string sufixo)
    {
        var (db, tx, servico, c, _) = await PrepararComContextoAsync(sufixo);
        return (db, tx, servico, c);
    }

    private async Task<(NexoraDbContext, IDbContextTransaction, ServicoImportacao, Cenario,
        ContextoMutavel)> PrepararComContextoAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();

        // ⚠️ O MESMO COLETOR no contexto e no servico (AUD-1). Ele e o elo entre a DECLARACAO
        // (`trilha.Declarar`) e a GRAVACAO (o interceptor, no `SaveChanges`). Com dois coletores
        // diferentes o servico declara num objeto que ninguem le, e a trilha some — sem erro.
        var trilha = new ColetorAuditoria();
        var db = banco.NovoContexto(ctx, coletor: trilha);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"importacao-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new ServicoImportacao(db, ctx, trilha), cenario, ctx);
    }
}
