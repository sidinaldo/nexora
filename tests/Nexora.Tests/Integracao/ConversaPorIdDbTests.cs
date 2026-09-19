using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Api.Controllers;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>`GET /api/conversas/{id}` — a única rota nova do DES-2.
///
/// ===================== POR QUE ELA PRECISOU EXISTIR =====================
/// A lista da caixa é por CURSOR e o cliente carrega só a primeira página. O Meu Dia manda o
/// vendedor direto para uma conversa (`/caixa?conversa=N`); se ela estiver na página 4, não havia
/// o que selecionar e a tela abria vazia — sem erro e sem explicação.
///
/// Rota nova em caminho autenticado é rota nova para vazar tenant. É disso que este arquivo
/// trata: o teste do isolamento vem primeiro e é o motivo de o arquivo existir.
/// ======================================================================= */</summary>
[Collection("banco")]
public class ConversaPorIdDbTests(BancoTeste banco)
{
    // ==================================================================== isolamento
    [Fact]
    public async Task CONVERSA_DE_OUTRA_EMPRESA_NAO_E_ENCONTRADA()
    {
        // ===================== O QUE ISTO IMPEDE =====================
        // Buscar por id é o caminho mais fácil de furar tenant: basta esquecer o filtro e a rota
        // vira um leitor universal de conversas — `/api/conversas/1`, `/2`, `/3`. O isolamento
        // aqui vem do query filter global, e este teste é o que prova que ele está no caminho.
        // ============================================================
        var (db, tx, ctx) = await PrepararAsync();
        using var _ = db; using var __ = tx;

        var a = await Semeador.TenantAsync(db, "conversa-id-a");
        var b = await Semeador.TenantAsync(db, "conversa-id-b");

        var servico = new ServicoCaixa(db, ctx);

        // Como A, a conversa de A aparece.
        ctx.EmpresaId = a.Id;
        ctx.UsuarioId = a.Dono.Id;
        Assert.NotNull(await servico.ConversaAsync(a.Conversa.Id, default));

        // Como A, a conversa de B NÃO aparece — mesmo com o id correto em mãos.
        Assert.Null(await servico.ConversaAsync(b.Conversa.Id, default));

        // E o inverso, para o teste não passar por a lista de B estar vazia.
        ctx.EmpresaId = b.Id;
        ctx.UsuarioId = b.Dono.Id;
        Assert.NotNull(await servico.ConversaAsync(b.Conversa.Id, default));
        Assert.Null(await servico.ConversaAsync(a.Conversa.Id, default));
    }

    [Fact]
    public async Task O_CONTROLLER_DEVOLVE_404_PARA_CONVERSA_DE_OUTRO_TENANT()
    {
        // 404 e não 403: 403 confirmaria que a conversa existe em algum lugar. A mensagem é a
        // MESMA da conversa inexistente, de propósito.
        var (db, tx, ctx) = await PrepararAsync();
        using var _ = db; using var __ = tx;

        var a = await Semeador.TenantAsync(db, "controller-a");
        var b = await Semeador.TenantAsync(db, "controller-b");

        ctx.EmpresaId = a.Id;
        ctx.UsuarioId = a.Dono.Id;

        var caixa = new ServicoCaixa(db, ctx);
        var controller = new ConversasController(new ServicoConversasQueNaoEUsado(), caixa);

        Assert.IsType<OkObjectResult>(await controller.Obter(a.Conversa.Id, default));

        var deOutro = await controller.Obter(b.Conversa.Id, default);
        var inexistente = await controller.Obter(999_999, default);

        var naoAchou = Assert.IsType<NotFoundObjectResult>(deOutro);
        var naoExiste = Assert.IsType<NotFoundObjectResult>(inexistente);

        // Corpo IDÊNTICO nos dois casos.
        Assert.Equal(naoExiste.Value!.ToString(), naoAchou.Value!.ToString());
    }

