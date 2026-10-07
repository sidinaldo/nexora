using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>===================== NPS-1 ETAPA 2b — A TABELA, E O QUE O BANCO GARANTE =====================
///
/// Esta suite nao testa regra de negocio: testa as INVARIANTES que moram no schema. Cada uma delas
/// existe porque a alternativa — confiar que o codigo lembra — ja falhou em algum lugar deste
/// projeto.
///
/// ⚠️ O PRIMEIRO TESTE AQUI GUARDA UM DEFEITO QUE EU INTRODUZI E MEDI. `OnDelete(SetNull)` do EF
/// sobre FK COMPOSTA gera `ON DELETE SET NULL` sem lista de colunas, e o Postgres zera TODAS —
/// inclusive `empresa_id`, que e NOT NULL. Apagar mensagem referenciada estourava a restricao, e o
/// estrago cairia no EXPURGO da rodada diaria, que nem conhece esta tabela.
/// ============================================================================================</summary>
[Collection("banco")]
public class PesquisaNpsDbTests(BancoTeste banco)
{
    private static readonly DateOnly Hoje = new(2026, 8, 6);

    // ==================================================================== as chaves estrangeiras

    /// <summary>===================== APAGAR A MENSAGEM NAO LEVA A PESQUISA =====================
    ///
    /// A nota e o dado que o relatorio precisa, e ela tem de sobreviver ao texto que a trouxe: o
    /// expurgo apaga mensagem de 30 dias, e uma pesquisa respondida em janeiro ainda conta em
    /// marco.
    ///
    /// ⚠️ E `empresa_id` TEM DE FICAR. Era aqui o defeito: `SET NULL` composto zerava o tenant
    /// junto, a restricao de nao-nulo estourava, e o DELETE da mensagem falhava — derrubando o
    /// expurgo inteiro para aquela empresa. O conserto e `SET NULL (coluna)`, escrito a mao na
    /// migration porque o EF nao sabe gerar.
    /// ================================================================================</summary>
    [Fact]
    public async Task APAGAR_A_MENSAGEM_NAO_LEVA_A_PESQUISA_NEM_ZERA_O_TENANT()
    {
        var (db, tx, amb) = await PrepararAsync("setnull");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb, comMensagem: true);

