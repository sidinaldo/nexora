using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Auditoria;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Conversoes;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;
using Nexora.Tests.Unidade;

namespace Nexora.Tests.Integracao;

/// <summary>===================== AS VENDAS QUE A META NUNCA VIU (INT-5) =====================
///
/// Esta lista é DERIVADA, e é o que a torna delicada: não existe linha nenhuma para ler. Quando o
/// portão da credencial está fechado, `PublicadorConversoes` devolve `void` sem gravar, sem logar e
/// sem contar — o estrago não deixa rastro, só a ausência dele. A lista é "venda fechada que não tem
/// evento".
///
/// ⚠️ O MODO DE FALHA QUE DESTRÓI A TELA é o falso positivo. Uma tela que acusa o produto de ter
/// perdido uma venda que ele ENTREGOU não é só imprecisa: ela não serve mais para nada, porque
/// ninguém vai conferir item por item para saber quais das acusações são verdadeiras. Por isso o
/// primeiro teste do arquivo é esse, e por isso ele passa por todos os cinco status.
///
/// Veio de um caso real: seis vendas fecharam sem chegar à Meta porque o consentimento não estava
/// marcado, e a tela mostrava a integração como configurada.
/// =========================================================================================</summary>
[Collection("banco")]
public class VendasSemConversaoDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset Marco = new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);

    private static SalvarCredencial Conectado => new(
        Identificador: "1234567890123456",
        Token: "EAAGtokenbemlongoparaMascarar",
        CodigoTeste: null,
        PaginaId: null,
        Ativo: true,
        EmLead: true,
        EmCompra: true,
        ConsentimentoDeclarado: true);

    private static SalvarCredencial SemConsentimento => Conectado with { ConsentimentoDeclarado = false };

    // ==================================================================== o falso positivo

    /// <summary>⚠️ O TESTE MAIS IMPORTANTE DO ARQUIVO.
    ///
    /// Venda com evento ENTREGUE não pode aparecer. A `[Theory]` passa pelos cinco status porque a
    /// regra não é "ignore as entregues" — é **qualquer evento tira a venda da lista**:
    ///
    ///   entregue  chegou. Listá-la seria o produto se acusando à toa;
    ///   pendente  está a caminho, em até 60s. O botão seria um `ON CONFLICT DO NOTHING`, e o dono
    ///             concluiria que o botão não funciona;
    ///   falhou    é do botão "Reenviar" que já existe na tabela de baixo. Dois botões para a mesma
    ///             venda, em dois lugares, com semânticas diferentes, é chamado de suporte;
    ///   expirado  o evento foi montado a tempo e a janela fechou. Já aparece marcado embaixo;
    ///   cancelado a linha ocupa a vaga única, então enfileirar de novo nem funcionaria.
    ///
    /// Um teste só com `entregue` deixaria passar um `!Any(Status == Pendente)` escrito por
    /// engano — que é o erro mais natural de se cometer aqui.</summary>
    [Theory]
    [InlineData(StatusConversao.Entregue)]
    [InlineData(StatusConversao.Pendente)]
    [InlineData(StatusConversao.Falhou)]
    [InlineData(StatusConversao.Expirado)]
    [InlineData(StatusConversao.Cancelado)]
    public async Task NENHUM_STATUS_DE_EVENTO_TRAZ_A_VENDA_DE_VOLTA_PARA_A_LISTA(StatusConversao status)
    {
        var (db, tx, amb) = await PrepararAsync($"status-{status}");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        var venda = await VenderAsync(db, amb, 300m);

        db.ChangeTracker.Clear();
        await db.EventosConversao.Where(e => e.NegociacaoId == venda)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, status), default);
        db.ChangeTracker.Clear();

        var lista = (await amb.Conversoes.ObterAsync(default)).VendasSemEnvio;

        Assert.Equal(0, lista.Total);
        Assert.Empty(lista.Vendas);
    }

    // ==================================================================== o caso de produção

    [Fact]
    public async Task SEM_CONSENTIMENTO_A_VENDA_APARECE_COM_NOME_VALOR_E_PRAZO()
    {
        // ===================== O CASO REAL, DE PONTA A PONTA =====================
        // A credencial existe, o token está lá, a tela mostra a integração configurada — e o
        // consentimento não está marcado. A venda fecha e nenhum evento nasce.
        // ========================================================================
        var (db, tx, amb) = await PrepararAsync("producao");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(SemConsentimento, default);
        var venda = await VenderAsync(db, amb, 556.12m);

        // Primeiro a prova de que o buraco é real: nenhum evento nasceu.
        Assert.False(await db.EventosConversao.AnyAsync(e => e.NegociacaoId == venda));

        var painel = await amb.Conversoes.ObterAsync(default);
        var linha = Assert.Single(painel.VendasSemEnvio.Vendas);

        Assert.Equal(venda, linha.NegociacaoId);
        Assert.Equal(556.12m, linha.Valor);
        Assert.Equal(amb.Cenario.Contato.Nome, linha.Contato);
        Assert.False(linha.ForaDoPrazo);

        // O prazo é a MESMA conta do publicador: fechamento + 7 dias.
        Assert.Equal(linha.GanhaEm.AddDays(PoliticaConversao.DiasDeValidade), linha.ExpiraEm);

        // E a tela sabe dizer POR QUE está parado, que é a metade que faltava.
        Assert.Contains(painel.Credencial!.MotivosParados, m => m.Contains("consentimento"));
        Assert.False(painel.Credencial.Enviando);
    }

    [Fact]
    public async Task O_TOTAL_E_A_SOMA_SAO_O_QUE_FAZ_ALGUEM_AGIR()
    {
        // "6 vendas pendentes" se lê como aviso técnico. "R$ 1.527,85 não chegaram à Meta" se lê
        // como prejuízo — e é esse que faz alguém clicar.
        var (db, tx, amb) = await PrepararAsync("totais");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(SemConsentimento, default);
        await VenderAsync(db, amb, 100m);
        await VenderAsync(db, amb, 556.12m, outroContato: "Segunda");
        await VenderAsync(db, amb, 561.23m, outroContato: "Terceira");

        var lista = (await amb.Conversoes.ObterAsync(default)).VendasSemEnvio;

        Assert.Equal(3, lista.Total);
        Assert.Equal(1217.35m, lista.ValorTotal);
    }

    // ==================================================================== o que fica de fora

    [Fact]
    public async Task A_VENDA_CANCELADA_NAO_APARECE_PORQUE_O_DINHEIRO_FOI_DESFEITO()
    {
        // ⚠️ ESTA EXCLUSÃO É LOAD-BEARING, e não defensiva: `ServicoVendas.CancelarAsync` MANTÉM o
        // `ganha_em` de propósito ("NADA de DELETE"). Sem a cláusula, um estorno apareceria como
        // conversão perdida — com um botão oferecendo mandar à Meta um faturamento que o dono
        // acabou de desfazer.
        var (db, tx, amb) = await PrepararAsync("cancelada");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(SemConsentimento, default);
        var venda = await VenderAsync(db, amb, 200m);

        await db.Negociacoes.Where(n => n.Id == venda)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, StatusNegociacao.Cancelada)
                .SetProperty(n => n.CanceladaEm, Marco.UtcDateTime), default);
        db.ChangeTracker.Clear();

        // O `ganha_em` continua lá — é o que torna a cláusula necessária.
        Assert.NotNull((await db.Negociacoes.AsNoTracking().SingleAsync(n => n.Id == venda)).GanhaEm);

        Assert.Equal(0, (await amb.Conversoes.ObterAsync(default)).VendasSemEnvio.Total);
    }

    [Fact]
    public async Task A_NEGOCIACAO_ABERTA_NAO_APARECE()
    {
        // Não há venda a relatar. E é a mesma condição que impede o publicador de inventar um
        // `event_time`: sem `ganha_em`, ele usaria `agora`.
        var (db, tx, amb) = await PrepararAsync("aberta");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(SemConsentimento, default);

        Assert.Equal(0, (await amb.Conversoes.ObterAsync(default)).VendasSemEnvio.Total);
    }

    [Fact]
    public async Task O_CONTATO_ANONIMIZADO_SAI_DA_LISTA()
    {
        // O titular pediu para sumir. Mandar um evento NOVO sobre ele depois disso desfaria o
        // pedido — e o evento iria sem telefone, sem e-mail e sem `fbc` de qualquer forma.
        var (db, tx, amb) = await PrepararAsync("anonimizado");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(SemConsentimento, default);
        await VenderAsync(db, amb, 150m);

        await db.Contatos.Where(c => c.Id == amb.Cenario.Contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AnonimizadoEm, Marco.UtcDateTime), default);
        db.ChangeTracker.Clear();

        Assert.Equal(0, (await amb.Conversoes.ObterAsync(default)).VendasSemEnvio.Total);
    }

    [Fact]
    public async Task A_VENDA_ANTERIOR_A_CONEXAO_DO_PIXEL_NAO_APARECE()
    {
        // ===================== O DIA UM NÃO PODE SER UM MURO =====================
        // Sem este piso, a empresa que conecta hoje abre a tela com três semanas de vendas antigas
        // marcadas como "não enviadas" — vinte linhas de acusação na única tela que precisa ganhar
        // a confiança dela. Antes de conectar não havia para onde mandar.
        //
        // ⚠️ Duas vendas de propósito: uma antes e uma depois. Com só a de antes, um piso ERRADO
        // (ou nenhum) passaria igual — é o par que prova onde o corte está.
        // ========================================================================
        var (db, tx, amb) = await PrepararAsync("antes-da-conexao");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(SemConsentimento, default);

        var criadaEm = (await db.CredenciaisConversao.AsNoTracking().SingleAsync()).CriadoEm;

        var antes = await VenderAsync(db, amb, 111m);
        var depois = await VenderAsync(db, amb, 222m, outroContato: "Depois");

        await EmpurrarAsync(db, antes, criadaEm.AddDays(-1));
        await EmpurrarAsync(db, depois, criadaEm.AddHours(1));

        var lista = (await amb.Conversoes.ObterAsync(default)).VendasSemEnvio;

        Assert.Equal(depois, Assert.Single(lista.Vendas).NegociacaoId);
    }

    [Fact]
    public async Task A_VENDA_FORA_DA_JANELA_DA_LISTA_NAO_APARECE()
    {
        // Além da janela, "nunca foi enfileirada" e "foi entregue e o registro foi expurgado" são a
        // mesma observação. Calar é melhor que acusar à toa.
        var (db, tx, amb) = await PrepararAsync("fora-da-janela");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(SemConsentimento, default);

        var velha = await VenderAsync(db, amb, 333m);
        var recente = await VenderAsync(db, amb, 444m, outroContato: "Recente");

        var janela = PoliticaConversao.DiasDaListaDeNaoEnviadas;
        await EmpurrarAsync(db, velha, Marco.UtcDateTime.AddDays(-janela - 1));
        await EmpurrarAsync(db, recente, Marco.UtcDateTime.AddDays(-janela + 1));
        // A credencial é mais velha que as duas, para o piso da conexão não ser o que corta.
        await db.CredenciaisConversao
            .ExecuteUpdateAsync(s => s.SetProperty(
                c => c.CriadoEm, Marco.UtcDateTime.AddDays(-90)), default);
        db.ChangeTracker.Clear();

        Assert.Equal(recente,
            Assert.Single((await amb.Conversoes.ObterAsync(default)).VendasSemEnvio.Vendas).NegociacaoId);
    }

    [Fact]
    public async Task SEM_CREDENCIAL_A_LISTA_VEM_VAZIA()
    {
        // Não há para onde mandar. Cobrar de quem nunca conectou é cobrar uma escolha que ele ainda
        // não fez — e a tela já tem outro aviso para isso.
        var (db, tx, amb) = await PrepararAsync("sem-credencial");
        using var _ = db; using var __ = tx;

        await VenderAsync(db, amb, 500m);

        var painel = await amb.Conversoes.ObterAsync(default);

        Assert.Null(painel.Credencial);
        Assert.Equal(0, painel.VendasSemEnvio.Total);
    }

    // ==================================================================== a ordem, e o teto

    [Fact]
    public async Task A_ORDEM_POE_PRIMEIRO_QUEM_AINDA_DA_TEMPO()
    {
        // ===================== O TETO NÃO PODE ESCONDER UM BOTÃO =====================
        // Com `ORDER BY ganha_em` puro, uma empresa com muitas vendas vencidas enche a página com
        // linhas que NÃO têm botão, e as poucas que ainda dava para salvar ficam invisíveis — a
        // tela existiria e não serviria para nada.
        // ===========================================================================
        var (db, tx, amb) = await PrepararAsync("ordem");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(SemConsentimento, default);
        await db.CredenciaisConversao
            .ExecuteUpdateAsync(s => s.SetProperty(
                c => c.CriadoEm, Marco.UtcDateTime.AddDays(-90)), default);
        db.ChangeTracker.Clear();

        // Duas vencidas, fechadas ANTES — então pelo tempo elas viriam primeiro.
        var vencidaA = await VenderAsync(db, amb, 10m, outroContato: "VencidaA");
        var vencidaB = await VenderAsync(db, amb, 20m, outroContato: "VencidaB");
        var noPrazo = await VenderAsync(db, amb, 30m, outroContato: "NoPrazo");

        await EmpurrarAsync(db, vencidaA, Marco.UtcDateTime.AddDays(-15));
        await EmpurrarAsync(db, vencidaB, Marco.UtcDateTime.AddDays(-12));
        await EmpurrarAsync(db, noPrazo, Marco.UtcDateTime.AddDays(-1));

        var vendas = (await amb.Conversoes.ObterAsync(default)).VendasSemEnvio.Vendas;

        Assert.Equal(noPrazo, vendas[0].NegociacaoId);
        Assert.False(vendas[0].ForaDoPrazo);
        Assert.True(vendas[1].ForaDoPrazo);
    }

    // ==================================================================== apoio

    /// <summary>Fecha uma venda pelo caminho REAL — `MarcarGanhoAsync` —, que é o ponto onde o
    /// publicador é chamado. Uma chamada de teste direta ao publicador provaria outra coisa.</summary>
    private static async Task<long> VenderAsync(
        NexoraDbContext db, Ambiente amb, decimal valor, string? outroContato = null)
    {
        var contatoId = amb.Cenario.Contato.Id;

        if (outroContato is not null)
        {
            var contato = new Contato
            {
                EmpresaId = amb.Cenario.Id,
                Nome = outroContato,
                Telefone = $"5584 9{Random.Shared.Next(1000, 9999)}{Random.Shared.Next(1000, 9999)}"
            };
            db.Contatos.Add(contato);
            db.Negociacoes.Add(new Negociacao
            {
                EmpresaId = amb.Cenario.Id,
                Contato = contato,
                PipelineId = amb.Cenario.Pipeline.Id,
                EtapaId = amb.Cenario.PrimeiraEtapa.Id,
                Status = StatusNegociacao.Aberta
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            contatoId = contato.Id;
        }

        await amb.Contatos.MarcarGanhoAsync(contatoId, valor, null, null, default);
        db.ChangeTracker.Clear();

        return await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == contatoId && n.GanhaEm != null)
            .Select(n => n.Id).FirstAsync();
    }

    /// <summary>Empurra o fechamento para trás no tempo. O relógio dos testes é congelado, então
    /// mexer na data é o único jeito de atravessar as janelas.</summary>
    private static async Task EmpurrarAsync(NexoraDbContext db, long negociacaoId, DateTime quando)
    {
        await db.Negociacoes.Where(n => n.Id == negociacaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.GanhaEm, quando), default);
        db.ChangeTracker.Clear();
    }

    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto, RelogioFalso Relogio,
        IServicoConversoes Conversoes, IServicoContatos Contatos);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(Marco);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"semconv-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        // ⚠️ O PUBLICADOR DE VERDADE, e nao um falso: o buraco que este arquivo testa esta DENTRO
        // dele — o `return` silencioso quando o portao esta fechado. Um duble que sempre enfileira
        // (ou que nunca enfileira) tornaria todos os testes daqui verdadeiros por construcao.
        var publicador = new PublicadorConversoes(
            db, relogio, PublicadorConversoesDeTeste.Opcoes,
            NullLogger<PublicadorConversoes>.Instance);

        return (db, tx, new Ambiente(
            cenario, ctx, relogio,
            new ServicoConversoes(db, ctx, new ClienteMetaFalso(), relogio),
            new ServicoContatos(db, ctx, PublicadorDeTeste.Novo(db, relogio), publicador,
                new ColetorAuditoria(), relogio)));
    }
}
