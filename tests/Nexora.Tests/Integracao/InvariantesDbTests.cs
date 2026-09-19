using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;
using Npgsql;

namespace Nexora.Tests.Integracao;

/// <summary>Cada indice unico PARCIAL carrega uma invariante de negocio. Se o EF nao gerar o
/// WHERE, a invariante some sem aviso — o schema continua "parecendo" certo e o bug so aparece
/// em producao, como mensagem duplicada ou numero banido.
///
/// Cada teste aqui prova as DUAS metades: o que o banco recusa E o que ele tem que aceitar.
/// A segunda metade e a que pega um filtro parcial escrito errado (um WHERE frouxo demais
/// passa no teste de recusa e quebra o caso legitimo).</summary>
[Collection("banco")]
public class InvariantesDbTests(BancoTeste banco)
{
    // ============================================================ uq_msg_wa_id
    [Fact]
    public async Task Mesma_mensagem_do_WhatsApp_nao_entra_duas_vezes()
    {
        // Cobre dois casos de uma vez: webhook REENTREGUE (a Evolution reentrega ate receber
        // 2xx) e ECO do proprio envio (ela devolve por webhook o que acabamos de mandar).
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "wa");

        db.Mensagens.Add(NovaMensagem(c, waId: "WA-REPETIDO"));
        await db.SaveChangesAsync();

        db.Mensagens.Add(NovaMensagem(c, waId: "WA-REPETIDO"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Duas_mensagens_com_wa_message_id_vazio_ambas_passam()
    {
        // A Evolution as vezes responde 2xx SEM key.id, e o cliente devolve string vazia. Se o
        // indice nao excluisse '', a segunda mensagem nessa situacao colidiria com a primeira —
        // duas mensagens legitimas viradas em uma.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "vazio");

        db.Mensagens.Add(NovaMensagem(c, waId: ""));
        db.Mensagens.Add(NovaMensagem(c, waId: ""));
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Mensagens.CountAsync(m => m.WaMessageId == ""));

        // NULL tambem nao colide (linha reservada, ainda nao postada).
        db.Mensagens.Add(NovaMensagem(c, waId: null));
        db.Mensagens.Add(NovaMensagem(c, waId: null));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Mensagens.CountAsync(m => m.WaMessageId == null));
    }

