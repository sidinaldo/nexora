using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Entidades;
using Nexora.Core.FollowUp;
using Nexora.Core.Servicos;
using Nexora.Core.Texto;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>A rodada de follow-up contra Postgres real.
///
/// A ELEGIBILIDADE é a parte que não veio do Recupera — lá é vencimento de dívida, aqui é
/// inatividade da conversa. Ela vive inteira no SQL, então testar em memória não provaria nada:
/// índice parcial, teto diário e o filtro por direção da última mensagem só existem no banco.</summary>
[Collection("banco")]
public class FollowUpDbTests(BancoTeste banco)
{
    // Quinta-feira, 10h30 da manhã em Brasília — dentro da janela padrão (8h-20h, seg-sáb).
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    // ============================================================ a regra de elegibilidade
    [Fact]
    public async Task Conversa_parada_com_ultima_mensagem_de_SAIDA_gera_lembrete()
    {
        var (db, tx, amb) = await PrepararAsync("elegivel");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);

        Assert.Equal(OrigemLembrete.Automatico, lembrete.Origem);
        Assert.True(lembrete.EnviaMensagem);
        Assert.Equal(amb.Cenario.Dono.Id, lembrete.ResponsavelId);   // herda o dono da conversa
        Assert.Contains(amb.Contato.Nome, lembrete.Titulo);
    }

    /// <summary>BUG-XX (T6): o texto do follow-up é o que a empresa escreveu em Configurações. Era
    /// fixo — "Passando para saber se você ainda tem interesse." —, sem dizer quem falava.</summary>
    [Fact]
    public async Task O_FOLLOW_UP_SAI_COM_O_TEXTO_DA_EMPRESA()
    {
        var (db, tx, amb) = await PrepararAsync("texto-proprio");
        using var _ = db; using var __ = tx;

        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.FollowUpTexto,
                "{{saudacao}} Seu orçamento da {{empresa}} continua valendo."), default);
        db.ChangeTracker.Clear();

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Gerados);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);
        Assert.Equal(
            $"{NomeDePessoa.Saudacao("Oi", amb.Contato.Nome)} Seu orçamento da Empresa texto-proprio continua valendo.",
            lembrete.TextoMensagem);
    }

    /// <summary>⚠️ A MENSAGEM QUE VAI PARA O CLIENTE DIZIA "Oi, (84)!".
    ///
    /// Relatado com a mensagem colada: "a régua está sendo enviada sem o nome ou numero do
    /// contato — Oi, (84)! Passando para saber se você ainda tem interesse."
    ///
    /// O contato que chega pelo WhatsApp SEM `pushName` recebe como nome o telefone formatado
    /// (`CanonicalizadorTelefone.Formatar`, que diz por escrito que é para isso), e "primeiro
    /// nome" era `Split(' ')[0]`. As duas decisões certas, longe uma da outra, produziram um
    /// cliente sendo chamado de "(84)" no WhatsApp dele.
    ///
    /// ⚠️ O TÍTULO E A MENSAGEM SÃO DE LEITORES DIFERENTES, e o teste fixa os dois: o título é do
    /// vendedor, que QUER ver o telefone quando não há nome; a mensagem é do cliente, que não pode
    /// ver número nenhum. Tratá-los igual é o que apagaria metade do conserto.</summary>
    [Fact]
    public async Task A_MENSAGEM_NAO_CHAMA_O_CLIENTE_PELO_TELEFONE()
    {
        var (db, tx, amb) = await PrepararAsync("nome-telefone");
        using var _ = db; using var __ = tx;

        // Exatamente o que o webhook grava quando o WhatsApp não manda `pushName`.
        var comoTelefone = CanonicalizadorTelefone.Formatar(amb.Contato.Telefone);
        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == amb.Contato.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Nome, comoTelefone), default);
        db.ChangeTracker.Clear();

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Gerados);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);

        // ---------- a MENSAGEM: nenhum pedaço do telefone, em nenhuma grafia
        Assert.Equal(
            "Oi! Aqui é da equipe Empresa nome-telefone. Ficou alguma dúvida sobre o que conversamos? "
            + "Se quiser continuar, é só responder aqui.", lembrete.TextoMensagem);

        Assert.DoesNotContain(comoTelefone, lembrete.TextoMensagem!);
        Assert.DoesNotContain("(", lembrete.TextoMensagem!);
        Assert.False(lembrete.TextoMensagem!.Any(char.IsDigit),
            $"a mensagem que sai para o cliente tem dígito: \"{lembrete.TextoMensagem}\"");

        // ---------- o TÍTULO: o vendedor continua vendo por quem esperar
        Assert.Contains(comoTelefone, lembrete.Titulo);
    }

    /// <summary>⚠️ O LEAD QUE CHEGA PELA CAIXA TAMBEM PRECISA DE FOLLOW-UP — e desde o E6 ele
    /// nao tem negociacao nenhuma.
    ///
    /// A elegibilidade dizia `Negociacoes.Any(Aberta)`, escrita como traducao de "nao esta em
    /// etapa terminal". Enquanto todo contato nascia com negociacao as duas frases eram a mesma;
    /// desde o E6 nao sao, e "sem negocio" passou a cair do lado de fora junto com ganho e
    /// perdido.
    ///
    /// O efeito: o vendedor responde o lead novo, o cliente some por cinco dias, e NENHUM
    /// lembrete e criado — exatamente o cenario que o motor existe para cobrir.</summary>
    [Fact]
    public async Task LEAD_SEM_NEGOCIO_NENHUM_TAMBEM_GERA_LEMBRETE()
    {
        var (db, tx, amb) = await PrepararAsync("sem-negocio");
        using var _ = db; using var __ = tx;

        // O contato do cenario perde a negociacao: vira o lead que chegou pela caixa e ainda nao
        // virou negocio. Contato e conversa continuam inteiros.
        await db.Negociacoes.IgnoreQueryFilters()
            .Where(n => n.ContatoId == amb.Contato.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);
    }

    [Fact]
    public async Task Conversa_cuja_ultima_mensagem_foi_de_ENTRADA_nao_gera_lembrete()
    {
        // A CONDIÇÃO MAIS IMPORTANTE DA ELEGIBILIDADE. Se a última foi de entrada, o CLIENTE
        // está esperando resposta — isso é o semáforo, não follow-up. Sem esta condição o sistema
        // cobra o vendedor duas vezes pela mesma coisa: no vermelho da caixa e no Meu Dia.
        var (db, tx, amb) = await PrepararAsync("entrada");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Entrada, diasAtras: 10);

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(0, r.Gerados);
        db.ChangeTracker.Clear();
        Assert.False(await db.Lembretes.IgnoreQueryFilters().AnyAsync(l => l.ContatoId == amb.Contato.Id));
    }

    [Fact]
    public async Task Conversa_parada_ha_MENOS_dias_que_o_configurado_nao_gera()
    {
        var (db, tx, amb) = await PrepararAsync("recente");
        using var _ = db; using var __ = tx;

        // DiasSemRespostaFollowUp padrão = 2. Um dia parado ainda não vale.
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 1);

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    [Fact]
    public async Task Contato_em_etapa_terminal_nao_gera_lembrete()
    {
        // Ganho ou perdido não se persegue. Mandar follow-up para quem já comprou é o tipo de
        // erro que o cliente percebe antes da gente.
        var (db, tx, amb) = await PrepararAsync("ganho");
        using var _ = db; using var __ = tx;

        var ganhoEm = DateTime.UtcNow.AddDays(-3);

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        // E o negocio: "etapa terminal" passou a ser o STATUS da negociacao (E4e).
        //
        // ⚠️ COM VALOR. `ck_negociacoes_valor` recusa ganha sem valor, e esta certo: ela entraria
        // no faturamento como zero, e o dono so notaria fechando o mes. A fixture aprendeu isso
        // levando o 23514 na cara.
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == amb.Contato.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, StatusNegociacao.Ganha)
                .SetProperty(n => n.GanhaEm, ganhoEm)
                .SetProperty(n => n.Valor, 150m));
        db.ChangeTracker.Clear();

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    [Fact]
    public async Task Contato_perdido_nao_gera_lembrete()
    {
        var (db, tx, amb) = await PrepararAsync("perdido");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        var perdidoEm = DateTime.UtcNow.AddDays(-3);

        // O negocio, que e de onde o motor le desde o E4e.
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.ContatoId == amb.Contato.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, StatusNegociacao.Perdida)
                .SetProperty(n => n.PerdidaEm, perdidoEm)
                .SetProperty(n => n.MotivoPerda, "comprou do concorrente"));
        db.ChangeTracker.Clear();

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    [Fact]
    public async Task Conversa_resolvida_nao_gera_lembrete()
    {
        var (db, tx, amb) = await PrepararAsync("resolvida");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, StatusConversa.Resolvida)
                .SetProperty(c => c.ResolvidoEm, DateTime.UtcNow));
        db.ChangeTracker.Clear();

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    [Fact]
    public async Task Contato_com_lembrete_pendente_nao_ganha_outro()
    {
        // Senão o vendedor recebe a mesma tarefa todo dia até fazer — e para de olhar a lista.
        var (db, tx, amb) = await PrepararAsync("ja-tem");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Contato.Id,
            Origem = OrigemLembrete.Manual,
            Status = StatusLembrete.Pendente,
            DataAlvo = DateOnly.FromDateTime(QuintaDeManha.UtcDateTime).AddDays(3),
            Titulo = "ligar depois"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    // ============================================================ um follow-up por silêncio
    /// <summary>⚠️ O DEFEITO: QUEM NÃO RESPONDIA RECEBIA A MESMA MENSAGEM A CADA RODADA.
    ///
    /// O envio do motor não move `ultima_mensagem_em` (o eco da Evolution é descartado como
    /// "mensagem já existente"), e o lembrete concluído deixava de contar como pendente. No banco
    /// de desenvolvimento, um contato recebeu "Passando para saber se você ainda tem interesse" em
    /// 14/09, 17/09 e 25/09 — toda rodada que rodou. Estas duas rodadas são as mesmas: o dia
    /// seguinte, e uma semana depois.</summary>
    [Fact]
    public async Task UM_FOLLOW_UP_POR_SILENCIO_a_rodada_seguinte_nao_manda_de_novo()
    {
        var (db, tx, amb) = await PrepararAsync("um-por-silencio");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Enviados);

        amb.Relogio.Avancar(TimeSpan.FromDays(1));
        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);

        amb.Relogio.Avancar(TimeSpan.FromDays(6));
        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);

        Assert.Single(amb.Cliente.TextosEnviados);
    }

    /// <summary>O outro lado da regra: o silêncio acaba quando alguém volta a falar, e o seguinte
    /// tem direito ao seu follow-up. Sem isto, "um por silêncio" viraria "um por contato, para
    /// sempre" — o primeiro follow-up da vida do lead bloquearia todos os outros.</summary>
    [Fact]
    public async Task QUANDO_O_VENDEDOR_VOLTA_A_FALAR_O_SILENCIO_SEGUINTE_GANHA_O_SEU()
    {
        var (db, tx, amb) = await PrepararAsync("silencio-novo");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Enviados);

        // Sexta: o vendedor escreve de novo, e o cliente some outra vez.
        amb.Relogio.Avancar(TimeSpan.FromDays(1));
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 0,
            agora: amb.Relogio.GetUtcNow().UtcDateTime);

        // Segunda: três dias de silêncio novo, acima dos dois do padrão.
        amb.Relogio.Avancar(TimeSpan.FromDays(3));
        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Enviados);

        Assert.Equal(2, amb.Cliente.TextosEnviados.Count);
    }

    /// <summary>Cancelar o follow-up é o vendedor dizendo "não mande". Se o cancelado não contasse,
    /// a rodada seguinte criaria outro igual, e cancelar viraria tarefa de todo dia.</summary>
    [Fact]
    public async Task FOLLOW_UP_CANCELADO_CONTA_COMO_O_DESTE_SILENCIO()
    {
        var (db, tx, amb) = await PrepararAsync("cancelado-conta");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await LembreteDepoisDoSilencioAsync(db, amb, OrigemLembrete.Automatico, StatusLembrete.Cancelado);

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    /// <summary>A regra é sobre o ROBÔ insistir. Uma mensagem que o vendedor agendou à mão é
    /// decisão dele, e não ocupa o lugar do follow-up do silêncio.</summary>
    [Fact]
    public async Task MENSAGEM_AGENDADA_A_MAO_NAO_OCUPA_O_LUGAR_DO_FOLLOW_UP()
    {
        var (db, tx, amb) = await PrepararAsync("manual-nao-conta");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await LembreteDepoisDoSilencioAsync(db, amb, OrigemLembrete.Manual, StatusLembrete.Concluido);

        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    /// <summary>O "Primeiro contato" da captura de formulário é AUTOMÁTICO e não manda nada: é
    /// tarefa para o vendedor. Concluída, ela não pode contar como "o cliente já recebeu o
    /// follow-up deste silêncio" — ele não recebeu mensagem nenhuma.</summary>
    [Fact]
    public async Task TAREFA_AUTOMATICA_SEM_MENSAGEM_NAO_OCUPA_O_LUGAR_DO_FOLLOW_UP()
    {
        var (db, tx, amb) = await PrepararAsync("tarefa-nao-conta");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await LembreteDepoisDoSilencioAsync(
            db, amb, OrigemLembrete.Automatico, StatusLembrete.Concluido, enviaMensagem: false);

        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Gerados);
    }

    // ============================================================ uma conversa por número (CONV-XX)
    /// <summary>O número A parado não dispara se o cliente está falando no B. O follow-up é do
    /// CONTATO, e mandar "ainda tem interesse?" no meio da conversa pelo outro número é o robô
    /// atropelando o vendedor.</summary>
    [Fact]
    public async Task CONV_O_NUMERO_PARADO_NAO_DISPARA_SE_O_CLIENTE_FALA_NO_OUTRO()
    {
        var (db, tx, amb) = await PrepararAsync("conv-fala-no-outro");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await Semeador.SegundoNumeroAsync(db, amb.Cenario, QuintaDeManha.UtcDateTime.AddHours(-1));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(0, r.Gerados);
        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    /// <summary>Os dois números parados: UM follow-up, pelo número em que se falou por último.</summary>
    [Fact]
    public async Task CONV_COM_OS_DOIS_NUMEROS_PARADOS_SAI_UM_SO_PELO_MAIS_RECENTE()
    {
        var (db, tx, amb) = await PrepararAsync("conv-dois-parados");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 10);
        var (conexaoB, conversaB) = await Semeador.SegundoNumeroAsync(
            db, amb.Cenario, QuintaDeManha.UtcDateTime.AddDays(-5));
        await PararConversaAsync(
            db, conversaB.Id, amb.Contato.Id, DirecaoMensagem.Saida, 5, QuintaDeManha.UtcDateTime);

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);
        Assert.Equal(0, r.Barrados);
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);
        Assert.Equal(conversaB.Id, lembrete.ConversaId);
        Assert.Equal(conexaoB.InstanceName, Assert.Single(amb.Cliente.TextosEnviados).Instancia);
    }

    // ============================================================ o teto diário anti-spam
    /// <summary>DUAS INSTÂNCIAS RODANDO JUNTAS — não há lock distribuído, ver `AgendadorFollowUp`.
    /// As duas leem a elegibilidade antes de qualquer uma gravar. O uq_lembrete_teto_diario barra a
    /// segunda, e o INSERT ... ON CONFLICT DO NOTHING traduz isso em "barrado" — não em exceção. Se
    /// virasse exceção, o `catch` por empresa engoliria a rodada INTEIRA daquele tenant.
    ///
    /// ⚠️ ESTE TESTE RODAVA O MOTOR DUAS VEZES EM SEQUÊNCIA e contava com a conversa voltar a ser
    /// elegível depois do primeiro envio — que era justamente o defeito do follow-up repetido. A
    /// segunda instância agora é simulada como ela é: com a leitura feita ANTES da gravação.</summary>
    [Fact]
    public async Task Segundo_automatico_no_mesmo_dia_e_BARRADO_pelo_banco_sem_excecao()
    {
        var (db, tx, amb) = await PrepararAsync("teto-motor");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        var dados = new ElegibilidadeCongelada(new DadosFollowUp(db, amb.Relogio));
        var motor = new MotorFollowUp(
            dados,
            new EnviadorMensagem(
                new DadosMensagem(db, amb.Relogio), amb.Cliente,
                new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero },
                amb.Relogio, NullLogger<EnviadorMensagem>.Instance),
            amb.Relogio, NullLogger<MotorFollowUp>.Instance);

        var primeira = await motor.ExecutarAsync();
        Assert.Equal(1, primeira.Gerados);

        var segunda = await motor.ExecutarAsync();

        Assert.Equal(0, segunda.Gerados);
        Assert.Equal(1, segunda.Barrados);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Lembretes.IgnoreQueryFilters()
            .CountAsync(l => l.ContatoId == amb.Contato.Id && l.Origem == OrigemLembrete.Automatico));
    }

    // ============================================================ reserve-defer
    /// <summary>===================== A MENSAGEM DO MOTOR NASCE AUTOMATICA (NPS-1) =====================
    ///
    /// ⚠️ A COLUNA TEM `DEFAULT 'humana'`, e e isso que torna este teste necessario. Esquecer de
    /// gravar `Origem` no `MotorFollowUp` nao quebra nada: a linha entra como humana, o banco
    /// aceita, a rodada declara sucesso — e a mensagem volta a contar como resposta no relatorio
    /// de tempo de resposta. O backfill da migration consertaria o passado e o futuro nasceria
    /// errado, sem nenhum sintoma.
    /// ==============================================================</summary>
    [Fact]
    public async Task A_MENSAGEM_DO_LEMBRETE_NASCE_MARCADA_COMO_AUTOMATICA()
    {
        var (db, tx, amb) = await PrepararAsync("origem-auto");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Gerados);

        db.ChangeTracker.Clear();
        var linha = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida);

        Assert.Equal(OrigemMensagem.Automatica, linha.Origem);
        Assert.Equal(TipoAutomacao.Lembrete, linha.TipoAutomacao);
    }

    /// <summary>BUG-XX: fora do horário a rodada não gera, não reserva e não posta — e a primeira
    /// hora dentro do horário manda. Era "reserva agora, posta na próxima rodada", e com uma rodada
    /// só por dia a próxima caía de novo fora do horário.</summary>
    [Fact]
    public async Task Rodada_FORA_do_horario_nao_faz_nada_e_a_primeira_hora_aberta_envia()
    {
        // 23h de quinta: postar aqui é acordar cliente de madrugada.
        var (db, tx, amb) = await PrepararAsync(
            "fora-janela", new DateTimeOffset(2026, 8, 7, 2, 0, 0, TimeSpan.Zero));   // 23h BRT de 06/08
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5,
            agora: new DateTime(2026, 8, 7, 2, 0, 0, DateTimeKind.Utc));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(0, r.Gerados);
        Assert.Empty(amb.Cliente.TextosEnviados);
        Assert.False(await db.Mensagens.IgnoreQueryFilters()
            .AnyAsync(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida));

        // Sexta, 9h: a rodada da hora gera e manda, uma vez.
        amb.Relogio.Avancar(TimeSpan.FromHours(10));
        var dentro = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, dentro.Gerados);
        Assert.Equal(1, dentro.Enviados);
        Assert.Single(amb.Cliente.TextosEnviados);
    }

    /// <summary>BUG-XX: nenhuma automática antes das 8h no relógio da empresa, mesmo para quem
    /// abre mais cedo (decisão do dono).</summary>
    [Fact]
    public async Task Antes_das_8h_nada_sai_mesmo_com_o_horario_aberto()
    {
        // 7h30 de quinta, e a empresa atende das 6h às 20h.
        var (db, tx, amb) = await PrepararAsync(
            "antes-das-8", new DateTimeOffset(2026, 8, 6, 10, 30, 0, TimeSpan.Zero));
        using var _ = db; using var __ = tx;
        await JanelaAsync(db, amb.Cenario.Id, inicio: 6, fim: 20);
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5,
            agora: new DateTime(2026, 8, 6, 10, 30, 0, DateTimeKind.Utc));

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Gerados);
        Assert.Empty(amb.Cliente.TextosEnviados);

        amb.Relogio.Avancar(TimeSpan.FromMinutes(30));   // 8h em ponto
        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Enviados);
    }

    /// <summary>BUG-XX: o salão que abre às 9h recebe às 9h — não às 8h, e não nunca.</summary>
    [Fact]
    public async Task Empresa_que_abre_as_9h_recebe_as_9h()
    {
        var (db, tx, amb) = await PrepararAsync(
            "abre-9h", new DateTimeOffset(2026, 8, 6, 11, 0, 0, TimeSpan.Zero));   // 8h BRT
        using var _ = db; using var __ = tx;
        await JanelaAsync(db, amb.Cenario.Id, inicio: 9, fim: 18);
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5,
            agora: new DateTime(2026, 8, 6, 11, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Enviados);

        amb.Relogio.Avancar(TimeSpan.FromHours(1));   // 9h
        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Enviados);
    }

    /// <summary>BUG-XX: o relógio é o DA EMPRESA. Em Manaus, 8h de Brasília são 7h — ainda cedo.</summary>
    [Fact]
    public async Task Empresa_de_Manaus_recebe_as_8h_de_Manaus()
    {
        var (db, tx, amb) = await PrepararAsync(
            "manaus", new DateTimeOffset(2026, 8, 6, 11, 0, 0, TimeSpan.Zero));   // 8h BRT = 7h AMT
        using var _ = db; using var __ = tx;
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.FusoHorario, "America/Manaus"));
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5,
            agora: new DateTime(2026, 8, 6, 11, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Enviados);

        amb.Relogio.Avancar(TimeSpan.FromHours(1));   // 9h BRT = 8h AMT
        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Enviados);
    }

    /// <summary>BUG-XX: a rodada passa de hora em hora, e o que saiu não sai de novo.</summary>
    [Fact]
    public async Task A_rodada_da_hora_seguinte_nao_repete_o_que_saiu()
    {
        var (db, tx, amb) = await PrepararAsync("hora-seguinte");
        using var _ = db; using var __ = tx;
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        Assert.Equal(1, (await amb.Motor.ExecutarAsync()).Enviados);

        for (var hora = 1; hora <= 3; hora++)
        {
            amb.Relogio.Avancar(TimeSpan.FromHours(1));
            var r = await amb.Motor.ExecutarAsync();
            Assert.Equal(0, r.Enviados);
            Assert.Equal(0, r.Gerados);
        }

        Assert.Single(amb.Cliente.TextosEnviados);
    }

    /// <summary>BUG-XX: NA DÚVIDA, NÃO REENVIA. A conexão caiu no meio do envio e não dá para saber
    /// se chegou: a linha sai da fila com o motivo, e nenhuma rodada depois tenta de novo.</summary>
    [Fact]
    public async Task Envio_sem_confirmacao_nao_e_reenviado()
    {
        var (db, tx, amb) = await PrepararAsync("incerto");
        using var _ = db; using var __ = tx;
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        amb.Cliente.ErroParaLancar = new IntegracaoWhatsAppException("sem resposta", incerto: true);
        var r = await amb.Motor.ExecutarAsync();
        Assert.Equal(1, r.Falhas);

        amb.Cliente.ErroParaLancar = null;
        for (var hora = 1; hora <= 3; hora++)
        {
            amb.Relogio.Avancar(TimeSpan.FromHours(1));
            Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Enviados);
        }

        Assert.Single(amb.Cliente.TextosEnviados);   // só a tentativa que ficou sem resposta

        db.ChangeTracker.Clear();
        var linha = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida);
        Assert.Null(linha.EnviadaEm);
        Assert.NotNull(linha.ExpiradaEm);
        Assert.Equal(EnviadorMensagem.MotivoIncerto, linha.Erro);
    }

    /// <summary>BUG-XX: o WhatsApp aceitou e a gravação da confirmação falhou. A mensagem SAIU — ela
    /// não pode virar falha, senão a drenagem a manda de novo.</summary>
    [Fact]
    public async Task Confirmacao_que_falha_depois_do_envio_nao_vira_reenvio()
    {
        var (db, tx, amb) = await PrepararAsync("confirmacao-falha");
        using var _ = db; using var __ = tx;
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        var motor = MontarMotor(db, amb.Contexto, amb.Cliente, amb.Relogio,
            new ConfirmacaoQueQuebra(new DadosMensagem(db, amb.Relogio)));

        Assert.Equal(1, (await motor.ExecutarAsync()).Enviados);

        for (var hora = 1; hora <= 3; hora++)
        {
            amb.Relogio.Avancar(TimeSpan.FromHours(1));
            Assert.Equal(0, (await motor.ExecutarAsync()).Enviados);
        }

        Assert.Single(amb.Cliente.TextosEnviados);
    }

    /// <summary>BUG-XX: o processo cai entre o WhatsApp aceitar e o banco gravar — aqui, nem a marca
    /// de enviada grava. Sobra `envio_iniciado_em`: a linha não volta para a fila, e a arrumação da
    /// rodada a dá por "não confirmada".</summary>
    [Fact]
    public async Task Envio_que_ficou_no_meio_nao_volta_para_a_fila()
    {
        var (db, tx, amb) = await PrepararAsync("ficou-no-meio");
        using var _ = db; using var __ = tx;
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        var quebrado = MontarMotor(db, amb.Contexto, amb.Cliente, amb.Relogio,
            new ConfirmacaoQueQuebra(new DadosMensagem(db, amb.Relogio)) { NemAMarca = true });
        await quebrado.ExecutarAsync();

        for (var hora = 1; hora <= 3; hora++)
        {
            amb.Relogio.Avancar(TimeSpan.FromHours(1));
            Assert.Equal(0, (await amb.Motor.ExecutarAsync()).Enviados);
        }

        Assert.Single(amb.Cliente.TextosEnviados);

        db.ChangeTracker.Clear();
        var linha = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida);
        Assert.Null(linha.EnviadaEm);
        Assert.NotNull(linha.ExpiradaEm);
        Assert.Equal(EnviadorMensagem.MotivoIncerto, linha.Erro);
    }

    /// <summary>BUG-XX: a falha DE VERDADE é tentada no máximo 3 vezes — com a rodada de hora em hora,
    /// sem teto ela seria tentada a cada hora até a reserva vencer.</summary>
    [Fact]
    public async Task Falha_e_tentada_no_maximo_3_vezes()
    {
        var (db, tx, amb) = await PrepararAsync("tres-tentativas");
        using var _ = db; using var __ = tx;
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        amb.Cliente.ErroParaLancar = new IntegracaoWhatsAppException("Evolution API respondeu 500");
        await amb.Motor.ExecutarAsync();
        for (var hora = 1; hora <= 5; hora++)
        {
            amb.Relogio.Avancar(TimeSpan.FromHours(1));
            await amb.Motor.ExecutarAsync();
        }

        Assert.Equal(3, amb.Cliente.TextosEnviados.Count);
    }

    [Fact]
    public async Task Conexao_caida_reserva_sem_postar_e_a_rodada_seguinte_drena()
    {
        var (db, tx, amb) = await PrepararAsync("conexao-caida");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        amb.Cliente.EstadoParaDevolver = "close";

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);
        Assert.Equal(1, r.Adiados);
        Assert.Empty(amb.Cliente.TextosEnviados);

        // A Evolution voltou: a drenagem reaproveita a MESMA linha, sem criar outra.
        amb.Cliente.EstadoParaDevolver = "open";
        var segunda = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, segunda.Enviados);
        Assert.Single(amb.Cliente.TextosEnviados);

        db.ChangeTracker.Clear();
        var linhas = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida)
            .ToListAsync();

        Assert.Single(linhas);                  // UMA linha, não duas
        Assert.NotNull(linhas[0].EnviadaEm);
    }

    // ============================================================ multi-número (ARQ-2)
    [Fact]
    public async Task UM_NUMERO_CAIDO_NAO_SEGURA_O_FOLLOW_UP_DO_OUTRO()
    {
        // ===================== O BUG QUE ISTO IMPEDE DE VOLTAR =====================
        // Até o ARQ-2 o motor pegava "a" conexão da empresa e usava o estado dela como porteiro da
        // rodada INTEIRA. Numa empresa com dois números, o de Vendas cair fazia o de Suporte parar
        // de mandar follow-up também — e o log dizia "conexão caída", que era verdade sobre uma
        // conexão e mentira sobre a rodada.
        //
        // O freio passou a ser por INSTÂNCIA, e o destino de cada lembrete sempre foi a conexão da
        // CONVERSA. Este teste prende as duas coisas juntas: se o freio voltar a ser por empresa,
        // `Enviados` cai para 0; se o destino voltar a ser "a" conexão da empresa, a instância da
        // mensagem enviada muda.
        // ==========================================================================
        var (db, tx, amb) = await PrepararAsync("dois-numeros");
        using var _ = db; using var __ = tx;

        var segunda = await SegundaConexaoAsync(db, amb.Cenario, "dois-numeros");

        // Duas conversas paradas: uma em cada número.
        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await PararConversaAsync(db, segunda.Conversa.Id, segunda.Contato.Id,
            DirecaoMensagem.Saida, 5, QuintaDeManha.UtcDateTime);

        // O número do cenário caiu; o novo está no ar.
        amb.Cliente.EstadoPorInstancia[amb.Cenario.Conexao.InstanceName] = "close";
        amb.Cliente.EstadoPorInstancia[segunda.Conexao.InstanceName] = "open";

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(2, r.Gerados);
        Assert.Equal(1, r.Enviados);   // o número no ar mandou
        Assert.Equal(1, r.Adiados);    // o caído só reservou

        var enviada = Assert.Single(amb.Cliente.TextosEnviados);
        Assert.Equal(segunda.Conexao.InstanceName, enviada.Instancia);
        Assert.Equal(segunda.Contato.Telefone, enviada.Telefone);

        db.ChangeTracker.Clear();

        // A do número caído ficou reservada, sem sair, e com a instância DELA — não a do outro.
        var pendente = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida);
        Assert.Null(pendente.EnviadaEm);
        Assert.Equal(amb.Cenario.Conexao.InstanceName, pendente.InstanceName);
    }

    [Fact]
    public async Task DRENAGEM_RESPEITA_A_CONEXAO_DA_MENSAGEM_PENDENTE()
    {
        // A pendente carrega o `instance_name` que a reservou. Drenar tudo pela conexão da empresa
        // mandaria a mensagem por um número que não é o da conversa — e o cliente veria a resposta
        // chegar de um telefone que ele nunca contatou.
        var (db, tx, amb) = await PrepararAsync("drenar-por-conexao");
        using var _ = db; using var __ = tx;

        var segunda = await SegundaConexaoAsync(db, amb.Cenario, "drenar-por-conexao");

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await PararConversaAsync(db, segunda.Conversa.Id, segunda.Contato.Id,
            DirecaoMensagem.Saida, 5, QuintaDeManha.UtcDateTime);

        // Rodada 1: os DOIS números fora. Ninguém envia, os dois reservam.
        amb.Cliente.EstadoParaDevolver = "close";
        var primeira = await amb.Motor.ExecutarAsync();
        Assert.Equal(2, primeira.Adiados);
        Assert.Empty(amb.Cliente.TextosEnviados);

        // Rodada 2: SÓ o segundo voltou.
        amb.Cliente.EstadoPorInstancia[segunda.Conexao.InstanceName] = "open";
        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Enviados);
        var enviada = Assert.Single(amb.Cliente.TextosEnviados);
        Assert.Equal(segunda.Conexao.InstanceName, enviada.Instancia);

        db.ChangeTracker.Clear();

        // E a do número ainda caído continua pendente — reaproveitando a MESMA linha.
        var linhas = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida)
            .ToListAsync();
        Assert.Single(linhas);
        Assert.Null(linhas[0].EnviadaEm);
    }

    // ============================================================ isolamento de falha
    [Fact]
    public async Task Excecao_numa_empresa_nao_interrompe_as_outras()
    {
        // Sem o catch por empresa, a primeira exceção interrompe o laço e NINGUÉM depois dela
        // recebe follow-up — em silêncio, porque o job segue "de pé".
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var a = await Semeador.TenantAsync(db, "quebra-a");
        var b = await Semeador.TenantAsync(db, "quebra-b");

        // Só as duas empresas deste teste participam da rodada (a transação isola as demais,
        // mas empresas de outros testes commitados não existem — cada teste faz rollback).
        await DesativarOutrasAsync(db, a.Id, b.Id);

        await PararConversaAsync(db, a.Conversa.Id, a.Contato.Id, DirecaoMensagem.Saida, 5, QuintaDeManha.UtcDateTime);
        await PararConversaAsync(db, b.Conversa.Id, b.Contato.Id, DirecaoMensagem.Saida, 5, QuintaDeManha.UtcDateTime);

        var relogio = new RelogioFalso(QuintaDeManha);
        var cliente = new ClienteWhatsAppFalso();

        // A instância da empresa A explode ao consultar o estado — dado ruim, cenário real.
        var clienteQueQuebraNaA = new ClienteQuebraNaInstancia(cliente, a.Conexao.InstanceName);
        var motor = MontarMotor(db, ctx, clienteQueQuebraNaA, relogio);

        var r = await motor.ExecutarAsync();

        // A empresa B foi atendida apesar da explosão na A.
        db.ChangeTracker.Clear();
        Assert.False(await db.Lembretes.IgnoreQueryFilters().AnyAsync(l => l.EmpresaId == a.Id));
        Assert.True(await db.Lembretes.IgnoreQueryFilters().AnyAsync(l => l.EmpresaId == b.Id));
        Assert.Equal(1, r.Gerados);
    }

    [Fact]
    public async Task Empresa_sem_conexao_e_pulada_sem_derrubar_a_rodada()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var semNumero = new Empresa { Nome = "Ainda nao pareou" };
        db.Empresas.Add(semNumero);
        await db.SaveChangesAsync();

        var comNumero = await Semeador.TenantAsync(db, "com-numero");
        await DesativarOutrasAsync(db, semNumero.Id, comNumero.Id);
        await PararConversaAsync(db, comNumero.Conversa.Id, comNumero.Contato.Id,
            DirecaoMensagem.Saida, 5, QuintaDeManha.UtcDateTime);

        var motor = MontarMotor(db, ctx, new ClienteWhatsAppFalso(), new RelogioFalso(QuintaDeManha));

        Assert.Equal(1, (await motor.ExecutarAsync()).Gerados);
    }

    // ============================================================ o envio
    [Fact]
    public async Task Rodada_dentro_da_janela_gera_envia_e_conclui_o_lembrete()
    {
        var (db, tx, amb) = await PrepararAsync("caminho-feliz");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);
        Assert.Equal(1, r.Enviados);
        Assert.Equal(0, r.Falhas);

        var enviada = Assert.Single(amb.Cliente.TextosEnviados);
        Assert.Equal(amb.Cenario.Conexao.InstanceName, enviada.Instancia);
        Assert.Equal(amb.Contato.Telefone, enviada.Telefone);
        // O texto usa o PRIMEIRO nome, não o nome inteiro do cadastro.
        Assert.Contains(amb.Contato.Nome.Split(' ')[0], enviada.Texto);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);
        Assert.Equal(StatusLembrete.Concluido, lembrete.Status);
        Assert.NotNull(lembrete.ConcluidoEm);

        var mensagem = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.LembreteId == lembrete.Id);
        Assert.NotNull(mensagem.EnviadaEm);
    }

    [Fact]
    public async Task Falha_na_Evolution_nao_conclui_o_lembrete_e_a_linha_fica_com_o_erro()
    {
        var (db, tx, amb) = await PrepararAsync("falha-envio");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        amb.Cliente.ErroParaLancar = new IntegracaoWhatsAppException("Evolution caiu no meio");

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Gerados);
        Assert.Equal(1, r.Falhas);
        Assert.Equal(0, r.Enviados);

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);
        Assert.Equal(StatusLembrete.Pendente, lembrete.Status);   // NÃO concluiu

        // A linha FICA, com o erro. Apagar liberaria o dedupe e um POST que chegou mas deu
        // timeout viraria mensagem duplicada.
        var mensagem = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.LembreteId == lembrete.Id);
        Assert.Null(mensagem.EnviadaEm);
        Assert.Contains("caiu no meio", mensagem.Erro!);
    }

    // ============================================================ feriado
    [Fact]
    public async Task Feriado_fecha_a_janela_em_pleno_horario_comercial()
    {
        // 10h30 de uma quinta — hora comercial. Mas a empresa marcou o dia como ponto
        // facultativo, e nada acontece. Se a janela olhasse só a hora e o bitmask, o cliente
        // receberia follow-up no feriado.
        var (db, tx, amb) = await PrepararAsync("feriado-hoje");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await MarcarFeriadosAsync(db, amb.Cenario.Id, new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 7));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(0, r.Gerados);
        Assert.Equal(0, r.Enviados);
        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    /// <summary>BUG-XX: no feriado nada nasce; o follow-up nasce e sai no próximo dia aberto, com a
    /// data dele. Antes ele nascia no feriado com a data "deslizada" — e com a rodada de hora em
    /// hora não há o que deslizar: a rodada simplesmente espera o dia aberto.</summary>
    [Fact]
    public async Task Feriado_deixa_o_follow_up_para_o_proximo_dia_aberto()
    {
        // 06/08/2026 é quinta. Com quinta E sexta em ponto facultativo, o próximo dia aberto é
        // SÁBADO — a janela padrão inclui sábado.
        var (db, tx, amb) = await PrepararAsync("feriado-desliza");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Saida, diasAtras: 5);
        await MarcarFeriadosAsync(db, amb.Cenario.Id, new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 7));

        await amb.Motor.ExecutarAsync();
        Assert.False(await db.Lembretes.IgnoreQueryFilters().AnyAsync(l => l.ContatoId == amb.Contato.Id));

        amb.Relogio.Avancar(TimeSpan.FromDays(2));   // sábado, 10h30
        var r = await amb.Motor.ExecutarAsync();

        db.ChangeTracker.Clear();
        var lembrete = await db.Lembretes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.ContatoId == amb.Contato.Id);

        Assert.Equal(new DateOnly(2026, 8, 8), lembrete.DataAlvo);   // sábado
        Assert.Equal(1, r.Enviados);
    }

    /// <summary>BUG-XX: o lembrete que venceu no feriado não é reservado nem perdido — ele espera, e
    /// sai na primeira hora do próximo dia aberto.</summary>
    [Fact]
    public async Task Lembrete_vencido_espera_o_proximo_dia_aberto_e_sai_nele()
    {
        var (db, tx, amb) = await PrepararAsync("defer");
        using var _ = db; using var __ = tx;

        await PararConversaAsync(db, amb, DirecaoMensagem.Entrada, diasAtras: 1);   // não gera novo
        await MarcarFeriadosAsync(db, amb.Cenario.Id, new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 7));

        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Contato.Id,
            ConversaId = amb.Conversa.Id,
            Origem = OrigemLembrete.Automatico,
            Status = StatusLembrete.Pendente,
            DataAlvo = new DateOnly(2026, 8, 5),        // venceu ontem
            Titulo = "retomar",
            EnviaMensagem = true,
            TextoMensagem = "ainda tem interesse?"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await amb.Motor.ExecutarAsync();
        Assert.Empty(amb.Cliente.TextosEnviados);
        Assert.False(await db.Mensagens.IgnoreQueryFilters()
            .AnyAsync(m => m.ContatoId == amb.Contato.Id && m.Direcao == DirecaoMensagem.Saida));

        amb.Relogio.Avancar(TimeSpan.FromDays(2));   // sábado, 10h30
        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Enviados);
        Assert.Single(amb.Cliente.TextosEnviados);
    }

    [Fact]
    public async Task Feriado_de_uma_empresa_nao_vale_para_outra()
    {
        // O feriado manual é do tenant. O global (empresa_id NULL) é de todos — e é o único que
        // o seed nacional cria.
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var a = await Semeador.TenantAsync(db, "fer-a");
        var b = await Semeador.TenantAsync(db, "fer-b");

        db.Feriados.Add(new Feriado
        {
            EmpresaId = a.Id, Data = new DateOnly(2026, 8, 6),
            Nome = "Aniversário da cidade", Abrangencia = AbrangenciaFeriado.Manual
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var dados = new DadosFollowUp(db, new RelogioFalso(QuintaDeManha));

        var deA = await dados.FeriadosAsync(a.Id, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), default);
        var deB = await dados.FeriadosAsync(b.Id, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), default);

        Assert.Contains(new DateOnly(2026, 8, 6), deA);
        Assert.DoesNotContain(new DateOnly(2026, 8, 6), deB);
    }

    /// <summary>BUG-XX: o feriado ESTADUAL é global (sem empresa) e tem `uf`. Bastava uma empresa do
    /// RN para ele valer em São Paulo — no motor e em toda tela que lê `feriados`.</summary>
    [Fact]
    public async Task Feriado_estadual_so_vale_para_a_empresa_do_estado()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var rn = await Semeador.TenantAsync(db, "fer-rn");
        var sp = await Semeador.TenantAsync(db, "fer-sp");
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == rn.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.Uf, "RN"));
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == sp.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.Uf, "SP"));

        var dia = new DateOnly(2026, 8, 13);
        db.Feriados.Add(new Feriado
        {
            EmpresaId = null, Data = dia, Uf = "RN",
            Nome = "Feriado do RN de teste", Abrangencia = AbrangenciaFeriado.Estadual
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // O motor (job, sem tenant)…
        var dados = new DadosFollowUp(db, new RelogioFalso(QuintaDeManha));
        Assert.Contains(dia, await dados.FeriadosAsync(rn.Id, dia, dia, default));
        Assert.DoesNotContain(dia, await dados.FeriadosAsync(sp.Id, dia, dia, default));

        // …e as telas, pelo filtro de tenant.
        ctx.EmpresaId = rn.Id;
        Assert.True(await db.Feriados.AnyAsync(f => f.Data == dia && f.Uf == "RN"));
        ctx.EmpresaId = sp.Id;
        Assert.False(await db.Feriados.AnyAsync(f => f.Data == dia && f.Uf == "RN"));
    }

    // ============================================================ apoio
    private sealed record Ambiente(
        Cenario Cenario, Contato Contato, Conversa Conversa, ContextoMutavel Contexto,
        ClienteWhatsAppFalso Cliente, MotorFollowUp Motor, RelogioFalso Relogio);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo, DateTimeOffset? quando = null)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);
        await DesativarOutrasAsync(db, cenario.Id);

        var relogio = new RelogioFalso(quando ?? QuintaDeManha);
        var cliente = new ClienteWhatsAppFalso();

        // A rodada roda SEM tenant no contexto (é job) — de propósito: é o que prova que o
        // IgnoreQueryFilters + filtro explícito do DadosFollowUp está no lugar.
        return (db, tx, new Ambiente(
            cenario, cenario.Contato, cenario.Conversa, ctx, cliente,
            MontarMotor(db, ctx, cliente, relogio), relogio));
    }

    private static MotorFollowUp MontarMotor(
        NexoraDbContext db, ContextoMutavel ctx, IClienteWhatsApp cliente, TimeProvider relogio,
        IDadosMensagem? dadosMensagem = null)
    {
        var enviador = new EnviadorMensagem(
            dadosMensagem ?? new DadosMensagem(db, relogio), cliente,
            new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero },
            relogio, NullLogger<EnviadorMensagem>.Instance);

        return new MotorFollowUp(
            new DadosFollowUp(db, relogio), enviador, relogio, NullLogger<MotorFollowUp>.Instance);
    }

    /// <summary>Um SEGUNDO número na mesma empresa, com contato e conversa próprios.
    ///
    /// Precisa de contato e conversa próprios porque o que se quer provar é que os dois números
    /// caminham independentes — reaproveitar a conversa do cenário faria as duas apontarem para a
    /// mesma linha e o teste não distinguiria nada.</summary>
    private static async Task<(Conexao Conexao, Contato Contato, Conversa Conversa)>
        SegundaConexaoAsync(NexoraDbContext db, Cenario cenario, string sufixo)
    {
        var conexao = new Conexao
        {
            EmpresaId = cenario.Id,
            Nome = "Suporte",
            InstanceName = $"inst-{sufixo}-2",
            Status = StatusConexao.Conectado,
            Numero = "5584900009999"
        };
        db.Conexoes.Add(conexao);
        await db.SaveChangesAsync();

        var contato = new Contato
        {
            EmpresaId = cenario.Id,
            Nome = $"Contato 2 {sufixo}",
            // ⚠️ `Semeador.Semente` e nao `GetHashCode()`: o hash de string do .NET e semeado por
            // PROCESSO, entao gera telefone diferente a cada rodada. Ja custou um CI vermelho.
            Telefone = $"5584{Semeador.Semente($"{sufixo}-2") % 900000000 + 100000000:D9}",
            ResponsavelId = cenario.Dono.Id
        };
        db.Contatos.Add(contato);

        // ⚠️ A NEGOCIACAO NASCE JUNTO (E4e). O motor de follow-up pergunta "este contato ainda
        // esta em negociacao?", e desde este bloco a resposta sai de `negociacoes`. Sem ela, o
        // contato nao gera lembrete nenhum — e o teste falha acusando o produto, nao a fixture.
        db.Negociacoes.Add(Semeador.Negocio(contato, cenario.PrimeiraEtapa, 2000m));

        await db.SaveChangesAsync();

        var conversa = new Conversa
        {
            EmpresaId = cenario.Id,
            ContatoId = contato.Id,
            ConexaoId = conexao.Id,
            ResponsavelId = cenario.Dono.Id,
            UltimaMensagemEm = DateTime.UtcNow
        };
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        return (conexao, contato, conversa);
    }

    /// <summary>Deixa ATIVAS só as empresas do teste. A transação isola as linhas dos outros
    /// testes, mas a rodada varre `empresas` inteira — sem isto, um teste que rode em paralelo
    /// com dados commitados de desenvolvimento veria empresas alheias.</summary>
    private static async Task DesativarOutrasAsync(NexoraDbContext db, params long[] manter)
    {
        await db.Empresas.IgnoreQueryFilters()
            .Where(e => !manter.Contains(e.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Ativo, false));
        db.ChangeTracker.Clear();
    }

    /// <summary>O horário de atendimento da empresa, todos os dias da semana.</summary>
    private static async Task JanelaAsync(NexoraDbContext db, long empresaId, short inicio, short fim)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == empresaId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.JanelaHoraInicio, inicio)
                .SetProperty(e => e.JanelaHoraFim, fim)
                .SetProperty(e => e.JanelaDiasSemana, (short)127));
        db.ChangeTracker.Clear();
    }

    /// <summary>O banco que ACEITA o envio e quebra ao gravar a confirmação — o eco que tomou o id
    /// antes, ou o banco caindo no meio (BUG-XX).</summary>
    private sealed class ConfirmacaoQueQuebra(IDadosMensagem real) : IDadosMensagem
    {
        public Task<long?> ReservarLembreteAsync(Mensagem reserva, CancellationToken ct) => real.ReservarLembreteAsync(reserva, ct);
        public Task<long?> ReservarNpsAsync(Mensagem reserva, CancellationToken ct) => real.ReservarNpsAsync(reserva, ct);
        public Task<Mensagem?> PerguntaNpsDaVendaAsync(long empresaId, long negociacaoId, CancellationToken ct) => real.PerguntaNpsDaVendaAsync(empresaId, negociacaoId, ct);
        public Task<long> GravarManualAsync(Mensagem mensagem, CancellationToken ct) => real.GravarManualAsync(mensagem, ct);
        public Task IniciarEnvioAsync(long mensagemId, CancellationToken ct) => real.IniciarEnvioAsync(mensagemId, ct);
        public Task ConfirmarEnvioAsync(long mensagemId, string waMessageId, CancellationToken ct) =>
            throw new InvalidOperationException("o banco caiu ao confirmar");
        public Task MarcarEnviadaSemIdAsync(long mensagemId, CancellationToken ct) =>
            NemAMarca ? throw new InvalidOperationException("o banco caiu de vez") : real.MarcarEnviadaSemIdAsync(mensagemId, ct);

        /// <summary>O banco caiu DE VEZ: nem a marca de enviada grava. Sobra `envio_iniciado_em`.</summary>
        public bool NemAMarca { get; init; }
        public Task RegistrarFalhaAsync(long mensagemId, string erro, CancellationToken ct) => real.RegistrarFalhaAsync(mensagemId, erro, ct);
        public Task MarcarIncertaAsync(long mensagemId, string motivo, CancellationToken ct) => real.MarcarIncertaAsync(mensagemId, motivo, ct);
        public Task DescartarAsync(long mensagemId, string motivo, CancellationToken ct) => real.DescartarAsync(mensagemId, motivo, ct);
        public Task TrocarPorModeloAsync(long mensagemId, long modeloId, string texto, CancellationToken ct) => real.TrocarPorModeloAsync(mensagemId, modeloId, texto, ct);
        public Task<IReadOnlyList<Mensagem>> PendentesAsync(long empresaId, DateOnly desde, int maxTentativas, CancellationToken ct) => real.PendentesAsync(empresaId, desde, maxTentativas, ct);
        public Task<int> ExpirarVencidasAsync(long empresaId, DateOnly limite, CancellationToken ct) => real.ExpirarVencidasAsync(empresaId, limite, ct);
        public Task<bool> EhDemonstracaoAsync(long empresaId, CancellationToken ct) => real.EhDemonstracaoAsync(empresaId, ct);
    }

    private static async Task MarcarFeriadosAsync(NexoraDbContext db, long empresaId, params DateOnly[] datas)
    {
        db.Feriados.AddRange(datas.Select(d => new Feriado
        {
            EmpresaId = empresaId, Data = d,
            Nome = "Ponto facultativo", Abrangencia = AbrangenciaFeriado.Manual
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private Task PararConversaAsync(
        NexoraDbContext db, Ambiente amb, DirecaoMensagem direcao, int diasAtras, DateTime? agora = null) =>
        PararConversaAsync(db, amb.Conversa.Id, amb.Contato.Id, direcao, diasAtras,
            agora ?? QuintaDeManha.UtcDateTime);

    /// <summary>Deixa a conversa "parada há N dias" com a última mensagem na direção pedida.
    /// `aguardando_desde` acompanha a direção: entrada deixa o cliente esperando, saída não.</summary>
    private static async Task PararConversaAsync(
        NexoraDbContext db, long conversaId, long contatoId,
        DirecaoMensagem direcao, int diasAtras, DateTime agora)
    {
        var quando = agora.AddDays(-diasAtras);

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == conversaId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UltimaMensagemEm, quando)
                .SetProperty(c => c.UltimaMensagemDirecao, direcao)
                .SetProperty(c => c.UltimaMensagemPrevia, "última mensagem")
                .SetProperty(c => c.AguardandoDesde,
                    direcao == DirecaoMensagem.Entrada ? quando : (DateTime?)null));

        db.ChangeTracker.Clear();
    }

    /// <summary>Um lembrete com mensagem criado DEPOIS da última mensagem da conversa — isto é,
    /// neste silêncio. Data de hoje no relógio do teste.</summary>
    private static async Task LembreteDepoisDoSilencioAsync(
        NexoraDbContext db, Ambiente amb, OrigemLembrete origem, StatusLembrete status,
        bool enviaMensagem = true)
    {
        var agora = amb.Relogio.GetUtcNow().UtcDateTime;
        db.Lembretes.Add(new Lembrete
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Contato.Id,
            ConversaId = amb.Conversa.Id,
            Origem = origem,
            Status = status,
            DataAlvo = DateOnly.FromDateTime(agora),
            Titulo = "Retomar contato",
            EnviaMensagem = enviaMensagem,
            TextoMensagem = enviaMensagem ? "Oi! Passando para saber se você ainda tem interesse." : null,
            CriadoEm = agora,
            AtualizadoEm = agora
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>A SEGUNDA INSTÂNCIA: a primeira leitura da elegibilidade vai ao banco, e as
    /// seguintes devolvem a mesma lista — o que vê quem leu antes de a outra instância gravar.</summary>
    private sealed class ElegibilidadeCongelada(IDadosFollowUp real) : IDadosFollowUp
    {
        private IReadOnlyList<ConversaInativa>? _lida;

        public async Task<IReadOnlyList<ConversaInativa>> ConversasInativasAsync(
            long empresaId, DateTime limite, CancellationToken ct) =>
            _lida ??= await real.ConversasInativasAsync(empresaId, limite, ct);

        public Task<IReadOnlyList<Empresa>> EmpresasAtivasAsync(CancellationToken ct) =>
            real.EmpresasAtivasAsync(ct);

        public Task<IReadOnlyList<(long Id, string InstanceName)>> ConexoesAsync(long empresaId, CancellationToken ct) =>
            real.ConexoesAsync(empresaId, ct);

        public Task<HashSet<DateOnly>> FeriadosAsync(long empresaId, DateOnly de, DateOnly ate, CancellationToken ct) =>
            real.FeriadosAsync(empresaId, de, ate, ct);

        public Task<long?> CriarLembreteAutomaticoAsync(
            long empresaId, long contatoId, long conversaId, long? responsavelId,
            DateOnly dataAlvo, string titulo, string texto, CancellationToken ct) =>
            real.CriarLembreteAutomaticoAsync(
                empresaId, contatoId, conversaId, responsavelId, dataAlvo, titulo, texto, ct);

        public Task<IReadOnlyList<LembreteParaDisparar>> LembretesADispararAsync(
            long empresaId, DateOnly hoje, CancellationToken ct) =>
            real.LembretesADispararAsync(empresaId, hoje, ct);

        public Task ConcluirLembreteAsync(long lembreteId, CancellationToken ct) =>
            real.ConcluirLembreteAsync(lembreteId, ct);

        public Task<string?> TelefoneDoContatoAsync(long contatoId, CancellationToken ct) =>
            real.TelefoneDoContatoAsync(contatoId, ct);
    }

    /// <summary>Decorador que explode ao consultar UMA instância específica. Simula o dado ruim
    /// de uma empresa sem contaminar as outras.</summary>
    private sealed class ClienteQuebraNaInstancia(IClienteWhatsApp real, string instanciaRuim) : IClienteWhatsApp
    {
        public Task<string> StatusInstanciaAsync(string instanceName, CancellationToken ct) =>
            instanceName == instanciaRuim
                ? throw new InvalidOperationException("instância corrompida")
                : real.StatusInstanciaAsync(instanceName, ct);

        public Task<string> EnviarTextoAsync(string i, string t, string x, CancellationToken ct) =>
            real.EnviarTextoAsync(i, t, x, ct);

        public Task<string> EnviarMidiaAsync(string i, string t, string b, string mt, string mi, string f, string? l, CancellationToken ct) =>
            real.EnviarMidiaAsync(i, t, b, mt, mi, f, l, ct);

        public Task<MidiaRecebida?> ObterMidiaAsync(string i, string w, string j, CancellationToken ct) =>
            real.ObterMidiaAsync(i, w, j, ct);

        public Task<string> EnviarAudioAsync(string i, string t, string b, CancellationToken ct) =>
            real.EnviarAudioAsync(i, t, b, ct);

        public Task<string> EnviarModeloAsync(string i, string t, ModeloParaEnvio m, CancellationToken ct) =>
            real.EnviarModeloAsync(i, t, m, ct);

        public Task<RespostaQr> ConectarInstanciaAsync(string i, string? n, CancellationToken ct) =>
            real.ConectarInstanciaAsync(i, n, ct);

        public Task<DetalhesInstancia?> ObterDetalhesInstanciaAsync(string i, CancellationToken ct) =>
            real.ObterDetalhesInstanciaAsync(i, ct);

        public Task DesconectarInstanciaAsync(string i, CancellationToken ct) =>
            real.DesconectarInstanciaAsync(i, ct);

        public Task RemoverInstanciaAsync(string i, CancellationToken ct) =>
            real.RemoverInstanciaAsync(i, ct);
    }
}
