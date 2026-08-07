using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>AUD-1 — a trilha contra Postgres real.
///
/// ===================== O QUE ESTES TESTES PROTEGEM =====================
/// A trilha só vale se for COMPLETA e VERDADEIRA. Os dois modos de falha são silenciosos:
///
///   • FALTAR — um serviço deixa de declarar, e a ação some do histórico sem erro nenhum;
///   • MENTIR — o interceptor adivinha a ação, ou PII sobrevive à anonimização.
///
/// O segundo é pior: trilha que mente parece confiável.
/// =======================================================================</summary>
[Collection("banco")]
public class TrilhaDbTests(BancoTeste banco)
{
    // ==================================================================== o diff
    [Fact]
    public async Task Editar_contato_grava_UM_evento_com_TODOS_os_campos_alterados()
    {
        // Um clique é um fato. Seis linhas soltas — uma por coluna — deixariam a linha do tempo
        // ilegível e obrigariam quem lê a remontar na cabeça o que foi uma ação só.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-editar");
        using var _ = db; using var __ = tx;

        var id = await amb.Contatos.CriarAsync(
            new NovoContato("Nome Antigo", "(84) 98111-0001", Email: "antigo@exemplo.com"), default);
        db.ChangeTracker.Clear();

        await amb.Contatos.AtualizarAsync(id, new EditarContato(
            "Nome Novo", "(84) 98111-0001", Email: "novo@exemplo.com",
            ResponsavelId: amb.Cenario.Dono.Id, Valor: 2500m), default);

        var eventos = await EventosAsync(db, EntidadeAuditada.Contato, id);
        var edicao = Assert.Single(eventos, e => e.Acao == AcaoAuditoria.Editou);

        var j = JsonDocument.Parse(edicao.Alteracoes).RootElement;

        Assert.Equal("Nome Antigo", j.GetProperty("nome").GetProperty("antes").GetString());
        Assert.Equal("Nome Novo", j.GetProperty("nome").GetProperty("depois").GetString());
        Assert.Equal("antigo@exemplo.com", j.GetProperty("email").GetProperty("antes").GetString());
        Assert.Equal(2500m, j.GetProperty("valor").GetProperty("depois").GetDecimal());

        // O telefone NÃO mudou — campo intocado não entra no diff, senão todo evento traria a
        // linha inteira e o que mudou de verdade se perderia no meio.
        Assert.False(j.TryGetProperty("telefone", out var _tel));

        // E `atualizado_em` também não: muda em toda escrita e não informa nada que a própria
        // linha da trilha já não diga.
        Assert.False(j.TryGetProperty("atualizadoEm", out var _at));
    }

    [Fact]
    public async Task Mover_grava_os_NOMES_das_etapas_e_nao_os_ids()
    {
        // "etapa_id: 4 → 3" não diz nada a quem lê. E resolver os ids na tela seria pior: etapa
        // renomeada ou excluída faria o histórico mudar de texto sozinho.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-mover");
        using var _ = db; using var __ = tx;

        var id = await amb.Contatos.CriarAsync(new NovoContato("Cliente", "(84) 98111-0002"), default);
        db.ChangeTracker.Clear();

        var origem = amb.Cenario.Etapas[0];
        var destino = amb.Cenario.Etapas[1];
        await amb.Funil.MoverAsync(id, new MoverContato(destino.Id, null, null), default);

        var eventos = await EventosAsync(db, EntidadeAuditada.Contato, id);
        var mover = Assert.Single(eventos, e => e.Acao == AcaoAuditoria.Moveu);

        var etapa = JsonDocument.Parse(mover.Alteracoes).RootElement.GetProperty("etapa");
        Assert.Equal(origem.Nome, etapa.GetProperty("antes").GetString());
        Assert.Equal(destino.Nome, etapa.GetProperty("depois").GetString());
    }

