using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>DIÁLOGOS DE MENTIRA nas conversas existentes.
///
/// ===================== O QUE PODE DAR ERRADO AQUI =====================
/// Semeador de dado falso raramente ganha teste, e este merece por um motivo específico: ele
/// REESCREVE conversas que a semeadura anterior distribuiu com cuidado pelas faixas do semáforo.
/// Se ele mexer em `ultima_mensagem_em` ou em `aguardando_desde`, o cenário inteiro de
/// desenvolvimento muda de uma vez — e ninguém liga o dashboard estranho ao semeador de conversa.
///
/// O outro risco é a IDEMPOTÊNCIA: rodar duas vezes empilhando dois diálogos dá uma thread em que
/// a mesma pessoa diz "boa tarde" no meio do assunto. Parece dado ruim de produção, não bug de
/// ferramenta.
/// ======================================================================</summary>
[Collection("banco")]
public class SementeConversasDbTests(BancoTeste banco)
{
    [Fact]
    public async Task A_THREAD_VIRA_UM_DIALOGO_ALTERNADO_E_COERENTE()
    {
        var (db, tx, amb) = await PrepararAsync("dialogo");
        using var _ = db; using var __ = tx;

        var resumo = await amb.Semeador.SemearAsync(10, default);

        Assert.True(resumo.Conversas > 0);
        Assert.True(resumo.MensagensCriadas >= resumo.Conversas * 6,
            "cada conversa deveria ganhar um diálogo, não duas ou três mensagens");

        db.ChangeTracker.Clear();
        var mensagens = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.EmpresaId == amb.Cenario.Id)
            .OrderBy(m => m.ConversaId).ThenBy(m => m.Id)
            .ToListAsync();

