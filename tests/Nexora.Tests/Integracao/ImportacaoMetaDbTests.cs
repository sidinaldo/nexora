using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Entidades;
using Nexora.Core.LeadAds;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>IMPORTAR O CSV DO META LEAD ADS — upload e prévia (INT-XX, commit 2).
///
/// Cada teste cita o critério de aceite do spec que ele cobre. Os critérios que dependem de
/// GRAVAR contato (o 3 e o 6) ficam para o commit da gravação — aqui nada vira contato, e um
/// dos testes existe só para provar isso.
///
/// ⚠️ A FIXTURE NÃO É UM EXPORT REAL. O cabeçalho vem do spec, e os valores usam o prefixo de tipo
/// nos ids (`l:`, `ag:`, `c:`, `f:`) e o `p:` no telefone, de memória do formato da Meta. O
/// critério 1 — "CSV real importa sem erro" — continua pendente até chegar um arquivo de verdade.</summary>
[Collection("banco")]
public class ImportacaoMetaDbTests(BancoTeste banco)
{
    private const string Cabecalho =
        "id,created_time,ad_id,ad_name,adset_id,adset_name,campaign_id,campaign_name,"
        + "form_id,form_name,is_organic,platform,full_name,phone_number,email,Qual seu orçamento?";

    /// <summary>Uma linha no formato do Gerenciador de Leads.</summary>
    private static string Lead(string leadId, string nome, string telefone, string orcamento = "até 5 mil") =>
        $"l:{leadId},2026-09-10T14:32:11+0000,ag:120201,Anuncio A,as:120202,Conjunto A,"
        + $"c:120203,Campanha Setembro,f:120204,Form A,false,ig,{nome},p:{telefone},x@x.com,{orcamento}";

    private static byte[] Arquivo(params string[] linhas) =>
        new UTF8Encoding(false).GetBytes(string.Join("\n", linhas));