    [Fact]
    public async Task Ganhar_cancelar_e_reabrir_geram_eventos_DISTINTOS()
    {
        // O interceptor vê `ganho_em` indo de NULL para uma data e não sabe se foi venda,
        // migração ou correção do suporte. Quem sabe é o serviço — este teste fixa que ele
        // declara, e declara a ação certa.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-ciclo");
        using var _ = db; using var __ = tx;

        var id = await amb.Contatos.CriarAsync(new NovoContato("Cliente", "(84) 98111-0003"), default);
        db.ChangeTracker.Clear();

        await amb.Contatos.MarcarGanhoAsync(id, 1500m, default);
        db.ChangeTracker.Clear();
        await amb.Contatos.ReabrirAsync(id, default);
        db.ChangeTracker.Clear();

        var doContato = (await EventosAsync(db, EntidadeAuditada.Contato, id))
            .Select(e => e.Acao).ToList();

        Assert.Contains(AcaoAuditoria.Criou, doContato);
        Assert.Contains(AcaoAuditoria.Ganhou, doContato);
        Assert.Contains(AcaoAuditoria.Reabriu, doContato);

        // A venda tem trilha PRÓPRIA: "quem fechou" é pergunta sobre a venda, não sobre o contato.
        var venda = await db.Vendas.AsNoTracking().SingleAsync(v => v.ContatoId == id);
        Assert.Contains(AcaoAuditoria.Criou, (await EventosAsync(db, EntidadeAuditada.Venda, venda.Id))
            .Select(e => e.Acao));

        await amb.Vendas.CancelarAsync(venda.Id, default);
        db.ChangeTracker.Clear();

        var cancelamento = Assert.Single(
            await EventosAsync(db, EntidadeAuditada.Venda, venda.Id), e => e.Acao == AcaoAuditoria.Cancelou);

        // O VALOR desfeito entra explicitamente: o diff sozinho traria só `canceladaEm`.
        Assert.Equal(1500m, JsonDocument.Parse(cancelamento.Alteracoes).RootElement
            .GetProperty("valor").GetProperty("antes").GetDecimal());
    }

    // ==================================================================== o ator
    [Fact]
    public async Task Acao_SEM_SESSAO_grava_ator_sistema_e_usuario_NULL()
    {
        // O MotorFollowUp roda sem usuário. Forçar um id aqui produziria AUTORIA FALSA — alguém
        // apareceria como autor de uma ação que não tomou, e a trilha existe para isso não
        // acontecer.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-sistema");
        using var _ = db; using var __ = tx;

        var id = await amb.Contatos.CriarAsync(new NovoContato("Cliente", "(84) 98111-0004"), default);
        db.ChangeTracker.Clear();

        // Job: sem usuário no contexto, exatamente como o motor roda.
        amb.Contexto.UsuarioId = 0;
        await amb.Contatos.MarcarGanhoAsync(id, 300m, default);

        var evento = Assert.Single(
            await EventosAsync(db, EntidadeAuditada.Contato, id), e => e.Acao == AcaoAuditoria.Ganhou);

        Assert.Equal(AtorAuditoria.Sistema, evento.Ator);
        Assert.Null(evento.UsuarioId);
    }

    // ==================================================================== LGPD
    [Fact]
    public async Task ANONIMIZAR_REMOVE_A_PII_DA_TRILHA_E_PRESERVA_OS_EVENTOS()
    {
        // Se o nome antigo sobrevivesse aqui, a anonimização não teria acontecido: o dado
        // pessoal continuaria no banco, só teria mudado de tabela.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-lgpd");
        using var _ = db; using var __ = tx;

        const string nomeAntigo = "Joaquim Pessoa Real";
        var id = await amb.Contatos.CriarAsync(
            new NovoContato(nomeAntigo, "(84) 98111-0005", Email: "joaquim@exemplo.com"), default);
        db.ChangeTracker.Clear();

        await amb.Contatos.AtualizarAsync(id, new EditarContato(
            "Joaquim P. Real", "(84) 98111-0005", Email: "outro@exemplo.com",
            ResponsavelId: null, Valor: 100m), default);
        db.ChangeTracker.Clear();

        var antes = await EventosAsync(db, EntidadeAuditada.Contato, id);
        Assert.Contains(antes, e => e.Alteracoes.Contains(nomeAntigo));   // estava lá

        await amb.Contatos.AnonimizarAsync(id, default);
        db.ChangeTracker.Clear();

        var depois = await EventosAsync(db, EntidadeAuditada.Contato, id);

        // 1. OS EVENTOS FICAM — a trilha continua provando que houve edição e anonimização.
        Assert.Contains(depois, e => e.Acao == AcaoAuditoria.Criou);
        Assert.Contains(depois, e => e.Acao == AcaoAuditoria.Editou);
        Assert.Contains(depois, e => e.Acao == AcaoAuditoria.Anonimizou);

        // 2. O DADO SAI — em nenhuma linha, de nenhum campo.
        Assert.All(depois, e =>
        {
            Assert.DoesNotContain(nomeAntigo, e.Alteracoes);
            Assert.DoesNotContain("joaquim@exemplo.com", e.Alteracoes);
            Assert.DoesNotContain("5584981110005", e.Alteracoes);
        });

        // 3. O que NÃO é PII permanece legível: mascarar tudo destruiria a utilidade da trilha.
        var edicao = Assert.Single(depois, e => e.Acao == AcaoAuditoria.Editou);
        var j = JsonDocument.Parse(edicao.Alteracoes).RootElement;
        Assert.Equal(100m, j.GetProperty("valor").GetProperty("depois").GetDecimal());
        Assert.Equal(Auditoria.Mascarado, j.GetProperty("nome").GetProperty("antes").GetString());
    }