    [Fact]
    public async Task Mesmo_wa_message_id_em_instancias_diferentes_convive()
    {
        // A chave e (instance_name, wa_message_id): o id do WhatsApp so e unico DENTRO de uma
        // instancia. Se o indice fosse so por wa_message_id, duas empresas se atrapalhariam.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var a = await CenarioAsync(db, ctx, "inst-a");
        var b = await CenarioAsync(db, ctx, "inst-b");

        db.Mensagens.Add(NovaMensagem(a, waId: "WA-MESMO-ID"));
        db.Mensagens.Add(NovaMensagem(b, waId: "WA-MESMO-ID"));
        await db.SaveChangesAsync();   // instancias diferentes: ambas passam

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Mensagens.IgnoreQueryFilters()
            .CountAsync(m => m.WaMessageId == "WA-MESMO-ID"));
    }

    // ============================================================ uq_msg_lembrete
    [Fact]
    public async Task Um_lembrete_nao_gera_duas_mensagens()
    {
        // O teto diario impede DOIS lembretes no mesmo dia; nao impede UM lembrete ser enviado
        // duas vezes. Um crash entre "insere mensagem" e "marca concluido", ou duas instancias
        // do motor, reenviariam. Aqui o banco e o arbitro.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "lemb-msg");

        var lembrete = NovoLembrete(c, OrigemLembrete.Automatico, enviaMensagem: true);
        db.Lembretes.Add(lembrete);
        await db.SaveChangesAsync();

        var m1 = NovaMensagem(c, waId: "WA-L1");
        m1.LembreteId = lembrete.Id;
        db.Mensagens.Add(m1);
        await db.SaveChangesAsync();

        var m2 = NovaMensagem(c, waId: "WA-L2");
        m2.LembreteId = lembrete.Id;
        db.Mensagens.Add(m2);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Varias_mensagens_sem_lembrete_convivem()
    {
        // A outra metade: o indice e PARCIAL (WHERE lembrete_id IS NOT NULL). Sem o filtro,
        // so poderia existir UMA mensagem manual por instalacao inteira.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "sem-lemb");

        db.Mensagens.Add(NovaMensagem(c, waId: "WA-M1"));
        db.Mensagens.Add(NovaMensagem(c, waId: "WA-M2"));
        db.Mensagens.Add(NovaMensagem(c, waId: "WA-M3"));
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.Mensagens.CountAsync(m => m.LembreteId == null && m.WaMessageId!.StartsWith("WA-M")));
    }

    // ============================================================ uq_lembrete_teto_diario
    [Fact]
    public async Task Dois_lembretes_automaticos_no_mesmo_dia_para_o_mesmo_contato_o_segundo_falha()
    {
        // A defesa anti-spam: disparo em lote para o mesmo destinatario e o jeito classico de
        // ter o numero banido do WhatsApp.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "teto");

        db.Lembretes.Add(NovoLembrete(c, OrigemLembrete.Automatico, enviaMensagem: true));
        await db.SaveChangesAsync();

        db.Lembretes.Add(NovoLembrete(c, OrigemLembrete.Automatico, enviaMensagem: true));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Lembrete_automatico_e_manual_no_mesmo_dia_ambos_passam()
    {
        // O manual nao dispara mensagem — e lembrete de acao para o vendedor (ligar, visitar).
        // Se o filtro do indice esquecesse `origem = 'automatico'`, o produto impediria o
        // vendedor de anotar uma tarefa no dia em que ha follow-up automatico.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "teto-misto");

        db.Lembretes.Add(NovoLembrete(c, OrigemLembrete.Automatico, enviaMensagem: true));
        db.Lembretes.Add(NovoLembrete(c, OrigemLembrete.Manual, enviaMensagem: false));
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Lembretes.CountAsync());
    }

    [Fact]
    public async Task Lembrete_automatico_cancelado_libera_a_vaga_do_dia()
    {
        // Intencional: cancelar e remarcar tem que funcionar. O filtro traz
        // `status <> 'cancelado'` exatamente para isso.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "teto-cancel");

        var primeiro = NovoLembrete(c, OrigemLembrete.Automatico, enviaMensagem: true);
        db.Lembretes.Add(primeiro);
        await db.SaveChangesAsync();

        primeiro.Status = StatusLembrete.Cancelado;
        await db.SaveChangesAsync();

        db.Lembretes.Add(NovoLembrete(c, OrigemLembrete.Automatico, enviaMensagem: true));
        await db.SaveChangesAsync();   // a vaga do dia voltou

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Lembretes.CountAsync());
    }

    [Fact]
    public async Task Lembrete_automatico_em_dias_diferentes_ambos_passam()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "teto-dias");

        var hoje = NovoLembrete(c, OrigemLembrete.Automatico, enviaMensagem: true);
        var amanha = NovoLembrete(c, OrigemLembrete.Automatico, enviaMensagem: true);
        amanha.DataAlvo = hoje.DataAlvo.AddDays(1);
        db.Lembretes.AddRange(hoje, amanha);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Lembretes.CountAsync());
    }

    // ============================================================ uq_conexoes_empresa_nome
    /// <summary>ARQ-2: a SEGUNDA conexão na mesma empresa passa a ser permitida pelo BANCO.
    ///
    /// Este teste era o inverso — provava que `uq_conexoes_empresa` recusava. Ele fica aqui,
    /// invertido, em vez de ser apagado: o schema deixou de proibir de propósito, e quem só ler o
    /// código novo não teria como saber que a trava existiu. O teto de números virou regra de
    /// APLICAÇÃO (`empresas.limite_conexoes`, conferido em `ServicoConexoes.CriarAsync`), porque é
    /// número que muda por contrato — e isso um índice não sabe fazer.</summary>
    [Fact]
    public async Task Segunda_conexao_na_mesma_empresa_e_permitida_pelo_banco()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "conex");

        db.Conexoes.Add(new Conexao
        {
            EmpresaId = c.Id, Nome = "Segunda", InstanceName = "inst-conex-2"
        });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Conexoes.IgnoreQueryFilters().CountAsync(x => x.EmpresaId == c.Id));
    }

    /// <summary>O NOME, esse sim, é único dentro da empresa. Sem isso a lista de conexões teria
    /// duas linhas "Principal" e ninguém saberia qual número está apagando.</summary>
    [Fact]
    public async Task Duas_conexoes_com_o_mesmo_nome_na_mesma_empresa_falham()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "conex-nome");

        var nomeExistente = await db.Conexoes.IgnoreQueryFilters()
            .Where(x => x.EmpresaId == c.Id).Select(x => x.Nome).FirstAsync();

        db.Conexoes.Add(new Conexao
        {
            EmpresaId = c.Id, Nome = nomeExistente, InstanceName = "inst-conex-nome-2"
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    /// <summary>O mesmo nome em EMPRESAS diferentes convive: o índice é (empresa_id, nome), e
    /// "Principal" é o nome óbvio para a primeira conexão de qualquer empresa.</summary>
    [Fact]
    public async Task Mesmo_nome_de_conexao_em_empresas_diferentes_convive()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var a = await CenarioAsync(db, ctx, "conex-a");
        var b = await CenarioAsync(db, ctx, "conex-b");

        db.ChangeTracker.Clear();

        var nomes = await db.Conexoes.IgnoreQueryFilters()
            .Where(x => x.EmpresaId == a.Id || x.EmpresaId == b.Id)
            .Select(x => x.Nome).ToListAsync();

        Assert.Equal(2, nomes.Count);
        Assert.Single(nomes.Distinct());   // é o MESMO nome nas duas empresas, e o banco aceitou
    }

    [Fact]
    public async Task Instance_name_e_unico_globalmente_e_nao_por_empresa()
    {
        // O webhook casa por instance_name SEM tenant no contexto. Se duas empresas pudessem
        // usar a mesma instancia, o tenant ficaria ambiguo e a mensagem iria para a errada.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var a = await CenarioAsync(db, ctx, "inst-unica");

        var outra = new Empresa { Nome = "Outra" };
        db.Empresas.Add(outra);
        await db.SaveChangesAsync();

        db.Conexoes.Add(new Conexao
        {
            EmpresaId = outra.Id, Nome = "Principal", InstanceName = a.Conexao.InstanceName
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    // ============================================================ uq_etapas_ganho
    [Fact]
    public async Task Segunda_etapa_de_ganho_na_mesma_empresa_falha()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "ganho");   // ja tem uma etapa com EGanho

        db.EtapasFunil.Add(new EtapaFunil
        {
            EmpresaId = c.Id, PipelineId = c.Pipeline.Id,
            Nome = "Outro Ganho", Ordem = 99, EGanho = true
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Varias_etapas_sem_ganho_convivem_na_mesma_empresa()
    {
        // A outra metade: o indice e PARCIAL (WHERE e_ganho). Sem o filtro, cada empresa
        // poderia ter UMA UNICA etapa — o funil inteiro deixaria de existir.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "etapas");

        db.EtapasFunil.Add(new EtapaFunil { EmpresaId = c.Id, PipelineId = c.Pipeline.Id, Nome = "Extra 1", Ordem = 10 });
        db.EtapasFunil.Add(new EtapaFunil { EmpresaId = c.Id, PipelineId = c.Pipeline.Id, Nome = "Extra 2", Ordem = 11 });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        ctx.EmpresaId = c.Id;
        Assert.Equal(5, await db.EtapasFunil.CountAsync());
        Assert.Equal(1, await db.EtapasFunil.CountAsync(x => x.EGanho));
    }

    [Fact]
    public async Task Cada_empresa_tem_a_propria_etapa_de_ganho()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        await CenarioAsync(db, ctx, "ganho-a");
        await CenarioAsync(db, ctx, "ganho-b");

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.EtapasFunil.IgnoreQueryFilters().CountAsync(e => e.EGanho));
    }

    // ============================================================ uq_contatos_telefone
    [Fact]
    public async Task Telefone_duplicado_entre_contatos_vivos_falha_mas_anonimizados_convivem()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "tel");

        db.Contatos.Add(new Contato
        {
            EmpresaId = c.Id, Nome = "Clone", Telefone = c.Contato.Telefone
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Anonimizar dois contatos zera o telefone dos dois: o indice parcial deixa.
        ctx.EmpresaId = c.Id;
        var extra = new Contato
        {
            EmpresaId = c.Id, Nome = "Outro", Telefone = "5584911112222"
        };
        db.Contatos.Add(extra);
        await db.SaveChangesAsync();

        await db.Contatos.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Nome, "(anonimizado)")
            .SetProperty(x => x.Telefone, "")
            .SetProperty(x => x.AnonimizadoEm, DateTime.UtcNow));

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Contatos.CountAsync(x => x.AnonimizadoEm != null));
    }

    // ============================================================ checks
    [Fact]
    public async Task Contato_nao_pode_ser_ganho_e_perdido_ao_mesmo_tempo()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "terminal");

        // ⚠️ A CONSTRAINT MUDOU DE TABELA (E4e/4). Era `ck_contatos_terminal`; virou
        // `ck_negociacoes_terminal`, porque quem ganha e perde e o NEGOCIO. Deixar o teste no
        // contato nao seria so obsoleto: como as colunas sumiram, ele pararia de exercitar
        // constraint nenhuma e passaria sempre.
        var confuso = new Contato
        {
            EmpresaId = c.Id, Nome = "Confuso", Telefone = "5584933334444"
        };
        db.Contatos.Add(confuso);

        var negocio = Semeador.Negocio(confuso, c.PrimeiraEtapa, valor: 100m);
        negocio.Status = StatusNegociacao.Ganha;
        negocio.GanhaEm = DateTime.UtcNow;
        negocio.PerdidaEm = DateTime.UtcNow;
        db.Negociacoes.Add(negocio);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Mensagem_de_saida_exige_data_disparo_e_entrada_nao()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "disparo");

        var saidaSemData = NovaMensagem(c, waId: "WA-S1");
        saidaSemData.Direcao = DirecaoMensagem.Saida;
        saidaSemData.DataDisparo = null;
        db.Mensagens.Add(saidaSemData);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Entrada sem data_disparo passa (a coluna nao significa nada em inbound).
        db.Mensagens.Add(NovaMensagem(c, waId: "WA-S2"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Lembrete_que_envia_mensagem_exige_texto()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "lemb-texto");

        var semTexto = NovoLembrete(c, OrigemLembrete.Automatico, enviaMensagem: true);
        semTexto.TextoMensagem = null;
        db.Lembretes.Add(semTexto);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Conversa_nao_aceita_contador_de_nao_lidas_negativo()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "naolidas");

        // PostgresException crua, nao DbUpdateException: ExecuteUpdate manda o UPDATE direto,
        // sem passar pelo SaveChanges — entao nao ha nada envolvendo a excecao do driver.
        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Conversas.ExecuteUpdateAsync(s => s.SetProperty(x => x.NaoLidas, x => x.NaoLidas - 1)));

        Assert.Equal("23514", erro.SqlState);   // check_violation
        Assert.Contains("ck_conversas_nao_lidas", erro.Message);
        db.ChangeTracker.Clear();
    }

    // ============================================================ FK composta
    [Fact]
    public async Task Contato_nao_pode_apontar_para_etapa_de_outro_tenant()
    {
        // O query filter protege LEITURA; nada impede a aplicacao de GRAVAR um id de outro
        // tenant. A FK composta (etapa_id, empresa_id) fecha isso no banco.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var a = await CenarioAsync(db, ctx, "fk-a");
        var b = await CenarioAsync(db, ctx, "fk-b");

        // ⚠️ A FK COMPOSTA MUDOU DE TABELA (E4e/4): `fk_contatos_etapa` sumiu com a coluna, e
        // quem aponta para etapa agora e a negociacao. A garantia e a mesma, e continua sendo de
        // ESCRITA — o query filter so protege leitura.
        var intruso = new Contato
        {
            EmpresaId = a.Id, Nome = "Intruso", Telefone = "5584955556666"
        };
        db.Contatos.Add(intruso);
        db.Negociacoes.Add(Semeador.Negocio(intruso, b.PrimeiraEtapa));   // etapa do OUTRO tenant

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Conversa_nao_pode_apontar_para_contato_de_outro_tenant()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var a = await CenarioAsync(db, ctx, "fkc-a");
        var b = await CenarioAsync(db, ctx, "fkc-b");

        db.Conversas.Add(new Conversa
        {
            EmpresaId = a.Id,
            ContatoId = b.Contato.Id,          // contato do OUTRO tenant
            ConexaoId = a.Conexao.Id,
            UltimaMensagemEm = DateTime.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Conversa_e_um_a_um_com_contato_na_fase_1()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "1a1");

        db.Conversas.Add(new Conversa
        {
            EmpresaId = c.Id, ContatoId = c.Contato.Id, ConexaoId = c.Conexao.Id,
            UltimaMensagemEm = DateTime.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    // ============================================================ ordem_kanban
    [Fact]
    public async Task Ponto_medio_do_kanban_nao_esgota_a_escala()
    {
        // Se ordem_kanban fosse numeric(18,6), inserir sempre entre o mesmo par de cards
        // esgotaria a escala em ~19 movimentos e dois cards colidiriam na mesma posicao.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "kanban");
        ctx.EmpresaId = c.Id;

        // ⚠️ A COLUNA MUDOU DE TABELA (E4e/4), e a razao de ela ser `numeric` sem escala fixa e
        // exatamente esta: inserir sempre no meio do mesmo par esgotaria `numeric(18,6)` em ~19
        // movimentos, e dois cards colidiriam na mesma posicao.
        decimal anterior = 1m, posterior = 2m;
        for (var i = 0; i < 40; i++)
        {
            var meio = (anterior + posterior) / 2m;
            var card = new Contato
            {
                EmpresaId = c.Id, Nome = $"Card {i}", Telefone = $"558491{i:D7}"
            };
            db.Contatos.Add(card);
            db.Negociacoes.Add(Semeador.Negocio(card, c.PrimeiraEtapa, meio));
            posterior = meio;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // 40 posicoes DISTINTAS: nenhuma colidiu por arredondamento no banco.
        var ordens = await db.Negociacoes.Where(x => x.Contato.Nome.StartsWith("Card "))
            .Select(x => x.OrdemKanban).ToListAsync();
        Assert.Equal(40, ordens.Count);
        Assert.Equal(40, ordens.Distinct().Count());
    }

    // ---------------------------------------------------------------- apoio

    /// <summary>Semeia o tenant E o coloca no contexto, como faria uma requisicao autenticada.
    ///
    /// Sem o segundo passo, as contagens de conferencia deste arquivo voltam ZERO em silencio —
    /// a mesma armadilha documentada em IsolamentoDominioDbTests, que apareceu de verdade
    /// escrevendo estes testes: seis deles falhavam com "esperado 2, obtido 0" porque o
    /// contexto continuava em tenant zero.</summary>
    // ============================================================ ux_contatos_meta_lead_id
    /// <summary>⚠️ REIMPORTAR O MESMO ARQUIVO NÃO PODE CRIAR O CONTATO DE NOVO (INT-XX).
    ///
    /// O serviço checa antes, para a recusa virar uma linha "duplicado" no relatório em vez de um
    /// 500 — mas quem GARANTE é este índice. É a mesma divisão de trabalho de
    /// `uq_negociacoes_card_por_funil`.</summary>
    [Fact]
    public async Task O_MESMO_LEAD_DA_META_NAO_ENTRA_DUAS_VEZES()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "meta-lead");

        db.Contatos.Add(NovoContato(c, "5584911110001", metaLeadId: "1234567890123456"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Outra pessoa, outro telefone — mas o MESMO lead da Meta. É o arquivo subindo de novo.
        db.Contatos.Add(NovoContato(c, "5584911110002", metaLeadId: "1234567890123456"));

        var erro = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("ux_contatos_meta_lead_id", ((PostgresException)erro.InnerException!).Message);
    }

    /// <summary>⚠️ A OUTRA METADE: `meta_lead_id` é NULO em todo contato que veio por outra porta
    /// — WhatsApp, cadastro à mão, formulário do site —, que é a imensa maioria da base. Vários
    /// nulos na mesma empresa têm de conviver.
    ///
    /// ⚠️ ESTE COMENTÁRIO JÁ AFIRMOU A COISA ERRADA, e a sabotagem é que mostrou. Ele dizia que
    /// sem o `WHERE meta_lead_id IS NOT NULL` o segundo contato comum seria recusado. Não é: no
    /// Postgres um índice único comum trata `NULL` como DISTINTO de `NULL`, então tirar o `WHERE`
    /// não quebra nada — o `WHERE` só deixa o índice pequeno, com as linhas da Meta em vez da base
    /// inteira. Removê-lo passou nos testes, e era para passar.
    ///
    /// O que este teste guarda de verdade é outra coisa: `NULLS NOT DISTINCT` (Postgres 15+), que
    /// faz os nulos colidirem. Com ele, o segundo contato sem lead da Meta é recusado e o produto
    /// para de cadastrar gente — e o teste de recusa acima continuaria passando. Sabotado assim, ele
    /// falha, e leva junto outros quatro testes que também criam contatos comuns.</summary>
    [Fact]
    public async Task CONTATOS_SEM_LEAD_DA_META_CONVIVEM_AOS_MONTES()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "meta-nulos");

        for (var i = 0; i < 3; i++)
            db.Contatos.Add(NovoContato(c, $"558491111100{i}", metaLeadId: null));

        await db.SaveChangesAsync();   // não estoura: nulo não disputa a chave
        db.ChangeTracker.Clear();

        Assert.Equal(3, await db.Contatos.CountAsync(x => x.MetaLeadId == null && x.Nome == "Importado"));
    }

    /// <summary>E o mesmo `meta_lead_id` em DUAS EMPRESAS convive: o índice é por tenant, e duas
    /// empresas que anunciam podem receber o mesmo lead se a pessoa preencheu os dois
    /// formulários.</summary>
    [Fact]
    public async Task O_MESMO_LEAD_DA_META_EM_EMPRESAS_DIFERENTES_CONVIVE()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var a = await CenarioAsync(db, ctx, "meta-tenant-a");
        db.Contatos.Add(NovoContato(a, "5584911110001", metaLeadId: "9999999999999999"));
        await db.SaveChangesAsync();

        var b = await Semeador.TenantAsync(db, "meta-tenant-b");
        ctx.EmpresaId = b.Id;
        db.Contatos.Add(NovoContato(b, "5584911110002", metaLeadId: "9999999999999999"));

        await db.SaveChangesAsync();   // não estoura
        db.ChangeTracker.Clear();

        Assert.Equal(2, await db.Contatos.IgnoreQueryFilters()
            .CountAsync(x => x.MetaLeadId == "9999999999999999"));
    }

    // ============================================================ a data que veio no arquivo
    /// <summary>⚠️ O `InterceptorAuditoria` DEIXOU DE SOBRESCREVER `CriadoEm` QUANDO ELE JÁ VEM
    /// PREENCHIDO (INT-XX), e este teste é a razão de a mudança ser segura.
    ///
    /// O lead exportado do Gerenciador de Leads traz `created_time` — o instante em que a pessoa
    /// preencheu o formulário, dias atrás. Carimbar `agora` por cima faria 600 leads antigos
    /// entrarem como "hoje": o dashboard diria que foi o melhor dia do ano, e o follow-up trataria
    /// contato de três dias como recém-chegado.
    ///
    /// As DUAS metades, porque a mudança podia quebrar o caso normal: quem não informa data
    /// continua recebendo `agora`.</summary>
    [Fact]
    public async Task A_DATA_QUE_VEIO_NO_ARQUIVO_SOBREVIVE_AO_INSERT()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "data-importada");

        var quandoPreencheu = new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc);

        var antigo = NovoContato(c, "5584911110001", metaLeadId: "1111111111111111");
        antigo.CriadoEm = quandoPreencheu;
        db.Contatos.Add(antigo);

        // E o do lado, SEM data informada — o caminho de todo dia.
        var hoje = NovoContato(c, "5584911110002", metaLeadId: null);
        db.Contatos.Add(hoje);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var doArquivo = await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == antigo.Id);
        Assert.Equal(quandoPreencheu, doArquivo.CriadoEm);

        // ⚠️ E o normal NÃO regrediu: sem data informada, o interceptor carimba.
        var normal = await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == hoje.Id);
        Assert.NotEqual(default, normal.CriadoEm);
        Assert.True(normal.CriadoEm > quandoPreencheu, "o contato sem data informada não foi carimbado");
    }

    /// <summary>⚠️ E O `UPDATE` CONTINUA PROTEGIDO, que é a metade que a mudança NÃO podia tocar.
    ///
    /// O `case Modified` do interceptor marca `CriadoEm` como não-modificado justamente porque um
    /// objeto desanexado com a data zerada sobrescreveria a original. Afrouxei o `Added`; se o
    /// `Modified` afrouxar junto por descuido, é aqui que aparece.</summary>
    [Fact]
    public async Task EDITAR_UM_CONTATO_NAO_MEXE_NA_DATA_DE_CRIACAO()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();
        var c = await CenarioAsync(db, ctx, "data-update");

        var nascido = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc);
        var contato = NovoContato(c, "5584911110003", metaLeadId: null);
        contato.CriadoEm = nascido;
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // ⚠️ A VERSAO REAL VAI JUNTO. `Contato.Versao` e token de concorrencia mapeado no `xmin`,
        // entao um objeto desanexado com versao zero produz `WHERE xmin = 0`, que nao casa com
        // linha nenhuma — e o teste morreria de `DbUpdateConcurrencyException` antes de chegar na
        // pergunta que ele faz. O que se quer desanexado aqui e a DATA, nao a versao.
        var versao = await db.Contatos.AsNoTracking()
            .Where(x => x.Id == contato.Id).Select(x => x.Versao).SingleAsync();

        // Volta DESANEXADO e com a data zerada — exatamente o caso do comentário do interceptor.
        var solto = new Contato
        {
            Id = contato.Id, EmpresaId = c.Id, Nome = "Nome Novo",
            Telefone = contato.Telefone, Origem = OrigemLead.Manual, Versao = versao
        };
        db.Contatos.Attach(solto);
        db.Entry(solto).Property(x => x.Nome).IsModified = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var depois = await db.Contatos.AsNoTracking().SingleAsync(x => x.Id == contato.Id);
        Assert.Equal("Nome Novo", depois.Nome);
        Assert.Equal(nascido, depois.CriadoEm);
    }

    private static Contato NovoContato(Cenario c, string telefone, string? metaLeadId) =>
        new()
        {
            EmpresaId = c.Id,
            Nome = "Importado",
            Telefone = telefone,
            Origem = metaLeadId is null ? OrigemLead.Manual : OrigemLead.MetaAds,
            MetaLeadId = metaLeadId
        };

    private static async Task<Cenario> CenarioAsync(
        NexoraDbContext db, ContextoMutavel ctx, string sufixo)
    {
        var c = await Semeador.TenantAsync(db, sufixo);
        ctx.EmpresaId = c.Id;
        return c;
    }

    private static Mensagem NovaMensagem(Cenario c, string? waId) => new()
    {
        EmpresaId = c.Id,
        ConversaId = c.Conversa.Id,
        ContatoId = c.Contato.Id,
        ConexaoId = c.Conexao.Id,
        InstanceName = c.Conexao.InstanceName,
        Direcao = DirecaoMensagem.Entrada,
        WaMessageId = waId,
        Texto = "conteudo",
        RecebidaEm = DateTime.UtcNow
    };

    private static Lembrete NovoLembrete(Cenario c, OrigemLembrete origem, bool enviaMensagem) => new()
    {
        EmpresaId = c.Id,
        ContatoId = c.Contato.Id,
        ConversaId = c.Conversa.Id,
        Origem = origem,
        DataAlvo = new DateOnly(2026, 5, 20),
        Titulo = "Follow-up",
        EnviaMensagem = enviaMensagem,
        TextoMensagem = enviaMensagem ? "Oi, tudo certo?" : null,
        ResponsavelId = c.Dono.Id
    };
}