    // ==================================================================== receber
    /// <summary>⚠️ O UPLOAD NÃO CRIA CONTATO NENHUM — o spec: "Ao subir: parsear, gravar a importação
    /// com status aguardando_mapeamento, NÃO importar nada ainda". As linhas ficam guardadas CRUAS,
    /// com `resultado` nulo: ainda não aconteceu nada com elas.</summary>
    [Fact]
    public async Task RECEBER_GUARDA_AS_LINHAS_E_NAO_CRIA_CONTATO()
    {
        var (db, tx, servico, _) = await PrepararAsync("receber");
        using var _1 = db; using var _2 = tx;

        var contatosAntes = await db.Contatos.CountAsync();

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho,
            Lead("1001", "Maria Silva", "+5584988887777"),
            Lead("1002", "João Souza", "+5584999996666")), default);

        Assert.Equal(2, r.TotalLinhas);

        db.ChangeTracker.Clear();
        var imp = await db.Importacoes.AsNoTracking().SingleAsync(i => i.Id == r.Id);
        Assert.Equal(StatusImportacao.AguardandoMapeamento, imp.Status);
        Assert.Equal("leads.csv", imp.NomeArquivo);

        var linhas = await db.ImportacaoLinhas.AsNoTracking()
            .Where(l => l.ImportacaoId == r.Id).OrderBy(l => l.NumeroLinha).ToListAsync();
        Assert.Equal([2, 3], linhas.Select(l => l.NumeroLinha));
        Assert.All(linhas, l => Assert.Null(l.Resultado));

        // A linha inteira foi guardada, inclusive a coluna que ninguém vai mapear.
        Assert.Contains("Qual seu orçamento?", linhas[0].DadosBrutos);
        Assert.Contains("Conjunto A", linhas[0].DadosBrutos);

        Assert.Equal(contatosAntes, await db.Contatos.CountAsync());
    }

    /// <summary>O mapeamento sugerido reconhece os metadados e o formulário, e deixa a pergunta que o
    /// cliente criou para ele decidir.</summary>
    [Fact]
    public async Task RECEBER_SUGERE_O_MAPEAMENTO_DO_GERENCIADOR_DE_LEADS()
    {
        var (db, tx, servico, _) = await PrepararAsync("sugestao");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("leads.csv",
            Arquivo(Cabecalho, Lead("1001", "Maria", "+5584988887777")), default);

        var m = r.Mapeamento.ToDictionary(x => x.Coluna, x => x.Campo);
        Assert.Equal(CampoImportacao.MetaLeadId, m["id"]);
        Assert.Equal(CampoImportacao.Telefone, m["phone_number"]);
        Assert.Equal(CampoImportacao.CriadoEm, m["created_time"]);
        Assert.Equal(CampoImportacao.Ignorar, m["Qual seu orçamento?"]);

        // Na ordem do arquivo, uma entrada por coluna.
        Assert.Equal(Cabecalho.Split(','), r.Mapeamento.Select(x => x.Coluna));
    }

    /// <summary>"Arquivo vazio ou só com cabeçalho" está no spec. Só cabeçalho é importação VAZIA,
    /// e não erro: recusar esconderia do dono que o export saiu sem linha — filtro de data errado
    /// no Gerenciador, que é o caso comum.</summary>
    [Fact]
    public async Task SO_CABECALHO_E_IMPORTACAO_VAZIA_E_NAO_ERRO()
    {
        var (db, tx, servico, _) = await PrepararAsync("so-cabecalho");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("vazio.csv", Arquivo(Cabecalho), default);

        Assert.Equal(0, r.TotalLinhas);
    }

    [Fact]
    public async Task ARQUIVO_VAZIO_E_RECUSADO_DIZENDO_POR_QUE()
    {
        var (db, tx, servico, _) = await PrepararAsync("vazio");
        using var _1 = db; using var _2 = tx;

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.ReceberAsync("x.csv", [], default));
        Assert.Contains("vazio", erro.Message);
    }

    // ==================================================================== a prévia
    /// <summary>O spec: "preview das 10 primeiras linhas já transformadas, mostrando telefone
    /// normalizado". E os totais contam o arquivo INTEIRO, que é o número que decide o clique.</summary>
    [Fact]
    public async Task A_PREVIA_MOSTRA_AS_10_PRIMEIRAS_JA_TRANSFORMADAS()
    {
        var (db, tx, servico, _) = await PrepararAsync("previa");
        using var _1 = db; using var _2 = tx;

        var linhas = new List<string> { Cabecalho };
        for (var i = 0; i < 12; i++) linhas.Add(Lead($"20{i:D2}", $"Pessoa {i}", $"+55849111100{i:D2}"));

        var r = await servico.ReceberAsync("leads.csv", Arquivo([.. linhas]), default);
        var p = await servico.PreverAsync(r.Id, r.Mapeamento, default);

        Assert.Equal(12, p.Total);
        Assert.Equal(12, p.Novos);
        Assert.Equal(10, p.Primeiras.Count);

        var primeira = p.Primeiras[0];
        Assert.Equal(2, primeira.Linha);
        Assert.Equal("Pessoa 0", primeira.Nome);
        // ⚠️ O `p:+55...` da Meta chega normalizado — é isto que o dono confere antes de gravar.
        Assert.Equal("5584911110000", primeira.Telefone);
        // ⚠️ E o id SEM o prefixo de tipo: guardado com `l:`, o mesmo lead teria outro id no dia em
        // que chegar pela Graph API, e a deduplicação deixaria de casar.
        Assert.Equal("2000", primeira.MetaLeadId);
        Assert.Equal(new DateTime(2026, 9, 10, 14, 32, 11, DateTimeKind.Utc), primeira.CriadoEm);
        Assert.Equal(ResultadoLinha.Importado, primeira.Resultado);

        // E a prévia NÃO gravou nada.
        db.ChangeTracker.Clear();
        Assert.All(await db.ImportacaoLinhas.AsNoTracking()
            .Where(l => l.ImportacaoId == r.Id).ToListAsync(), l => Assert.Null(l.Resultado));
    }

    /// <summary>O spec: "Telefone é obrigatório. Sem coluna de telefone mapeada, bloquear o avanço."</summary>
    [Fact]
    public async Task SEM_TELEFONE_MAPEADO_O_AVANCO_E_BLOQUEADO()
    {
        var (db, tx, servico, _) = await PrepararAsync("sem-telefone");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("leads.csv",
            Arquivo(Cabecalho, Lead("1001", "Maria", "+5584988887777")), default);

        var semTelefone = r.Mapeamento
            .Select(m => m.Campo == CampoImportacao.Telefone ? m with { Campo = CampoImportacao.Ignorar } : m)
            .ToList();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.PreverAsync(r.Id, semTelefone, default));
        Assert.Contains("telefone", erro.Message);
    }

    /// <summary>Critério 4: "arquivo com 3 linhas de telefone inválido importa o resto e lista as 3".
    /// E o spec: "não tente corrigir o nono dígito" — telefone curto é inválido, e ponto.</summary>
    [Fact]
    public async Task TRES_TELEFONES_INVALIDOS_NAO_DERRUBAM_O_RESTO()
    {
        var (db, tx, servico, _) = await PrepararAsync("invalidos");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho,
            Lead("3001", "Boa 1", "+5584988880001"),
            Lead("3002", "Ruim 1", "123"),
            Lead("3003", "Boa 2", "+5584988880002"),
            Lead("3004", "Ruim 2", "8488880003"[..7]),
            Lead("3005", "Ruim 3", "abc"),
            Lead("3006", "Boa 3", "+5584988880004")), default);

        var p = await servico.PreverAsync(r.Id, r.Mapeamento, default);

        Assert.Equal(3, p.Novos);
        Assert.Equal(3, p.Invalidos);

        var ruins = p.Primeiras.Where(l => l.Resultado == ResultadoLinha.Invalido).ToList();
        Assert.Equal([3, 5, 6], ruins.Select(l => l.Linha));
        Assert.All(ruins, l => Assert.StartsWith("telefone_", l.Motivo));
    }

    /// <summary>Critério 7: "arquivo com ponto-e-vírgula como delimitador funciona" — no nível do
    /// serviço, e não só do leitor.</summary>
    [Fact]
    public async Task PONTO_E_VIRGULA_TAMBEM_FUNCIONA()
    {
        var (db, tx, servico, _) = await PrepararAsync("ponto-virgula");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho.Replace(',', ';'),
            Lead("4001", "Maria", "+5584988887777").Replace(',', ';')), default);

        var p = await servico.PreverAsync(r.Id, r.Mapeamento, default);

        Assert.Equal(1, p.Novos);
        Assert.Equal("5584988887777", p.Primeiras[0].Telefone);
    }

    // ==================================================================== a deduplicação
    /// <summary>⚠️ A ORDEM É A REGRA. O spec: primeiro `meta_lead_id`, depois telefone.
    ///
    /// O teste monta o caso em que as duas respostas DIVERGEM: o lead da planilha tem o id da Meta
    /// de um contato e o telefone de OUTRO. Com a ordem certa, a linha é reconhecida como o lead já
    /// importado; com a ordem invertida, ela seria casada com a pessoa do telefone — e o commit da
    /// gravação enriqueceria o contato errado.</summary>
    [Fact]
    public async Task O_ID_DA_META_VEM_ANTES_DO_TELEFONE()
    {
        var (db, tx, servico, c) = await PrepararAsync("ordem-dedup");
        using var _1 = db; using var _2 = tx;

        var jaImportado = await NovoContatoAsync(db, c, "Veio da Meta", "5584911110001", metaLeadId: "5001");
        var doWhatsapp = await NovoContatoAsync(db, c, "Veio do WhatsApp", "5584911110002", metaLeadId: null);

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho,
            // o id do primeiro, o telefone do segundo
            Lead("5001", "Planilha", "+5584911110002")), default);

        var linha = (await servico.PreverAsync(r.Id, r.Mapeamento, default)).Primeiras.Single();

        Assert.Equal(ResultadoLinha.Duplicado, linha.Resultado);
        Assert.Equal("lead_ja_importado", linha.Motivo);
        _ = jaImportado; _ = doWhatsapp;
    }

    /// <summary>Critério 2 na prévia: o mesmo arquivo de novo é reconhecido. E o 3, na metade que
    /// cabe aqui: o lead que já tinha entrado pelo WhatsApp é duplicado — não um contato novo.</summary>
    [Fact]
    public async Task O_LEAD_QUE_JA_ENTROU_PELO_WHATSAPP_E_DUPLICADO()
    {
        var (db, tx, servico, c) = await PrepararAsync("whatsapp");
        using var _1 = db; using var _2 = tx;

        await NovoContatoAsync(db, c, "(84) 98888-7777", "5584988887777", metaLeadId: null);

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho, Lead("6001", "Maria Silva", "+5584988887777")), default);

        var linha = (await servico.PreverAsync(r.Id, r.Mapeamento, default)).Primeiras.Single();

        Assert.Equal(ResultadoLinha.Duplicado, linha.Resultado);
        Assert.Equal("telefone_ja_cadastrado", linha.Motivo);
    }

    /// <summary>⚠️ O REPETIDO DENTRO DO PRÓPRIO ARQUIVO — não está no spec, e é o que impede o lote
    /// de cair: sem isto as duas linhas passariam pela checagem contra o banco, e o índice único
    /// derrubaria a gravação das outras 9.998.</summary>
    [Fact]
    public async Task O_MESMO_LEAD_DUAS_VEZES_NO_ARQUIVO_E_DUPLICADO_NA_SEGUNDA()
    {
        var (db, tx, servico, _) = await PrepararAsync("repetido-arquivo");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho,
            Lead("7001", "Maria", "+5584988887777"),
            Lead("7002", "Maria de novo", "+5584988887777"),     // mesmo telefone
            Lead("7001", "Maria outra vez", "+5584911112222")), default);  // mesmo lead

        var p = await servico.PreverAsync(r.Id, r.Mapeamento, default);

        Assert.Equal(1, p.Novos);
        Assert.Equal(2, p.Duplicados);
        Assert.All(p.Primeiras.Skip(1), l => Assert.Equal("repetido_no_arquivo:2", l.Motivo));
    }

    // ==================================================================== isolamento e papel
    /// <summary>Critério 5: "arquivo de outra empresa nunca aparece". O id da importação de A, pedido
    /// do contexto de B, é a mesma coisa que um id inventado.</summary>
    [Fact]
    public async Task A_IMPORTACAO_DE_OUTRA_EMPRESA_NAO_EXISTE()
    {
        var (db, tx, servico, _, ctx) = await PrepararComContextoAsync("tenant-a");
        using var _1 = db; using var _2 = tx;

        var deA = await servico.ReceberAsync("leads.csv",
            Arquivo(Cabecalho, Lead("8001", "Maria", "+5584988887777")), default);

        var b = await Semeador.TenantAsync(db, "importacao-meta-tenant-b");
        ctx.EmpresaId = b.Id;
        ctx.UsuarioId = b.Dono.Id;
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.PreverAsync(deA.Id, deA.Mapeamento, default));
        Assert.Equal(404, erro.StatusHttp);

        // E nem a importação nem as linhas aparecem numa leitura comum.
        Assert.False(await db.Importacoes.AnyAsync(i => i.Id == deA.Id));
        Assert.False(await db.ImportacaoLinhas.AnyAsync(l => l.ImportacaoId == deA.Id));
    }

    [Fact]
    public async Task VENDEDOR_NAO_IMPORTA()
    {
        var (db, tx, servico, _, ctx) = await PrepararComContextoAsync("papel");
        using var _1 = db; using var _2 = tx;

        ctx.Papel = "vendedor";

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(() => servico.ReceberAsync(
            "leads.csv", Arquivo(Cabecalho, Lead("9001", "Maria", "+5584988887777")), default));
        Assert.Contains("dono ou um gestor", erro.Message);
    }

    // ====================================================================
    private static async Task<long> NovoContatoAsync(
        NexoraDbContext db, Cenario c, string nome, string telefone, string? metaLeadId)
    {
        var contato = new Contato
        {
            EmpresaId = c.Id, Nome = nome, Telefone = telefone, MetaLeadId = metaLeadId,
            Origem = metaLeadId is null ? OrigemLead.Whatsapp : OrigemLead.MetaAds
        };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return contato.Id;
    }

    private async Task<(NexoraDbContext, IDbContextTransaction, ServicoImportacaoMeta, Cenario)>
        PrepararAsync(string sufixo)
    {
        var (db, tx, servico, c, _) = await PrepararComContextoAsync(sufixo);
        return (db, tx, servico, c);
    }

    private async Task<(NexoraDbContext, IDbContextTransaction, ServicoImportacaoMeta, Cenario,
        ContextoMutavel)> PrepararComContextoAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var c = await Semeador.TenantAsync(db, $"importacao-meta-{sufixo}");
        ctx.EmpresaId = c.Id;
        ctx.UsuarioId = c.Dono.Id;
        ctx.Papel = "dono";
        db.ChangeTracker.Clear();

        return (db, tx, new ServicoImportacaoMeta(db, ctx), c, ctx);
    }
}
