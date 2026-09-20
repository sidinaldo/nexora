using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Auditoria;
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

    // ==================================================================== a origem (o conserto)
    /// <summary>⚠️ A PLANILHA COMUM NÃO É LEAD DE ANÚNCIO — e esta tela gravava todo mundo como
    /// `meta_ads`, fixo no código.
    ///
    /// A tela absorveu a importação da issue #8, cujo caso é a base que o cliente novo sobe no
    /// primeiro dia. Com a origem fixa, a padaria com 800 clientes ficava com 800 "leads de
    /// anúncio" no cadastro e no relatório de origem.
    ///
    /// Três coisas num teste só, porque só juntas elas descrevem a regra: o servidor SUGERE pelo
    /// cabeçalho, a coluna `origem` manda quando diz algo que entendemos, e o resto fica com a
    /// escolha do dono.</summary>
    [Fact]
    public async Task A_PLANILHA_COMUM_NAO_ENTRA_COMO_LEAD_DE_ANUNCIO()
    {
        var (db, tx, servico, _) = await PrepararAsync("origem-planilha");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("clientes.csv", Arquivo(
            "nome;telefone;origem;observacoes",
            "Maria Silva;84988887777;indicacao;cliente antiga",
            "João Souza;84999996666;;sem origem na planilha",
            "Ana Paula;84911112222;panfleto da esquina;origem que não existe aqui"), default);

        // ---------- o servidor sugere pelo cabeçalho: sem metadados da Meta, é planilha
        Assert.Equal(OrigemLead.Manual, r.OrigemSugerida);

        // ---------- e reconhece as colunas que a importação da #8 já reconhecia
        var m = r.Mapeamento.ToDictionary(x => x.Coluna, x => x.Campo);
        Assert.Equal(CampoImportacao.Origem, m["origem"]);
        Assert.Equal(CampoImportacao.Observacoes, m["observacoes"]);

        // ---------- o dono responde "vieram do site"
        await servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento, Origem: OrigemLead.Site), default);

        db.ChangeTracker.Clear();
        var porTelefone = await db.Contatos.AsNoTracking()
            .Where(c => c.Telefone.StartsWith("55849"))
            .ToDictionaryAsync(c => c.Telefone, c => c);

        // A linha que disse "indicacao" vale o que ela disse…
        Assert.Equal(OrigemLead.Indicacao, porTelefone["5584988887777"].Origem);

        // …a que não disse nada fica com a escolha do dono…
        Assert.Equal(OrigemLead.Site, porTelefone["5584999996666"].Origem);

        // …e a que disse algo que não existe no Nexora TAMBÉM fica com a escolha dele. Cair em
        // `Manual` aqui atropelaria em silêncio o que ele respondeu.
        Assert.Equal(OrigemLead.Site, porTelefone["5584911112222"].Origem);

        // E a observação entrou, com a pergunta na frente.
        Assert.Contains("cliente antiga", porTelefone["5584988887777"].Observacoes);
    }

    /// <summary>O export da Meta continua sendo lead de anúncio — e a sugestão vem respondida, para
    /// o dono não ter de dizer o óbvio em cada importação.</summary>
    [Fact]
    public async Task O_EXPORT_DA_META_JA_CHEGA_COMO_META_ADS()
    {
        var (db, tx, servico, _) = await PrepararAsync("origem-meta");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho, Lead("7101", "Maria", "+5584988887777")), default);

        Assert.Equal(OrigemLead.MetaAds, r.OrigemSugerida);

        // Sem `Origem` no pedido — o cliente da API que não conhece o campo mantém a sugestão.
        await servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default);

        db.ChangeTracker.Clear();
        var maria = await db.Contatos.AsNoTracking().SingleAsync(x => x.Telefone == "5584988887777");
        Assert.Equal(OrigemLead.MetaAds, maria.Origem);
    }

    // ==================================================================== a gravação (commit 3)
    /// <summary>⚠️ O QUE A PRÉVIA MOSTROU É O QUE ENTRA. O contato nasce com `origem = meta_ads`, os
    /// `meta_*` do anúncio, a pergunta do formulário nas observações (critério 6) e a DATA EM QUE A
    /// PESSOA PREENCHEU — não a de hoje.
    ///
    /// A data é o teste do afrouxamento do interceptor da auditoria (commit 1): sem ele, o lead de
    /// três dias atrás entraria como "hoje" e poluiria "leads de hoje" no dashboard.</summary>
    [Fact]
    public async Task GRAVAR_CRIA_O_CONTATO_COM_A_ORIGEM_OS_IDS_E_A_DATA_DO_ANUNCIO()
    {
        var (db, tx, servico, _) = await PrepararAsync("gravar");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho,
            Lead("1001", "Maria Silva", "+5584988887777", "até 5 mil")), default);

        // ⚠️ CRITÉRIO 6: a pergunta que o cliente criou no formulário chega no campo que o DONO
        // escolher. A sugestão a deixa em `ignorar` de propósito — ninguém além dele sabe se
        // "Qual seu orçamento?" é observação, e o mapeamento manual é o passo que existe para isso.
        var mapeamento = r.Mapeamento
            .Select(x => x.Coluna == "Qual seu orçamento?"
                ? x with { Campo = CampoImportacao.Observacoes }
                : x)
            .ToList();

        var previa = await servico.PreverAsync(r.Id, mapeamento, default);
        Assert.Equal(1, previa.Novos);

        var fim = await servico.GravarAsync(r.Id, new GravarImportacao(mapeamento), default);

        Assert.Equal(1, fim.Importados);
        Assert.Equal(StatusImportacao.Concluida, fim.Status);

        db.ChangeTracker.Clear();
        var maria = await db.Contatos.AsNoTracking().SingleAsync(x => x.Telefone == "5584988887777");

        Assert.Equal("Maria Silva", maria.Nome);
        Assert.Equal(OrigemLead.MetaAds, maria.Origem);
        Assert.Equal("1001", maria.MetaLeadId);
        Assert.Equal("120201", maria.MetaAdId);
        Assert.Equal("120203", maria.MetaCampaignId);
        Assert.Equal("120204", maria.MetaFormId);
        Assert.Equal("Campanha Setembro", maria.OrigemDetalhe);

        // Critério 6: a coluna que o cliente criou no formulário chega ao vendedor, com a pergunta.
        Assert.Equal("Qual seu orçamento?: até 5 mil", maria.Observacoes);

        // `created_time` do arquivo, em UTC.
        Assert.Equal(new DateTime(2026, 9, 10, 14, 32, 11, DateTimeKind.Utc), maria.CriadoEm);

        // E a linha ficou carimbada, apontando para o contato que ela virou.
        var linha = await db.ImportacaoLinhas.AsNoTracking().SingleAsync(l => l.ImportacaoId == r.Id);
        Assert.Equal(ResultadoLinha.Importado, linha.Resultado);
        Assert.Equal(maria.Id, linha.ContatoId);
        Assert.Null(linha.Motivo);
    }

    /// <summary>⚠️ CRITÉRIO 3, A METADE QUE SÓ A GRAVAÇÃO PROVA: o lead que já tinha entrado pelo
    /// WhatsApp NÃO vira um segundo contato — ele ganha os `meta_*` que estavam nulos, e o nome que
    /// o vendedor escreveu continua o dele.
    ///
    /// "Enriquecer é melhor que duplicar": sem isto, o cliente fica com a mesma pessoa duas vezes e
    /// o histórico da conversa numa delas só.</summary>
    [Fact]
    public async Task O_LEAD_DO_WHATSAPP_GANHA_OS_META_SEM_DUPLICAR_NEM_PERDER_O_NOME()
    {
        var (db, tx, servico, c) = await PrepararAsync("enriquecer");
        using var _1 = db; using var _2 = tx;

        var doWhatsapp = await NovoContatoAsync(db, c, "Maria (WhatsApp)", "5584988887777", metaLeadId: null);
        await db.Contatos.Where(x => x.Id == doWhatsapp)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Observacoes, "ligar depois das 18h"));
        db.ChangeTracker.Clear();

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho, Lead("2001", "MARIA DA PLANILHA", "+5584988887777")), default);

        var fim = await servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default);

        Assert.Equal(0, fim.Importados);
        Assert.Equal(1, fim.Duplicados);

        db.ChangeTracker.Clear();
        var maria = await db.Contatos.AsNoTracking().SingleAsync(x => x.Telefone == "5584988887777");

        // Ganhou de onde veio…
        Assert.Equal("2001", maria.MetaLeadId);
        Assert.Equal("120201", maria.MetaAdId);
        Assert.Equal("Campanha Setembro", maria.OrigemDetalhe);

        // …sem perder o que o vendedor tinha escrito, e sem virar "lead da Meta" no cadastro.
        Assert.Equal("Maria (WhatsApp)", maria.Nome);
        Assert.Equal("ligar depois das 18h", maria.Observacoes);
        Assert.Equal(OrigemLead.Whatsapp, maria.Origem);

        // UMA pessoa só, e a linha aponta para ela.
        Assert.Equal(1, await db.Contatos.CountAsync(x => x.Telefone == "5584988887777"));
        var linha = await db.ImportacaoLinhas.AsNoTracking().SingleAsync(l => l.ImportacaoId == r.Id);
        Assert.Equal(ResultadoLinha.Duplicado, linha.Resultado);
        Assert.Equal("telefone_ja_cadastrado", linha.Motivo);
        Assert.Equal(maria.Id, linha.ContatoId);
    }

    /// <summary>⚠️ CRITÉRIO 2: o mesmo arquivo importado duas vezes não duplica ninguém.
    ///
    /// É o erro mais fácil de cometer — o dono não lembra se já importou o export de segunda — e o
    /// mais caro: a base dobra, e desfazer é na mão.</summary>
    [Fact]
    public async Task O_MESMO_ARQUIVO_DUAS_VEZES_NAO_DUPLICA_NINGUEM()
    {
        var (db, tx, servico, _) = await PrepararAsync("duas-vezes");
        using var _1 = db; using var _2 = tx;

        var arquivo = Arquivo(
            Cabecalho,
            Lead("3001", "Maria Silva", "+5584988887777"),
            Lead("3002", "João Souza", "+5584999996666"));

        var primeira = await servico.ReceberAsync("leads.csv", arquivo, default);
        var r1 = await servico.GravarAsync(primeira.Id, new GravarImportacao(primeira.Mapeamento), default);
        Assert.Equal(2, r1.Importados);

        db.ChangeTracker.Clear();

        // O MESMO arquivo, subido de novo — é o que a pessoa faz quando não lembra.
        var segunda = await servico.ReceberAsync("leads.csv", arquivo, default);
        var r2 = await servico.GravarAsync(segunda.Id, new GravarImportacao(segunda.Mapeamento), default);

        Assert.Equal(0, r2.Importados);
        Assert.Equal(2, r2.Duplicados);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Contatos.CountAsync(x => x.Telefone == "5584988887777"));
        Assert.Equal(1, await db.Contatos.CountAsync(x => x.Telefone == "5584999996666"));
    }

    /// <summary>⚠️ CRITÉRIO 4: telefone ilegível não derruba o resto. As boas entram, e cada recusa
    /// fica registrada com o número DA LINHA DO EXCEL — é ele que a pessoa procura na planilha.
    ///
    /// Sem isto, um export com três linhas sujas perderia as outras 9.997.</summary>
    [Fact]
    public async Task TELEFONE_INVALIDO_NAO_DERRUBA_O_RESTO_E_A_LINHA_FICA_REGISTRADA()
    {
        var (db, tx, servico, _) = await PrepararAsync("invalidos");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho,
            Lead("4001", "Boa 1", "+5584988887777"),
            Lead("4002", "Curta", "123"),
            // A coluna VAZIA, e nao `p:` sozinho: sao motivos diferentes, e o teste quer os dois.
            Lead("4003", "Sem telefone", "").Replace(",p:,", ",,"),
            Lead("4004", "Letras", "nao tenho"),
            Lead("4005", "Boa 2", "+5584999996666")), default);

        var fim = await servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default);

        Assert.Equal(2, fim.Importados);
        Assert.Equal(3, fim.Invalidos);
        Assert.Equal(StatusImportacao.Concluida, fim.Status);

        db.ChangeTracker.Clear();
        var recusadas = await db.ImportacaoLinhas.AsNoTracking()
            .Where(l => l.ImportacaoId == r.Id && l.Resultado == ResultadoLinha.Invalido)
            .OrderBy(l => l.NumeroLinha)
            .Select(l => new { l.NumeroLinha, l.Motivo, l.ContatoId })
            .ToListAsync();

        // As linhas 3, 4 e 5 do EXCEL — cabeçalho é a 1.
        Assert.Equal([3, 4, 5], recusadas.Select(x => x.NumeroLinha));
        Assert.Equal(["telefone_invalido", "telefone_ausente", "telefone_invalido"],
            recusadas.Select(x => x.Motivo));
        Assert.All(recusadas, x => Assert.Null(x.ContatoId));
    }

    /// <summary>O funil é OPCIONAL e vazio por padrão — 10.000 cards de uma vez é um quadro
    /// inutilizável. Quem escolhe, recebe um card por lead importado, na etapa de entrada; e o
    /// responsável escolhido vale para todos.</summary>
    [Fact]
    public async Task COM_FUNIL_ESCOLHIDO_CADA_LEAD_VIRA_UM_CARD_COM_RESPONSAVEL()
    {
        var (db, tx, servico, c) = await PrepararAsync("com-funil");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho,
            Lead("6001", "Maria", "+5584988887777"),
            Lead("6002", "João", "+5584999996666")), default);

        await servico.GravarAsync(r.Id, new GravarImportacao(
            r.Mapeamento, PipelineId: c.Pipeline.Id, ResponsavelId: c.Dono.Id), default);

        db.ChangeTracker.Clear();
        var importados = await db.Contatos.AsNoTracking()
            .Where(x => x.Telefone == "5584988887777" || x.Telefone == "5584999996666")
            .ToListAsync();

        Assert.All(importados, x => Assert.Equal(c.Dono.Id, x.ResponsavelId));

        var ids = importados.Select(x => x.Id).ToList();
        var negocios = await db.Negociacoes.AsNoTracking()
            .Where(n => ids.Contains(n.ContatoId)).ToListAsync();

        Assert.Equal(2, negocios.Count);
        Assert.All(negocios, n => Assert.Equal(c.PrimeiraEtapa.Id, n.EtapaId));
        // Posições diferentes: todos na mesma ordem empilhariam os cards numa ordem que o banco
        // escolhe, e arrastar um mexeria na vizinhança errada.
        Assert.Equal(2, negocios.Select(n => n.OrdemKanban).Distinct().Count());
    }

    /// <summary>Sem funil — o padrão — ninguém entra no quadro. O lead fica na base, e alguém decide
    /// depois que virou negócio.</summary>
    [Fact]
    public async Task SEM_FUNIL_NINGUEM_ENTRA_NO_QUADRO()
    {
        var (db, tx, servico, _) = await PrepararAsync("sem-funil");
        using var _1 = db; using var _2 = tx;

        var antes = await db.Negociacoes.CountAsync();

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho, Lead("7001", "Maria", "+5584988887777")), default);
        await servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default);

        db.ChangeTracker.Clear();
        Assert.Equal(antes, await db.Negociacoes.CountAsync());
    }

    /// <summary>⚠️ GRAVAR DUAS VEZES A MESMA IMPORTAÇÃO É RECUSADO. Sem isto, o segundo clique
    /// encontraria os contatos que ELE MESMO criou, contaria todos como duplicados e sobrescreveria
    /// "487 importados" por "0 importados" — a tela diria que o arquivo não trouxe ninguém.</summary>
    [Fact]
    public async Task GRAVAR_A_MESMA_IMPORTACAO_DE_NOVO_E_RECUSADO()
    {
        var (db, tx, servico, _) = await PrepararAsync("duas-gravacoes");
        using var _1 = db; using var _2 = tx;

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho, Lead("8001", "Maria", "+5584988887777")), default);

        await servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default));

        Assert.True(erro.Conflito);
        Assert.Contains("já foi processada", erro.Message);

        db.ChangeTracker.Clear();
        var imp = await db.Importacoes.AsNoTracking().SingleAsync(i => i.Id == r.Id);
        Assert.Equal(1, imp.Importados);   // o número da PRIMEIRA vez continua lá
    }

    /// <summary>A caixinha do aviso vem MARCADA aqui, ao contrário da planilha comum: o lead do
    /// anúncio é de ontem, e a automação do cliente é o que ele quer que rode. Marcada, sai um
    /// `lead.criado` por lead CRIADO — o enriquecido não nasceu agora.</summary>
    [Fact]
    public async Task O_AVISO_VEM_MARCADO_E_SO_OS_CRIADOS_AVISAM()
    {
        var (db, tx, servico, c) = await PrepararAsync("aviso");
        using var _1 = db; using var _2 = tx;

        db.WebhooksSaida.Add(new WebhookSaida
        {
            EmpresaId = c.Id, Url = "https://webhook.cliente.com/nexora", Segredo = "segredo-de-teste"
        });
        await db.SaveChangesAsync();

        var jaExiste = await NovoContatoAsync(db, c, "Do WhatsApp", "5584911112222", metaLeadId: null);

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho,
            Lead("9101", "Maria", "+5584988887777"),
            Lead("9102", "Repetida", "+5584911112222")), default);

        var previa = await servico.PreverAsync(r.Id, r.Mapeamento, default);
        Assert.Equal(new AvisoIntegracoes(true, true), previa.Aviso);

        await servico.GravarAsync(r.Id, new GravarImportacao(
            r.Mapeamento, AvisarIntegracoes: true), default);

        db.ChangeTracker.Clear();
        var entregas = await db.EntregasWebhook.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.EmpresaId == c.Id).ToListAsync();

        var maria = await db.Contatos.AsNoTracking().SingleAsync(x => x.Telefone == "5584988887777");

        var entrega = Assert.Single(entregas);
        Assert.Equal(EventoWebhook.LeadCriado, entrega.Evento);
        Assert.True(entrega.EmMassa);   // fim da fila: ver `EntregaWebhook.EmMassa`
        // O id do CONTATO vem dentro de `dados`; o `id` de fora é o do evento.
        using var doc = JsonDocument.Parse(entrega.Payload);
        Assert.Equal(maria.Id, doc.RootElement.GetProperty("dados").GetProperty("id").GetInt64());
        Assert.NotEqual(jaExiste, doc.RootElement.GetProperty("dados").GetProperty("id").GetInt64());
    }

    /// <summary>E sem marcar, a importação é muda — mesmo com a integração ligada.</summary>
    [Fact]
    public async Task SEM_MARCAR_O_AVISO_A_IMPORTACAO_E_MUDA()
    {
        var (db, tx, servico, c) = await PrepararAsync("aviso-nao");
        using var _1 = db; using var _2 = tx;

        db.WebhooksSaida.Add(new WebhookSaida
        {
            EmpresaId = c.Id, Url = "https://webhook.cliente.com/nexora", Segredo = "segredo-de-teste"
        });
        await db.SaveChangesAsync();

        var r = await servico.ReceberAsync("leads.csv", Arquivo(
            Cabecalho, Lead("9201", "Maria", "+5584988887777")), default);

        await servico.GravarAsync(r.Id, new GravarImportacao(r.Mapeamento), default);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.EntregasWebhook.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.EmpresaId == c.Id).ToListAsync());
    }

    /// <summary>⚠️ 10.000 LINHAS NÃO PODEM SER 10.000 IDAS AO BANCO. A gravação é em lotes, e o
    /// número de consultas tem de crescer com os LOTES, não com as linhas.
    ///
    /// O teste compara 20 linhas com 80: quatro vezes mais leads, o mesmo número de lotes (um), e
    /// portanto o mesmo número de comandos. Com uma consulta por linha, o segundo número explode.</summary>
    [Fact]
    public async Task GRAVAR_NAO_CONSULTA_O_BANCO_POR_LINHA()
    {
        var contador = new ContadorDeComandos();
        var ctx = new ContextoMutavel();
        var trilha = new ColetorAuditoria();
        using var db = banco.NovoContexto(ctx, coletor: trilha, contador: contador);
        using var tx = await db.Database.BeginTransactionAsync();

        var c = await Semeador.TenantAsync(db, "importacao-meta-nmais1");
        ctx.EmpresaId = c.Id;
        ctx.UsuarioId = c.Dono.Id;
        ctx.Papel = "dono";
        db.ChangeTracker.Clear();

        var servico = new ServicoImportacaoMeta(db, ctx, PublicadorDeTeste.Novo(db), trilha, TimeProvider.System);

        byte[] ArquivoCom(int quantas, int baseTelefone)
        {
            var linhas = new List<string> { Cabecalho };
            for (var i = 0; i < quantas; i++)
                linhas.Add(Lead($"{baseTelefone + i}", $"Pessoa {i}", $"+55849{baseTelefone + i:D7}"));
            return Arquivo([.. linhas]);
        }

        async Task<int> ComandosAoGravar(byte[] arquivo)
        {
            var r = await servico.ReceberAsync("leads.csv", arquivo, default);

            contador.Zerar();
            await servico.GravarAsync(r.Id, new GravarImportacao(
                r.Mapeamento, PipelineId: c.Pipeline.Id), default);

            return contador.Comandos.Count;
        }

        var arquivo20 = ArquivoCom(20, 1_000_000);
        var arquivo80 = ArquivoCom(80, 2_000_000);

        // ---------- criando
        var novos20 = await ComandosAoGravar(arquivo20);
        var novos80 = await ComandosAoGravar(arquivo80);

        Assert.True(novos20 == novos80,
            $"criar 20 fez {novos20} comandos e criar 80 fez {novos80} — a gravação cresce com o arquivo");

        // ---------- ⚠️ E ENRIQUECENDO, que é o outro caminho. Os MESMOS arquivos de novo: agora
        // toda linha é duplicada, e é aqui que uma consulta por linha se esconde — o teste que só
        // media a criação passava com ela dentro.
        var duplicados20 = await ComandosAoGravar(arquivo20);
        var duplicados80 = await ComandosAoGravar(arquivo80);

        Assert.True(duplicados20 == duplicados80,
            $"enriquecer 20 fez {duplicados20} comandos e 80 fez {duplicados80} — cresce com o arquivo");
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
        // ⚠️ O MESMO COLETOR no contexto e no servico (AUD-1): com dois, o servico declara num
        // objeto que ninguem le e a trilha some, sem erro nenhum.
        var trilha = new ColetorAuditoria();
        var db = banco.NovoContexto(ctx, coletor: trilha);
        var tx = await db.Database.BeginTransactionAsync();

        var c = await Semeador.TenantAsync(db, $"importacao-meta-{sufixo}");
        ctx.EmpresaId = c.Id;
        ctx.UsuarioId = c.Dono.Id;
        ctx.Papel = "dono";
        db.ChangeTracker.Clear();

        // O publicador REAL, como nos outros testes de banco: um dublê esconderia justamente a
        // consulta que decide se o evento sai. Ver `PublicadorDeTeste`.
        return (db, tx, new ServicoImportacaoMeta(db, ctx, PublicadorDeTeste.Novo(db), trilha, TimeProvider.System), c, ctx);
    }
}