        foreach (var thread in mensagens.GroupBy(m => m.ConversaId))
        {
            var linha = thread.ToList();

            // ===== O CLIENTE FALA PRIMEIRO =====
            // No Nexora toda conversa nasce de uma mensagem que CHEGOU. Não existe caminho em que
            // a empresa abre a conversa — nem o formulário do site manda WhatsApp.
            Assert.Equal(DirecaoMensagem.Entrada, linha[0].Direcao);

            // Alternado do começo ao fim: é o que separa diálogo de lista de frases.
            for (var i = 1; i < linha.Count; i++)
                Assert.NotEqual(linha[i - 1].Direcao, linha[i].Direcao);

            // A ORDEM DE INSERÇÃO é a ordem do tempo. A thread da caixa ordena por `id`, não por
            // data — inserir fora de ordem daria um diálogo embaralhado com timestamps certos.
            var instantes = linha.Select(m => m.RecebidaEm ?? m.EnviadaEm ?? m.ReservadoEm).ToList();
            Assert.Equal(instantes.OrderBy(x => x).ToList(), instantes);

            Assert.All(linha, m => Assert.False(string.IsNullOrWhiteSpace(m.Texto)));
        }
    }

    [Fact]
    public async Task NAO_MEXE_NO_QUE_DEFINE_O_SEMAFORO()
    {
        // ===== O CENÁRIO É DE OUTRO SEMEADOR =====
        // `ultima_mensagem_em` põe a conversa numa faixa do semáforo; `aguardando_desde` decide se
        // ela entra no Meu Dia e no "aguardando resposta" do dashboard. Este semeador escolhe o
        // roteiro para CASAR com esse estado, e não o contrário.
        var (db, tx, amb) = await PrepararAsync("semaforo");
        using var _ = db; using var __ = tx;

        var antes = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EmpresaId == amb.Cenario.Id)
            .Select(c => new { c.Id, c.UltimaMensagemEm, c.AguardandoDesde, c.Status })
            .ToListAsync();
        db.ChangeTracker.Clear();

        await amb.Semeador.SemearAsync(50, default);
        db.ChangeTracker.Clear();

        var depois = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EmpresaId == amb.Cenario.Id)
            .Select(c => new { c.Id, c.UltimaMensagemEm, c.AguardandoDesde, c.Status })
            .ToListAsync();

        Assert.Equal(
            antes.OrderBy(x => x.Id).Select(x => (x.Id, x.UltimaMensagemEm, x.AguardandoDesde, x.Status)),
            depois.OrderBy(x => x.Id).Select(x => (x.Id, x.UltimaMensagemEm, x.AguardandoDesde, x.Status)));
    }

    [Fact]
    public async Task A_ULTIMA_MENSAGEM_TERMINA_NA_DIRECAO_QUE_A_CONVERSA_JA_TINHA()
    {
        // Conversa com `aguardando_desde` preenchido = o cliente falou por último. Se o roteiro
        // terminasse com a empresa falando, o semáforo estaria contando espera de uma conversa
        // que já foi respondida.
        var (db, tx, amb) = await PrepararAsync("direcao");
        using var _ = db; using var __ = tx;

        await amb.Semeador.SemearAsync(50, default);
        db.ChangeTracker.Clear();

        var conversas = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EmpresaId == amb.Cenario.Id).ToListAsync();

        foreach (var c in conversas)
        {
            var ultima = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.ConversaId == c.Id).OrderByDescending(m => m.Id).FirstOrDefaultAsync();

            if (ultima is null) continue;

            var esperando = c.AguardandoDesde is not null;
            Assert.Equal(
                esperando ? DirecaoMensagem.Entrada : DirecaoMensagem.Saida,
                ultima.Direcao);

            // A prévia da lista tem que ser o texto do último balão — senão a caixa mostra um
            // resumo que não existe dentro da conversa.
            Assert.Equal(ultima.Texto, c.UltimaMensagemPrevia);

            // Não lidas = quantas entradas vieram desde a última resposta nossa. Zero quando a
            // bola está com o cliente.
            if (!esperando) Assert.Equal(0, c.NaoLidas);
            else Assert.True(c.NaoLidas > 0);
        }
    }

    [Fact]
    public async Task E_IDEMPOTENTE()
    {
        var (db, tx, amb) = await PrepararAsync("idempotente");
        using var _ = db; using var __ = tx;

        var primeira = await amb.Semeador.SemearAsync(20, default);
        db.ChangeTracker.Clear();
        var textosPrimeira = await TextosAsync(db, amb);

        var segunda = await amb.Semeador.SemearAsync(20, default);
        db.ChangeTracker.Clear();
        var textosSegunda = await TextosAsync(db, amb);

        Assert.Equal(primeira.MensagensCriadas, segunda.MensagensCriadas);
        // Determinístico: o roteiro e os intervalos saem do id da conversa, então a segunda
        // execução produz a MESMA thread. Semeadura que muda a cada rodada torna impossível
        // conferir o que mudou.
        Assert.Equal(textosPrimeira, textosSegunda);
    }

    [Fact]
    public async Task AS_MENSAGENS_CAEM_DENTRO_DO_EXPEDIENTE()
    {
        // ===== POR QUE ISSO IMPORTA, E NÃO É ESTÉTICA =====
        // O semáforo mede espera em minutos ÚTEIS. Uma thread que acontece de madrugada tem "10
        // horas de espera" que valem zero na conta — o cenário passaria a exercitar exatamente o
        // caso que não interessa.
        var (db, tx, amb) = await PrepararAsync("expediente");
        using var _ = db; using var __ = tx;

        var empresa = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == amb.Cenario.Id);
        var janela = new JanelaAtendimento(
            empresa.JanelaHoraInicio, empresa.JanelaHoraFim, empresa.JanelaDiasSemana);
        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);

        await amb.Semeador.SemearAsync(30, default);
        db.ChangeTracker.Clear();

        var mensagens = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.EmpresaId == amb.Cenario.Id)
            .Select(m => new { m.Id, m.ConversaId, Quando = m.RecebidaEm ?? m.EnviadaEm ?? m.ReservadoEm })
            .ToListAsync();

        // A ÚLTIMA de cada conversa fica de fora: ela cai exatamente em `ultima_mensagem_em`, que
        // o outro semeador escolheu e este preserva de propósito — inclusive quando cai fora do
        // expediente, que é o caso que faz o semáforo pausar.
        var ultimas = mensagens.GroupBy(m => m.ConversaId).Select(g => g.Max(m => m.Id)).ToHashSet();
        var vazio = new HashSet<DateOnly>();

        var foraDoExpediente = mensagens
            .Where(m => !ultimas.Contains(m.Id))
            .Count(m => !janela.Contem(TimeZoneInfo.ConvertTimeFromUtc(m.Quando, fuso), vazio));

        Assert.Equal(0, foraDoExpediente);
    }

    [Fact]
    public async Task Ha_exemplo_de_nao_entregue_e_de_expirada()
    {
        // Os dois estados terminais do envio existem no desenho da thread (tick de erro, aviso de
        // "não entregue"). Sem um exemplo na base, esses caminhos nunca são vistos.
        var (db, tx, amb) = await PrepararAsync("falhas");
        using var _ = db; using var __ = tx;

        var resumo = await amb.Semeador.SemearAsync(50, default);
        db.ChangeTracker.Clear();

        Assert.True(resumo.NaoEntregues > 0, "nenhuma mensagem falha foi semeada");
        Assert.True(resumo.Expiradas > 0, "nenhuma mensagem expirada foi semeada");

        // E NUNCA na última: uma falha ali faria a caixa mostrar erro como se fosse o assunto.
        var conversas = await db.Conversas.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EmpresaId == amb.Cenario.Id).Select(c => c.Id).ToListAsync();

        foreach (var id in conversas)
        {
            var ultima = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.ConversaId == id).OrderByDescending(m => m.Id).FirstOrDefaultAsync();
            if (ultima is null) continue;

            Assert.Null(ultima.Erro);
            Assert.Null(ultima.ExpiradaEm);
        }
    }

    [Fact]
    public async Task CONVERSA_DE_OUTRA_EMPRESA_NAO_E_TOCADA()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var minha = await Semeador.TenantAsync(db, "sem-conv-a");
        var outra = await Semeador.TenantAsync(db, "sem-conv-b");

        var antesDaOutra = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.EmpresaId == outra.Id).Select(m => m.Id).ToListAsync();
        db.ChangeTracker.Clear();

        ctx.EmpresaId = minha.Id;
        ctx.UsuarioId = minha.Dono.Id;
        ctx.Papel = "dono";

        await new ServicoSementeConversas(db).SemearAsync(50, default);
        db.ChangeTracker.Clear();

        var depoisDaOutra = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.EmpresaId == outra.Id).Select(m => m.Id).ToListAsync();

        Assert.Equal(antesDaOutra, depoisDaOutra);
    }

    [Fact]
    public async Task Sem_conversa_nenhuma_devolve_zero_sem_estourar()
    {
        var (db, tx, amb) = await PrepararAsync("vazio");
        using var _ = db; using var __ = tx;

        await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.EmpresaId == amb.Cenario.Id).ExecuteDeleteAsync();
        await db.Conversas.IgnoreQueryFilters()
            .Where(c => c.EmpresaId == amb.Cenario.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        var resumo = await amb.Semeador.SemearAsync(60, default);
        Assert.Equal(0, resumo.Conversas);
        Assert.Equal(0, resumo.MensagensCriadas);
    }

    // ==================================================================== apoio
    private sealed record Ambiente(Cenario Cenario, IServicoSementeConversas Semeador);

    private static async Task<List<string?>> TextosAsync(NexoraDbContext db, Ambiente amb) =>
        await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.EmpresaId == amb.Cenario.Id)
            .OrderBy(m => m.ConversaId).ThenBy(m => m.Id)
            .Select(m => m.Texto)
            .ToListAsync();

    /// <summary>Um tenant com VÁRIAS conversas — o semeador escolhe as mais recentes, e com uma
    /// só nada do que este arquivo testa apareceria.</summary>
    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"sem-conv-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        await CriarConversasAsync(db, cenario, 24);

        return (db, tx, new Ambiente(cenario, new ServicoSementeConversas(db)));
    }

    /// <summary>Cria conversas espalhadas no tempo, metade esperando resposta e metade não — que
    /// é a distribuição que o semeador precisa casar.</summary>
    private static async Task CriarConversasAsync(NexoraDbContext db, Cenario cenario, int quantas)
    {
        var agora = DateTime.UtcNow;
        var etapa = cenario.PrimeiraEtapa.Id;

        for (var i = 0; i < quantas; i++)
        {
            var contato = new Contato
            {
                EmpresaId = cenario.Id,
                Nome = $"Cliente {i}",
                Telefone = $"5584{900000000 + cenario.Id * 1000 + i}",
                EtapaId = etapa,
                OrdemKanban = 1000m + i
            };
            db.Contatos.Add(contato);
            await db.SaveChangesAsync();

            var esperando = i % 2 == 0;
            // Horas variadas para cair dentro e fora do expediente — inclusive de madrugada, que
            // é o caso que o teste de expediente precisa ver preservado na última mensagem.
            var ultima = agora.AddHours(-(i * 5 + 1));

            db.Conversas.Add(new Conversa
            {
                EmpresaId = cenario.Id,
                ContatoId = contato.Id,
                ConexaoId = cenario.Conexao.Id,
                UltimaMensagemEm = ultima,
                UltimaMensagemDirecao = esperando ? DirecaoMensagem.Entrada : DirecaoMensagem.Saida,
                UltimaMensagemPrevia = "texto antigo",
                AguardandoDesde = esperando ? ultima : null,
                NaoLidas = esperando ? 2 : 0
            });
            await db.SaveChangesAsync();
        }

        db.ChangeTracker.Clear();
    }
}
