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

        var lista = await servico.ConversasAsync(FiltroConversa.Todas, null, null, null, 30, default);
        var naLista = lista.Itens.Single(x => x.Id == c.Conversa.Id);
        var porId = await servico.ConversaAsync(c.Conversa.Id, default);

        Assert.NotNull(porId);
        Assert.Equal(naLista, porId);   // record: compara campo a campo
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
            Telefone = "5500900009999",
            EtapaId = c.Contato.EtapaId,
            OrdemKanban = 9_000m
        };
        db.Contatos.Add(outroContato);
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
        var primeira = await servico.ConversasAsync(FiltroConversa.Todas, null, null, null, 1, default);
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

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, ContextoMutavel Ctx)>
        PrepararAsync()
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();
        return (db, tx, ctx);
    }
}