        var msgId = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).Select(x => x.MensagemEnvioId).SingleAsync();

        Assert.NotNull(msgId);

        // O que o expurgo da rodada diaria faz.
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.Id == msgId).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        var depois = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa)
            .Select(x => new { x.MensagemEnvioId, x.EmpresaId, x.Status })
            .SingleAsync();

        Assert.Null(depois.MensagemEnvioId);
        Assert.Equal(amb.Cenario.Id, depois.EmpresaId);
        Assert.Equal(StatusPesquisaNps.Enviada, depois.Status);
    }

    /// <summary>===================== APAGAR A VENDA SOLTA A MENSAGEM =====================
    ///
    /// ⚠️ O MESMO DEFEITO DO TESTE ACIMA, NA FK VIZINHA (revisao NPS-1). `fk_msg_negociacao` e
    /// composta e nasceu com `SET NULL` sem coluna: apagar a venda tentava zerar tambem
    /// `mensagens.empresa_id`, que e NOT NULL, e o DELETE estourava. A migration
    /// `FkMensagemNegociacaoSetNullColuna` troca por `SET NULL (negociacao_id)`. A mensagem e
    /// historico da conversa — fica, sem o vinculo, e com o tenant.
    /// ==========================================================================</summary>
    [Fact]
    public async Task APAGAR_A_VENDA_SOLTA_A_MENSAGEM_SEM_ZERAR_O_TENANT()
    {
        var (db, tx, amb) = await PrepararAsync("setnull-venda");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb, comMensagem: true);

        var ids = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa)
            .Select(x => new { x.MensagemEnvioId, x.NegociacaoId })
            .SingleAsync();

        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.Id == ids.NegociacaoId).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        var mensagem = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Id == ids.MensagemEnvioId)
            .Select(m => new { m.NegociacaoId, m.EmpresaId })
            .SingleAsync();

        Assert.Null(mensagem.NegociacaoId);
        Assert.Equal(amb.Cenario.Id, mensagem.EmpresaId);
    }

    /// <summary>===================== APAGAR A VENDA LEVA A PESQUISA =====================
    ///
    /// `Cascade` e o certo: pesquisa de uma venda que nao existe mais nao responde pergunta
    /// nenhuma, e a nota orfa inflaria o denominador do relatorio.
    ///
    /// ⚠️ ESCREVI ESTE TESTE SOBRE O CONTATO PRIMEIRO, e o banco recusou com 23503:
    /// `fk_negociacoes_contato` e RESTRICT, entao contato COM negociacao nao pode ser apagado — a
    /// negociacao barra antes. O `Cascade` do contato e defensivo e INALCANCAVEL por esse caminho,
    /// e o projeto nem apaga contato: a LGPD aqui e anonimizacao, "sem delete fisico, sem soft
    /// delete".
    ///
    /// ⚠️ E ANONIMIZAR E OUTRA COISA: preserva o historico e apaga so o comentario. Vem na fatia
    /// das acoes.
    /// ==========================================================================</summary>
    [Fact]
    public async Task APAGAR_A_VENDA_LEVA_A_PESQUISA()
    {
        var (db, tx, amb) = await PrepararAsync("cascata");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb);
        var negociacao = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).Select(x => x.NegociacaoId).SingleAsync();

        await db.Negociacoes.IgnoreQueryFilters()
            .Where(n => n.Id == negociacao).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await db.PesquisasNps.IgnoreQueryFilters()
            .Where(x => x.Id == pesquisa).ToListAsync());
    }

    /// <summary>E o contato COM negociacao nao pode ser apagado — `fk_negociacoes_contato` e
    /// RESTRICT. Fica registrado porque foi o que me fez trocar o teste acima: o `Cascade` da
    /// pesquisa para o contato nunca e exercido na pratica.</summary>
    [Fact]
    public async Task CONTATO_COM_VENDA_NAO_PODE_SER_APAGADO()
    {
        var (db, tx, amb) = await PrepararAsync("restrict");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb);
        var contato = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).Select(x => x.ContatoId).SingleAsync();

        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Contatos.IgnoreQueryFilters().Where(c => c.Id == contato).ExecuteDeleteAsync());

        Assert.Contains("fk_negociacoes_contato", erro.Message);
    }

    /// <summary>⚠️ A FK COMPOSTA RECUSA A NEGOCIACAO DA VIZINHA. O filtro global do EF protege a
    /// LEITURA; isto protege a ESCRITA — inclusive a de um job que roda sem tenant, que e
    /// exatamente o caso do agendamento do NPS.</summary>
    [Fact]
    public async Task PESQUISA_NAO_APONTA_PARA_NEGOCIACAO_DE_OUTRA_EMPRESA()
    {
        var (db, tx, amb) = await PrepararAsync("tenant");
        using var _ = db; using var __ = tx;

        var vizinha = await Semeador.TenantAsync(db, "nps-vizinha");
        var cenarioDela = await NegocioAsync(db, vizinha);

        // A pesquisa diz `empresa_id` da MINHA empresa e aponta para a negociacao DELA.
        db.PesquisasNps.Add(new PesquisaNps
        {
            EmpresaId = amb.Cenario.Id,
            NegociacaoId = cenarioDela.NegociacaoId,
            ContatoId = cenarioDela.ContatoId,
            Status = StatusPesquisaNps.Agendada,
            DataAgendada = Hoje,
            DataLimite = Hoje.AddDays(7)
        });

        var erro = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Contains("fk_pesquisas_nps", erro.InnerException!.Message);
    }

    // ==================================================================== as invariantes

    /// <summary>===================== UMA PESQUISA POR VENDA, E O BANCO GARANTE =====================
    ///
    /// ⚠️ E ESTA LINHA QUE TORNA O AGENDAMENTO POR RECONCILIACAO SEGURO. Os dois caminhos de
    /// conclusao de venda sao baseados em CONJUNTO (`ExecuteUpdate` e SQL cru) e nao carregam
    /// entidade onde pendurar um gancho, entao a rodada diaria PROCURA negociacao concluida sem
    /// pesquisa e insere. Duas rodadas sobrepostas — um restart perto da hora — tentariam inserir
    /// as duas, e a segunda bate aqui em vez de o cliente receber a pergunta duas vezes.
    /// ==================================================================================</summary>
    [Fact]
    public async Task UMA_PESQUISA_POR_VENDA_E_A_SEGUNDA_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("unica");
        using var _ = db; using var __ = tx;

        var primeira = await PesquisaAsync(db, amb);

        var negociacao = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == primeira).Select(x => x.NegociacaoId).SingleAsync();
        var contato = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == primeira).Select(x => x.ContatoId).SingleAsync();

        db.PesquisasNps.Add(new PesquisaNps
        {
            EmpresaId = amb.Cenario.Id,
            NegociacaoId = negociacao,
            ContatoId = contato,
            Status = StatusPesquisaNps.Agendada,
            DataAgendada = Hoje,
            DataLimite = Hoje.AddDays(7)
        });

        var erro = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Contains("uq_pesquisas_nps_negociacao", erro.InnerException!.Message);
    }

    /// <summary>===================== RESPONDIDA SEM NOTA NAO EXISTE =====================
    ///
    /// O relatorio conta `status = 'respondida'` e soma as notas. Uma linha respondida sem nota
    /// entraria na taxa de resposta e sumiria da distribuicao — e os dois numeros deixariam de
    /// fechar entre si, que e o tipo de divergencia que ninguem consegue explicar depois.
    /// ========================================================================</summary>
    [Fact]
    public async Task RESPONDIDA_SEM_NOTA_E_RECUSADA()
    {
        var (db, tx, amb) = await PrepararAsync("resp-sem-nota");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb);

        // ⚠️ `PostgresException` E NAO `DbUpdateException`: `ExecuteUpdate` vai direto ao banco,
        // sem passar pelo rastreador, entao o EF nao embrulha o erro. O mesmo vale para
        // `ExecuteDelete`. Nos `SaveChanges` abaixo o embrulho acontece.
        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            db.PesquisasNps.IgnoreQueryFilters().Where(x => x.Id == pesquisa)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.Status, StatusPesquisaNps.Respondida)));

        Assert.Contains("ck_pesquisas_nps_respondida", erro.Message);
    }

    /// <summary>⚠️ MAS `PossivelNota` SEM NOTA E LEGITIMO, e o check nao a inclui de proposito:
    /// ali a nota e SUSPEITA, e pode nem haver uma ainda. O relatorio nunca le `nota IS NOT NULL`
    /// — ele le `status = 'respondida'`, e e essa distincao que impede um numero que ninguem
    /// confirmou de entrar no NPS.</summary>
    [Fact]
    public async Task POSSIVEL_NOTA_SEM_NOTA_E_PERMITIDA()
    {
        var (db, tx, amb) = await PrepararAsync("possivel");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb);

        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.Id == pesquisa)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, StatusPesquisaNps.PossivelNota));

        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.PossivelNota,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == pesquisa).Select(x => x.Status).SingleAsync());
    }

    /// <summary>A escala e FECHADA em 0 a 10, e o check e quem fecha. Onze nao e nota, e o
    /// `LeitorDeNota` ja recusa — isto e a segunda tranca, para o caminho que nao passa por ele
    /// (a confirmacao manual do vendedor).</summary>
    [Theory]
    [InlineData(11)]
    [InlineData(-1)]
    [InlineData(100)]
    public async Task NOTA_FORA_DA_FAIXA_E_RECUSADA(int nota)
    {
        var (db, tx, amb) = await PrepararAsync($"faixa{nota}");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb);

        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            db.PesquisasNps.IgnoreQueryFilters().Where(x => x.Id == pesquisa)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Nota, (short)nota)));

        Assert.Contains("ck_pesquisas_nps_nota", erro.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task AS_BORDAS_DA_ESCALA_SAO_VALIDAS(int nota)
    {
        var (db, tx, amb) = await PrepararAsync($"borda{nota}");
        using var _ = db; using var __ = tx;

        var pesquisa = await PesquisaAsync(db, amb);

        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.Id == pesquisa)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Nota, (short)nota));

        db.ChangeTracker.Clear();
        Assert.Equal((short)nota, await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == pesquisa).Select(x => x.Nota).SingleAsync());
    }

    /// <summary>O limite do adiamento nunca pode ser ANTES do primeiro agendamento — seria uma
    /// pesquisa que nasce ja cancelada, e a rodada a mataria sem nunca ter tentado enviar.</summary>
    [Fact]
    public async Task LIMITE_ANTES_DO_AGENDAMENTO_E_RECUSADO()
    {
        var (db, tx, amb) = await PrepararAsync("limite");
        using var _ = db; using var __ = tx;

        var cenario = await NegocioAsync(db, amb.Cenario);

        db.PesquisasNps.Add(new PesquisaNps
        {
            EmpresaId = amb.Cenario.Id,
            NegociacaoId = cenario.NegociacaoId,
            ContatoId = cenario.ContatoId,
            Status = StatusPesquisaNps.Agendada,
            DataAgendada = Hoje,
            DataLimite = Hoje.AddDays(-1)
        });

        var erro = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Contains("ck_pesquisas_nps_limite", erro.InnerException!.Message);
    }

    // ==================================================================== a configuração

    /// <summary>===================== A PESQUISA NASCE DESLIGADA =====================
    ///
    /// ⚠️ NAO E TIMIDEZ. Ligada por padrao, toda empresa JA EXISTENTE comecaria a mandar mensagem
    /// automatica para os clientes dela no dia do deploy, sem ninguem ter escrito o texto nem
    /// escolhido o prazo. E o texto PADRAO existe no banco, nao so no C#: coluna NOT NULL precisa
    /// de valor para quem ja estava la, e `nps_texto` vazio seria uma pergunta em branco saindo no
    /// WhatsApp do cliente.
    /// ====================================================================</summary>
    [Fact]
    public async Task A_PESQUISA_NASCE_DESLIGADA_E_COM_O_TEXTO_PADRAO()
    {
        var (db, tx, amb) = await PrepararAsync("padroes");
        using var _ = db; using var __ = tx;

        var e = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == amb.Cenario.Id)
            .Select(x => new
            {
                x.NpsAtivo, x.NpsDiasAposConclusao, x.NpsDiasExpiracao, x.NpsTexto,
                x.NpsMensagemPromotor, x.NpsMensagemDetrator
            })
            .SingleAsync();

        Assert.False(e.NpsAtivo);
        Assert.Equal((short)3, e.NpsDiasAposConclusao);
        Assert.Equal((short)3, e.NpsDiasExpiracao);

        // ⚠️ O TEXTO PEDE O NUMERO EXPLICITAMENTE, e isso nao e enfeite: `LeitorDeNota` so
        // reconhece a nota com confianca quando ela vem sozinha, depois de um puxador, ou fechada
        // por pontuacao. Pergunta que convide a prosa produziria `PossivelNota` em serie, e cada
        // uma e trabalho manual para o vendedor.
        // ⚠️ `{{saudacao}}` E NAO `{{nome}}` NO PADRAO: com "Oi, {{nome}}!", o contato cujo nome e
        // um telefone formatado — 4 dos 1250 no `nexora_dev` — receberia "Oi, ! Aqui é da...".
        // `NomeDePessoa.Saudacao` decide a pontuacao junto com o nome. Ver o comentario em
        // `Empresa.NpsTexto`.
        Assert.Contains("{{saudacao}}", e.NpsTexto);
        Assert.DoesNotContain("{{nome}}", e.NpsTexto);
        Assert.Contains("{{empresa}}", e.NpsTexto);
        Assert.Contains("0 a 10", e.NpsTexto);
        Assert.Contains("número", e.NpsTexto);

        // Agradecimento VAZIO = nao envia. Uma segunda mensagem automatica depois da primeira
        // dobra o risco do numero, e nem toda empresa quer.
        Assert.Null(e.NpsMensagemPromotor);
        Assert.Null(e.NpsMensagemDetrator);
    }

    // ==================================================================== o andaime

    private sealed record Ambiente(NexoraDbContext Db, Cenario Cenario, ContextoMutavel Contexto);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"nps-{sufixo}");

        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new Ambiente(db, cenario, ctx));
    }

    /// <summary>Um contato com negociacao CONCLUIDA — a venda que a pesquisa acompanha.</summary>
    private static async Task<(long ContatoId, long NegociacaoId)> NegocioAsync(
        NexoraDbContext db, Cenario cenario)
    {
        var contato = new Contato
        {
            EmpresaId = cenario.Id,
            Nome = "Cliente NPS",
            Telefone = $"5584{Random.Shared.NextInt64(900000000, 999999999)}"
        };
        db.Contatos.Add(contato);

        var negocio = Semeador.Negocio(contato, cenario.Etapas[0]);
        negocio.Status = StatusNegociacao.Concluida;
        negocio.Valor = 1000m;
        negocio.GanhaEm = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        negocio.ConcluidaEm = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        db.Negociacoes.Add(negocio);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return (contato.Id, negocio.Id);
    }

    private static async Task<long> PesquisaAsync(
        NexoraDbContext db, Ambiente amb, bool comMensagem = false)
    {
        var cenario = await NegocioAsync(db, amb.Cenario);

        long? mensagemId = null;

        if (comMensagem)
        {
            var conversa = new Conversa
            {
                EmpresaId = amb.Cenario.Id,
                ContatoId = cenario.ContatoId,
                ConexaoId = amb.Cenario.Conexao.Id,
                UltimaMensagemEm = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc)
            };
            db.Conversas.Add(conversa);
            await db.SaveChangesAsync();

            var mensagem = new Mensagem
            {
                EmpresaId = amb.Cenario.Id,
                ConversaId = conversa.Id,
                ContatoId = cenario.ContatoId,
                ConexaoId = amb.Cenario.Conexao.Id,
                InstanceName = amb.Cenario.Conexao.InstanceName,
                Direcao = DirecaoMensagem.Saida,
                Texto = "De 0 a 10, quanto você recomendaria a gente?",
                // `ck_msg_data_disparo` exige a data em toda SAIDA — e descobri isso aqui, com o
                // 23514. E a invariante do teto diario: saida sem dia de disparo nao tem como ser
                // contada contra o limite.
                DataDisparo = Hoje,
                Origem = OrigemMensagem.Automatica,
                TipoAutomacao = TipoAutomacao.Nps,
                NegociacaoId = cenario.NegociacaoId
            };
            db.Mensagens.Add(mensagem);
            await db.SaveChangesAsync();

            mensagemId = mensagem.Id;
        }

        var pesquisa = new PesquisaNps
        {
            EmpresaId = amb.Cenario.Id,
            NegociacaoId = cenario.NegociacaoId,
            ContatoId = cenario.ContatoId,
            MensagemEnvioId = mensagemId,
            Status = comMensagem ? StatusPesquisaNps.Enviada : StatusPesquisaNps.Agendada,
            DataAgendada = Hoje,
            DataLimite = Hoje.AddDays(7)
        };
        db.PesquisasNps.Add(pesquisa);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return pesquisa.Id;
    }
}