    // ==================================================================== o conteúdo
    [Fact]
    public async Task A_BUSCA_POR_ID_DEVOLVE_A_MESMA_LINHA_QUE_A_LISTA()
    {
        // ===================== POR QUE ISTO É TESTE =====================
        // A lista e a busca por id compartilham a projeção `Resumo`. Se alguém duplicar a
        // expressão — para "só ajustar um campo aqui" —, a conversa aberta pelo Meu Dia passa a
        // mostrar um dado a menos que a MESMA linha na lista, e ninguém entende por quê.
        // ================================================================
        var (db, tx, ctx) = await PrepararAsync();
        using var _ = db; using var __ = tx;

        var c = await Semeador.TenantAsync(db, "mesma-linha");
        ctx.EmpresaId = c.Id;
        ctx.UsuarioId = c.Dono.Id;

        var servico = new ServicoCaixa(db, ctx);

        var lista = await servico.ConversasAsync(FiltroConversa.Todas, null, null, null, null, 30, default);
        var naLista = lista.Itens.Single(x => x.Id == c.Conversa.Id);
        var porId = await servico.ConversaAsync(c.Conversa.Id, default);

        Assert.NotNull(porId);

        // ===================== POR QUE NAO E UM `Assert.Equal` DIRETO =====================
        // `ConversaResumo` e record, e record compara campo a campo — o que era exatamente a
        // graca deste teste. Mas `Etiquetas` e uma COLECAO, e colecao em record compara por
        // REFERENCIA: duas listas vazias, vindas de duas consultas, nunca sao iguais.
        //
        // Entao a comparacao e em duas partes: o resto do record com a colecao zerada nos dois
        // lados, e as etiquetas pelo CONTEUDO. Continua sendo "as duas projecoes concordam" — que
        // e o que o teste existe para provar —, sem depender de um detalhe de igualdade de record
        // que mudaria de novo no proximo campo de colecao.
        // ==============================================================================
        // ⚠️ E ELE MUDOU DE NOVO, exatamente como o paragrafo acima previu: `FunisOcupados`
        // entrou e derrubou este teste (hoje e `FunisDisponiveis`). `[]` para um alvo de
        // array/lista compila para `Array.Empty<T>()`, que e SINGLETON — por isso zerar os dois
        // lados funciona, e por isso funcionava com uma colecao so. Toda colecao nova entra aqui.
        Assert.Equal(
            naLista with { Etiquetas = [], FunisDisponiveis = [] },
            porId! with { Etiquetas = [], FunisDisponiveis = [] });

        Assert.Equal(
            naLista.Etiquetas.Select(e => e.Id),
            porId!.Etiquetas.Select(e => e.Id));

        Assert.Equal(naLista.FunisDisponiveis, porId!.FunisDisponiveis);
    }

    [Fact]
    public async Task A_conversa_e_encontrada_MESMO_estando_fora_da_primeira_pagina()
    {
        // O caso que motivou a rota: o alvo está longe no cursor. Buscar por id tem que achar
        // sem depender de quantas páginas o cliente carregou.
        var (db, tx, ctx) = await PrepararAsync();
        using var _ = db; using var __ = tx;

        var c = await Semeador.TenantAsync(db, "fora-da-pagina");
        ctx.EmpresaId = c.Id;
        ctx.UsuarioId = c.Dono.Id;

        var servico = new ServicoCaixa(db, ctx);

        // Empurra a conversa do cenário para o FIM da ordenação: `ultima_mensagem_em` bem antiga.
        var antiga = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await db.Conversas.IgnoreQueryFilters().Where(x => x.Id == c.Conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UltimaMensagemEm, antiga));

        // Uma conversa MAIS NOVA, para a antiga de fato cair fora de uma página de um item.
        // Sem ela o cenário tem uma conversa só e a página de 1 sempre a contém — o teste
        // passaria sem provar nada.
        var outroContato = new Contato
        {
            EmpresaId = c.Id,
            Nome = "Contato recente",
            Telefone = "5500900009999"
        };
        db.Contatos.Add(outroContato);
        db.Negociacoes.Add(Semeador.Negocio(outroContato, c.PrimeiraEtapa, 9_000m));
        await db.SaveChangesAsync();