    [Fact]
    public async Task Buscar_o_nome_antigo_de_contato_anonimizado_na_trilha_NAO_RETORNA_NADA()
    {
        // A mesma garantia, feita como o encarregado de dados faria: uma varredura por texto na
        // tabela inteira, sem saber onde procurar.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-busca");
        using var _ = db; using var __ = tx;

        const string nome = "Maria Aparecida Testemunha";
        var id = await amb.Contatos.CriarAsync(new NovoContato(nome, "(84) 98111-0006"), default);
        db.ChangeTracker.Clear();

        await amb.Contatos.AnonimizarAsync(id, default);
        db.ChangeTracker.Clear();

        // SQL cru, e de propósito: `Contains` sobre coluna `jsonb` não traduz (o Postgres não
        // tem `jsonb ~~ text`). O `::text LIKE` é a varredura CEGA — a mesma que o encarregado de
        // dados faria para responder a um pedido de titular, sem saber em que chave procurar.
        var ocorrencias = await db.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*)::int AS "Value"
              FROM auditoria
             WHERE empresa_id = {0} AND alteracoes::text LIKE {1}
            """, amb.Cenario.Id, $"%{nome}%").SingleAsync();

        Assert.Equal(0, ocorrencias);
    }

    // ==================================================================== o que NÃO é auditado
    [Fact]
    public async Task Conversa_NAO_gera_evento_a_cada_mensagem_recebida()
    {
        // `conversas` é escrita a cada mensagem (`aguardando_desde`, `nao_lidas`,
        // `ultima_mensagem_*`). Auditar isso geraria mais linha de trilha que de mensagem —
        // e afogaria os eventos que importam.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-conversa");
        using var _ = db; using var __ = tx;

        var conversa = amb.Cenario.Conversa;
        var antes = await db.Auditoria.CountAsync(a => a.Entidade == EntidadeAuditada.Conversa);

        for (var i = 0; i < 3; i++)
        {
            await db.Conversas.Where(c => c.Id == conversa.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.NaoLidas, c => c.NaoLidas + 1));
        }

        var conversaRastreada = await db.Conversas.SingleAsync(c => c.Id == conversa.Id);
        conversaRastreada.UltimaMensagemPrevia = "chegou outra";
        await db.SaveChangesAsync();

        Assert.Equal(antes, await db.Auditoria.CountAsync(a => a.Entidade == EntidadeAuditada.Conversa));
    }

    [Fact]
    public async Task Assumir_a_conversa_GERA_evento()
    {
        // O contrapeso do teste acima: assumir é DECISÃO HUMANA, e "quem pegou esse atendimento"
        // é exatamente o que a trilha existe para responder.
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-assumir");
        using var _ = db; using var __ = tx;

        // O Semeador já deixa a conversa com o dono — e `AssumirAsync` é no-op quando já é dele.
        // Liberar por SQL (sem trilha) deixa o fixture no estado que o teste quer medir.
        await db.Conversas.Where(c => c.Id == amb.Cenario.Conversa.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, (long?)null));
        db.ChangeTracker.Clear();

        var conversas = new ServicoConversas(
            db, amb.Contexto, null!, amb.Trilha, TimeProvider.System);

        await conversas.AssumirAsync(amb.Cenario.Conversa.Id, default);

        var evento = Assert.Single(
            await EventosAsync(db, EntidadeAuditada.Conversa, amb.Cenario.Conversa.Id));
        Assert.Equal(AcaoAuditoria.Atribuiu, evento.Acao);
        Assert.Equal(amb.Cenario.Dono.Id, evento.UsuarioId);
    }

    // ==================================================================== acesso e isolamento
    [Fact]
    public async Task VENDEDOR_nao_acessa_a_trilha_nem_pela_API_direta()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-papel");
        using var _ = db; using var __ = tx;

        var servico = new ServicoTrilha(db, amb.Contexto);

        amb.Contexto.Papel = "vendedor";
        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => servico.DoRegistroAsync(EntidadeAuditada.Contato, amb.Cenario.Contato.Id, 50, default));

        // Gestor PODE — senão o teste passaria com uma regra que recusa todo mundo.
        amb.Contexto.Papel = "gestor";
        await servico.DoRegistroAsync(EntidadeAuditada.Contato, amb.Cenario.Contato.Id, 50, default);
    }

    [Fact]
    public async Task O_query_filter_isola_a_trilha_entre_tenants()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-iso");
        using var _ = db; using var __ = tx;

        var alheia = await Semeador.TenantAsync(db, "aud-iso-vizinha");
        db.ChangeTracker.Clear();

        db.Auditoria.Add(new Auditoria
        {
            EmpresaId = alheia.Id, Entidade = EntidadeAuditada.Contato,
            EntidadeId = alheia.Contato.Id, Acao = AcaoAuditoria.Editou,
            Alteracoes = """{"nome":{"antes":"Segredo Alheio","depois":"x"}}""",
            Ator = AtorAuditoria.Usuario, Quando = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.All(await db.Auditoria.AsNoTracking().ToListAsync(),
            a => Assert.Equal(amb.Cenario.Id, a.EmpresaId));

        // E o serviço não devolve a linha da vizinha nem perguntando pelo id dela.
        var servico = new ServicoTrilha(db, amb.Contexto);
        Assert.Empty(await servico.DoRegistroAsync(
            EntidadeAuditada.Contato, alheia.Contato.Id, 50, default));
    }

    // ==================================================================== retenção
    [Fact]
    public async Task O_expurgo_remove_o_que_passou_da_retencao_e_preserva_o_resto()
    {
        var (db, tx, amb) = await ContatosDbTests.PrepararAsync(banco, "aud-expurgo");
        using var _ = db; using var __ = tx;

        var agora = DateTime.UtcNow;
        db.Auditoria.AddRange(
            Evento(amb.Cenario.Id, agora.AddDays(-10)),                                  // fica
            Evento(amb.Cenario.Id, agora - ExpurgoTrilha.Retencao + TimeSpan.FromDays(1)), // fica
            Evento(amb.Cenario.Id, agora - ExpurgoTrilha.Retencao - TimeSpan.FromDays(1)), // sai
            Evento(amb.Cenario.Id, agora.AddYears(-3)));                                   // sai
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var antes = await db.Auditoria.CountAsync();
        var apagadas = await ExpurgoTrilha.ExpurgarAsync(db, TimeProvider.System, default);

        Assert.Equal(2, apagadas);
        Assert.Equal(antes - 2, await db.Auditoria.CountAsync());
        Assert.All(await db.Auditoria.AsNoTracking().ToListAsync(),
            a => Assert.True(a.Quando >= agora - ExpurgoTrilha.Retencao));
    }

    // ==================================================================== apoio
    private static Auditoria Evento(long empresaId, DateTime quando) => new()
    {
        EmpresaId = empresaId, Entidade = EntidadeAuditada.Contato, EntidadeId = 1,
        Acao = AcaoAuditoria.Editou, Alteracoes = "{}",
        Ator = AtorAuditoria.Sistema, Quando = quando
    };

    private static async Task<List<Auditoria>> EventosAsync(
        NexoraDbContext db, EntidadeAuditada entidade, long id)
    {
        db.ChangeTracker.Clear();
        return await db.Auditoria.AsNoTracking()
            .Where(a => a.Entidade == entidade && a.EntidadeId == id)
            .OrderBy(a => a.Id)
            .ToListAsync();
    }
}