        db.Conversas.Add(new Conversa
        {
            EmpresaId = c.Id,
            ContatoId = outroContato.Id,
            ConexaoId = c.Conexao.Id,
            UltimaMensagemEm = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Primeira página com UM item: a conversa alvo não cabe nela.
        var primeira = await servico.ConversasAsync(FiltroConversa.Todas, null, null, null, null, 1, default);
        Assert.Single(primeira.Itens);
        Assert.DoesNotContain(primeira.Itens, x => x.Id == c.Conversa.Id);

        // E mesmo assim a busca por id acha.
        var porId = await servico.ConversaAsync(c.Conversa.Id, default);
        Assert.NotNull(porId);
        Assert.Equal(c.Conversa.Id, porId!.Id);
    }

    // ==================================================================== apoio
    /// <summary>O controller pede `IServicoConversas` para responder e assumir; a rota testada
    /// aqui não o usa. Se algum dia usar, este fake explode em vez de passar por engano.</summary>
    private sealed class ServicoConversasQueNaoEUsado : IServicoConversas
    {
        public Task<RespostaEnviada> ResponderAsync(long conversaId, string texto, CancellationToken ct) =>
            throw new InvalidOperationException("A rota de obter conversa não deveria chamar isto.");
        public Task AssumirAsync(long conversaId, CancellationToken ct) =>
            throw new InvalidOperationException("A rota de obter conversa não deveria chamar isto.");
        public Task LiberarAsync(long conversaId, CancellationToken ct) =>
            throw new InvalidOperationException("A rota de obter conversa não deveria chamar isto.");
        public Task<RespostaEnviada> EnviarMidiaAsync(
            long conversaId, ArquivoParaEnvio arquivo, string? legenda, CancellationToken ct) =>
            throw new InvalidOperationException("A rota de obter conversa não deveria chamar isto.");
        public Task<RespostaEnviada> EnviarAudioAsync(
            long conversaId, ArquivoParaEnvio arquivo, CancellationToken ct) =>
            throw new InvalidOperationException("A rota de obter conversa não deveria chamar isto.");
        public Task<RespostaEnviada> ReenviarAsync(long mensagemId, CancellationToken ct) =>
            throw new InvalidOperationException("A rota de obter conversa não deveria chamar isto.");
    }

    /// <summary>⚠️ A CAIXA OFERECIA UM BOTAO QUE SEMPRE ERRA para contato anonimizado.
    ///
    /// A caixa nao filtra anonimizado — de proposito: a conversa e historico e continua visivel.
    /// Mas `AbrirNegociacaoAsync` recusa contato anonimizado, e a faixa "Abrir negociacao"
    /// aparecia para ele desde que a condicao deixou de ser `ContatoGanhou` e passou a ser "nao
    /// tem negocio aberto" — os anonimizados entraram junto, e nenhum deles tem negocio aberto.
    ///
    /// Este projeto ja trata isso como defeito por escrito: "oferecer um botao que sempre erra e
    /// pior que nao oferecer".</summary>
    [Fact]
    public async Task CONTATO_ANONIMIZADO_NAO_PODE_ABRIR_NEGOCIACAO()
    {
        var (db, tx, ctx) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var c = await Semeador.TenantAsync(db, "anon-caixa");
        ctx.EmpresaId = c.Id;
        ctx.UsuarioId = c.Dono.Id;

        var servico = new ServicoCaixa(db, ctx);

        // Sem negocio aberto, mas VIVO: pode abrir.
        await db.Negociacoes.IgnoreQueryFilters()
            .Where(n => n.ContatoId == c.Contato.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        Assert.True((await servico.ConversaAsync(c.Conversa.Id, default))!.PodeAbrirNegociacao);

        // Anonimizado: NAO pode — a API recusaria, e o botao nao deve aparecer.
        await db.Contatos.IgnoreQueryFilters().Where(x => x.Id == c.Contato.Id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.AnonimizadoEm, DateTime.UtcNow)
                .SetProperty(x => x.Nome, "Contato anonimizado")
                .SetProperty(x => x.Telefone, $"ANON-{c.Contato.Id}"));
        db.ChangeTracker.Clear();

        var anonimo = (await servico.ConversaAsync(c.Conversa.Id, default))!;
        Assert.False(anonimo.PodeAbrirNegociacao);

        // E o seletor vem VAZIO — o botao e as opcoes caem juntos, porque o booleano e derivado
        // da lista. Com o funil livre na lista, o seletor ofereceria o que a API recusa.
        Assert.Empty(anonimo.FunisDisponiveis);
    }

    /// <summary>⚠️ A CAIXA NAO OFERECIA ABRIR NEGOCIACAO PARA QUEM JA TINHA UMA EM OUTRO FUNIL.
    ///
    /// Relatado assim: "ainda nao consigo adicionar Ysia a outro funil que ela nao esteja".
    ///
    /// A regra virou "uma aberta por FUNIL", a tela do contato acompanhou, e a caixa ficou para
    /// tras com `!Any(Aberta)` — a pergunta por CONTATO. Quem tinha uma aberta em Pos-venda nao
    /// via a faixa NA CAIXA, mesmo com Vendas e Teste livres. E a caixa e onde o vendedor
    /// trabalha.</summary>
    [Fact]
    public async Task A_CAIXA_OFERECE_ABRIR_ENQUANTO_SOBRAR_FUNIL_LIVRE()
    {
        var (db, tx, ctx) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var c = await Semeador.TenantAsync(db, "caixa-outro-funil");
        ctx.EmpresaId = c.Id;
        ctx.UsuarioId = c.Dono.Id;

        var servico = new ServicoCaixa(db, ctx);

        // O cenario tem UM funil, e o contato ja tem aberta nele: nao sobra livre.
        Assert.False((await servico.ConversaAsync(c.Conversa.Id, default))!.PodeAbrirNegociacao);

        // Nasce um segundo funil — e agora sobra.
        var outra = new Pipeline { EmpresaId = c.Id, Nome = "Pós-venda", Ordem = 2 };
        db.Pipelines.Add(outra);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var depois = (await servico.ConversaAsync(c.Conversa.Id, default))!;

        Assert.True(depois.PodeAbrirNegociacao);

        // E o seletor recebe PRONTO o que oferecer: so o funil novo, com o nome — a tela nao
        // subtrai mais nada da lista do menu.
        Assert.Equal([new FunilLivre(outra.Id, "Pós-venda")], depois.FunisDisponiveis);
    }

    /// <summary>⚠️ O BOTAO "REGISTRAR VENDA" ERRAVA NAS DUAS PONTAS.
    ///
    /// A tela decidia por `!ContatoGanhou` — "nunca ganhou" —, que nao e a mesma pergunta que
    /// "tem negocio para fechar". Resultado:
    ///   · MOSTRAVA para o lead sem negocio nenhum, e o clique levava "este contato nao tem
    ///     negocio em aberto" (novo desde o E6);
    ///   · ESCONDIA do cliente recorrente com negocio ABERTO — venda pronta para fechar, sem
    ///     botao. Esse errava desde o E4c/2, quando as duas linhas passaram a coexistir.</summary>
    [Fact]
    public async Task REGISTRAR_VENDA_SO_APARECE_COM_NEGOCIO_ABERTO()
    {
        var (db, tx, ctx) = await PrepararAsync();
        using var _1 = db; using var _2 = tx;

        var c = await Semeador.TenantAsync(db, "pode-vender");
        ctx.EmpresaId = c.Id;
        ctx.UsuarioId = c.Dono.Id;

        var servico = new ServicoCaixa(db, ctx);

        // 1. Com negocio aberto: pode.
        Assert.True((await servico.ConversaAsync(c.Conversa.Id, default))!.PodeRegistrarVenda);

        // 2. Sem negocio nenhum (o lead da caixa): NAO pode — a API recusaria.
        await db.Negociacoes.IgnoreQueryFilters()
            .Where(n => n.ContatoId == c.Contato.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        Assert.False((await servico.ConversaAsync(c.Conversa.Id, default))!.PodeRegistrarVenda);

        // 3. JA GANHOU E abriu outra: PODE — e era aqui que a regra antiga escondia o botao.
        //
        // ⚠️ A GANHA VAI PARA UM SEGUNDO FUNIL. As duas nasciam no funil do cenario, e
        // `uq_negociacoes_card_por_funil` passou a recusar — um card por pessoa por funil. O
        // estado que o teste precisa ("ja comprou, e tem outra para fechar") e o do cliente
        // recorrente, e ele continua existindo: em funis diferentes.
        var outro = new Pipeline { EmpresaId = c.Id, Nome = "Atacado", Ordem = 2 };
        db.Pipelines.Add(outro);
        await db.SaveChangesAsync();

        var vendido = new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = outro.Id, Nome = "Vendido", Ordem = 1, EGanho = true
        };
        db.EtapasFunil.Add(vendido);
        await db.SaveChangesAsync();

        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = c.Id, ContatoId = c.Contato.Id, PipelineId = outro.Id,
            EtapaId = vendido.Id, OrdemKanban = 1m,
            Valor = 500m, Status = StatusNegociacao.Ganha, GanhaEm = DateTime.UtcNow.AddMonths(-3)
        });
        // ⚠️ POR ID, e nao pela navegacao: `c.Contato` veio do semeador e esta DESTACADO aqui.
        // `Semeador.Negocio` liga por navegacao — util quando os dois nascem juntos — e neste
        // contexto faria o EF tentar INSERIR o contato de novo, batendo em "identity always".
        db.Negociacoes.Add(new Negociacao
        {
            EmpresaId = c.Id, ContatoId = c.Contato.Id, PipelineId = c.Pipeline.Id,
            EtapaId = c.PrimeiraEtapa.Id, OrdemKanban = 2000m, Status = StatusNegociacao.Aberta
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var recorrente = (await servico.ConversaAsync(c.Conversa.Id, default))!;
        Assert.True(recorrente.ContatoGanhou);          // ja comprou
        Assert.True(recorrente.PodeRegistrarVenda);     // e tem outra para fechar
        // Os DOIS funis estao ocupados — um pela aberta, outro pela ganha —, entao nao sobra
        // onde abrir. E a `ganha` conta: sem ela, Atacado apareceria livre abaixo.
        Assert.False(recorrente.PodeAbrirNegociacao);
        Assert.Empty(recorrente.FunisDisponiveis);
    }

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, ContextoMutavel Ctx)>
        PrepararAsync()
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();
        return (db, tx, ctx);
    }
}
