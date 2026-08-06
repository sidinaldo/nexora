using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Reescreve a THREAD de conversas que já existem, com diálogos de verdade.
///
/// Roda autenticado (o query filter global vale) e só em Development — ver DevController.
///
/// Sem `TimeProvider`: este semeador não inventa instante nenhum. Todo horário sai de
/// `conversas.ultima_mensagem_em`, que já existe — é o que preserva a distribuição do semáforo.</summary>
public class ServicoSementeConversas(NexoraDbContext db) : IServicoSementeConversas
{
    /// <summary>O prefixo do `wa_message_id` do que sai daqui. É o que distingue a mensagem de
    /// roteiro da que veio da semeadura geral (`SEM-`) ou de um webhook de verdade.</summary>
    public const string Marca = "ROT";

    /// <summary>Teto por chamada. Sessenta conversas com diálogo de 6 a 17 turnos dá ~700
    /// mensagens — o suficiente para a caixa parecer uma caixa sem multiplicar por cinco a tabela
    /// de maior volume do banco.</summary>
    private const int MaximoConversas = 200;

    public async Task<ResumoSementeConversas> SemearAsync(int quantas, CancellationToken ct)
    {
        quantas = Math.Clamp(quantas, 1, MaximoConversas);

        var janela = await JanelaDaEmpresaAsync(ct);
        var fuso = await FusoDaEmpresaAsync(ct);

        // As mais RECENTES: são as que aparecem no topo da caixa, e portanto as que alguém vai
        // abrir. Adensar as do fim da lista seria trabalho que ninguém vê.
        var conversas = await db.Conversas
            .OrderByDescending(c => c.UltimaMensagemEm).ThenByDescending(c => c.Id)
            .Take(quantas)
            .ToListAsync(ct);

        if (conversas.Count == 0)
            return new ResumoSementeConversas(0, 0, 0, 0, 0);

        var ids = conversas.Select(c => c.Id).ToList();
        var conexaoPorConversa = conversas.ToDictionary(c => c.Id, c => c.ConexaoId);

        var instancias = await db.Conexoes.AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.InstanceName, ct);

        // ===================== IDEMPOTÊNCIA POR APAGAR ANTES =====================
        // Sem isto, a segunda execução empilharia um segundo diálogo em cima do primeiro e a
        // thread viraria duas conversas coladas — com a mesma pessoa dizendo "boa tarde" no meio
        // do assunto. Apaga TUDO da conversa, não só o que este semeador criou: a thread é um
        // texto contínuo, e misturar as três mensagens antigas com o roteiro dá o mesmo problema.
        // =========================================================================
        var apagadas = await db.Mensagens.Where(m => ids.Contains(m.ConversaId)).ExecuteDeleteAsync(ct);
        db.ChangeTracker.Clear();

        // Recarrega: o ExecuteDelete não passa pelo ChangeTracker, e as entidades acima ficaram
        // com estado velho.
        conversas = await db.Conversas.Where(c => ids.Contains(c.Id)).ToListAsync(ct);

        var donoId = await db.Usuarios.AsNoTracking()
            .Where(u => u.Papel == PapelUsuario.Dono).Select(u => (long?)u.Id).FirstOrDefaultAsync(ct);

        var criadas = 0;
        var naoEntregues = 0;
        var expiradas = 0;

        foreach (var conversa in conversas)
        {
            var conexaoId = conexaoPorConversa[conversa.Id];
            var instancia = instancias.GetValueOrDefault(conexaoId, $"emp-{conversa.EmpresaId}");

            var (mensagens, semEntrega, expirou) = MontarThread(
                conversa, conexaoId, instancia, donoId, janela, fuso);

            // Em ORDEM cronológica: a thread da caixa ordena por `id`, não por data. Inserir fora
            // de ordem daria um diálogo embaralhado com timestamps certos — o pior dos dois.
            db.Mensagens.AddRange(mensagens);

            criadas += mensagens.Count;
            naoEntregues += semEntrega;
            expiradas += expirou;
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        return new ResumoSementeConversas(conversas.Count, criadas, apagadas, naoEntregues, expiradas);
    }

    // ==================================================================== a thread
    private (List<Mensagem> Mensagens, int NaoEntregues, int Expiradas) MontarThread(
        Conversa conversa, long conexaoId, string instancia, long? donoId,
        JanelaAtendimento janela, TimeZoneInfo fuso)
    {
        // ===================== O ESTADO DA CONVERSA MANDA =====================
        // `aguardando_desde` preenchido significa que o CLIENTE falou por último e está esperando.
        // É isso que acende o semáforo, alimenta o Meu Dia e conta no "aguardando resposta" do
        // dashboard. O roteiro é escolhido para terminar nessa direção — o contrário quebraria o
        // cenário inteiro de uma vez, e em silêncio.
        // ======================================================================
        var esperando = conversa.AguardandoDesde is not null;
        var roteiro = RoteirosConversa.Escolher(esperando, conversa.Id);

        // Determinístico por conversa: reexecutar dá exatamente a mesma thread, com os mesmos
        // intervalos. Semeadura que muda a cada execução torna impossível conferir o que mudou.
        var rnd = new Random((int)(conversa.Id * 7919 % int.MaxValue));

        // Os intervalos entre turnos, em minutos ÚTEIS. A distribuição é o que dá ritmo: a maioria
        // é resposta rápida (2-15 min), e um em cada quatro é a pausa longa de quem foi almoçar ou
        // dormiu em cima do assunto.
        var intervalos = new int[roteiro.Turnos.Length];
        for (var i = 1; i < intervalos.Length; i++)
            intervalos[i] = rnd.Next(4) == 0 ? rnd.Next(90, 420) : rnd.Next(2, 16);

        // A ÚLTIMA mensagem cai exatamente em `ultima_mensagem_em` — o instante que a semeadura
        // anterior escolheu para pôr esta conversa numa faixa do semáforo. Daí para trás.
        var fimLocal = TimeZoneInfo.ConvertTimeFromUtc(conversa.UltimaMensagemEm, fuso);
        var quandoLocal = new DateTime[roteiro.Turnos.Length];
        quandoLocal[^1] = fimLocal;

        for (var i = roteiro.Turnos.Length - 2; i >= 0; i--)
            quandoLocal[i] = RecuarNoExpediente(quandoLocal[i + 1], intervalos[i + 1], janela);

        var mensagens = new List<Mensagem>(roteiro.Turnos.Length);
        int naoEntregues = 0, expiradas = 0;

        for (var i = 0; i < roteiro.Turnos.Length; i++)
        {
            var entrada = i % 2 == 0;   // índice par é o cliente — ver Roteiro
            var quando = TimeZoneInfo.ConvertTimeToUtc(quandoLocal[i], fuso);

            var m = new Mensagem
            {
                EmpresaId = conversa.EmpresaId,
                ConversaId = conversa.Id,
                ContatoId = conversa.ContatoId,
                ConexaoId = conexaoId,
                InstanceName = instancia,
                Direcao = entrada ? DirecaoMensagem.Entrada : DirecaoMensagem.Saida,
                Texto = roteiro.Turnos[i],
                // Único por empresa (uq_msg_wa_id). O id da conversa mais a posição basta, e é
                // estável entre execuções.
                WaMessageId = $"{Marca}-{conversa.Id}-{i}",
                RecebidaEm = entrada ? quando : null,
                EnviadaEm = entrada ? null : quando,
                // ck_msg_data_disparo: saída EXIGE data_disparo.
                DataDisparo = entrada ? null : DateOnly.FromDateTime(quando),
                ReservadoEm = quando,
                EnviadoPor = entrada ? null : donoId,
                // ACK só na saída. Alterna 4 (lido) e 3 (entregue) para os dois ticks aparecerem —
                // sem os dois, metade do desenho da thread nunca é vista.
                Ack = entrada ? null : (short)(i % 4 == 1 ? 4 : 3),
                AckEm = entrada ? null : quando.AddMinutes(1),
                Tentativas = entrada ? (short)0 : (short)1
            };

            mensagens.Add(m);
        }

        // ===================== OS DOIS ESTADOS TERMINAIS DO ENVIO =====================
        // Uma mensagem que FALHOU e uma que EXPIROU, numa conversa a cada cinco. Eles existem no
        // desenho da thread (tick de erro, aviso de "não entregue") e sem um exemplo na base
        // ninguém vê esses caminhos até um cliente reclamar.
        //
        // NUNCA na última: a última define a direção e a prévia da conversa, e uma falha ali faria
        // a caixa mostrar erro como se fosse o assunto.
        // ==============================================================================
        if (conversa.Id % 5 == 0)
        {
            var candidata = mensagens
                .Take(mensagens.Count - 1)
                .LastOrDefault(x => x.Direcao == DirecaoMensagem.Saida);

            if (candidata is not null)
            {
                candidata.EnviadaEm = null;
                candidata.Ack = null;
                candidata.AckEm = null;
                candidata.Tentativas = 3;
                candidata.Erro = "Evolution API respondeu 500: instância sem sessão ativa.";
                naoEntregues++;

                // Numa a cada dez, a falha ainda EXPIRA: passou da janela de reenvio e não será
                // mais tentada. É número próprio no endpoint de saúde, e a diferença entre "ainda
                // vai" e "não vai mais" é a que exige ação humana.
                if (conversa.Id % 10 == 0)
                {
                    candidata.ExpiradaEm = candidata.ReservadoEm.AddDays(3);
                    expiradas++;
                }
            }
        }

        AjustarConversa(conversa, mensagens, esperando);
        return (mensagens, naoEntregues, expiradas);
    }

    /// <summary>Alinha a conversa com a thread nova — SEM tocar no que define o semáforo.
    ///
    /// `ultima_mensagem_em`, `aguardando_desde` e `status` ficam como estavam. O que muda é a
    /// PRÉVIA (que precisa bater com o último balão, senão a lista da caixa mostra um texto que
    /// não existe na conversa) e o contador de NÃO LIDAS.</summary>
    private static void AjustarConversa(
        Conversa conversa, List<Mensagem> mensagens, bool esperando)
    {
        var ultima = mensagens[^1];

        conversa.UltimaMensagemDirecao = ultima.Direcao;
        conversa.UltimaMensagemPrevia = ultima.Texto is { Length: > 120 } t ? t[..120] : ultima.Texto;

        // Não lidas = quantas mensagens de ENTRADA vieram desde a última resposta nossa. Antes era
        // um número aleatório entre 1 e 3, o que dava badge "3" numa conversa com uma mensagem só.
        var naoLidas = 0;
        for (var i = mensagens.Count - 1; i >= 0 && mensagens[i].Direcao == DirecaoMensagem.Entrada; i--)
            naoLidas++;

        conversa.NaoLidas = esperando ? Math.Max(1, naoLidas) : 0;
    }

    // ==================================================================== expediente
    /// <summary>Recua `minutos` a partir de `local`, contando SÓ tempo de expediente.
    ///
    /// ===================== POR QUE NÃO SUBTRAIR E PRONTO =====================
    /// Subtração direta espalha mensagens às 3h da manhã e no domingo. Isso não é só feio: o
    /// semáforo mede espera em minutos ÚTEIS, e uma conversa cujo diálogo acontece de madrugada
    /// tem "10 horas de espera" que valem zero na conta — o cenário passaria a exercitar
    /// exatamente o caso que não interessa.
    ///
    /// Feriado NÃO entra: a janela aqui vem da empresa, e carregar o calendário de feriados para
    /// posicionar mensagem de mentira custaria mais do que vale. A consequência é uma thread
    /// ocasional caindo num feriado — visualmente indistinguível de um dia comum.
    /// ========================================================================</summary>
    private static DateTime RecuarNoExpediente(DateTime local, int minutos, JanelaAtendimento janela)
    {
        var vazio = new HashSet<DateOnly>();

        // A trava de 400 voltas é a mesma dos utilitários de tempo do Core: com bitmask zerado
        // (dado ruim) o laço não terminaria nunca.
        for (var volta = 0; volta < 400 && minutos > 0; volta++)
        {
            if (!janela.Contem(local, vazio))
            {
                local = FechamentoAnterior(local, janela, vazio);
                continue;
            }

            var abertura = local.Date.AddHours(janela.HoraInicio);
            var disponivel = (int)(local - abertura).TotalMinutes;

            if (disponivel >= minutos) return local.AddMinutes(-minutos);

            minutos -= disponivel;
            local = abertura.AddMinutes(-1);   // cai para o dia anterior
        }

        return local;
    }

    /// <summary>O último minuto de expediente ANTES de `local`.</summary>
    private static DateTime FechamentoAnterior(
        DateTime local, JanelaAtendimento janela, HashSet<DateOnly> vazio)
    {
        // Ainda é hoje, mas depois de fechar: volta para o fim do expediente de hoje.
        if (CalendarioAtendimento.DiaPermitido(DateOnly.FromDateTime(local), janela.DiasSemana, vazio)
            && local.Hour >= janela.HoraFim)
            return local.Date.AddHours(janela.HoraFim).AddMinutes(-1);

        var dia = local.Date.AddDays(-1);
        for (var i = 0; i < 14; i++)
        {
            if (CalendarioAtendimento.DiaPermitido(DateOnly.FromDateTime(dia), janela.DiasSemana, vazio))
                return dia.AddHours(janela.HoraFim).AddMinutes(-1);
            dia = dia.AddDays(-1);
        }

        return local.AddDays(-1);   // bitmask sem nenhum dia: devolve algo em vez de girar
    }

    private async Task<JanelaAtendimento> JanelaDaEmpresaAsync(CancellationToken ct)
    {
        var e = await db.Empresas.AsNoTracking()
            .Select(x => new { x.JanelaHoraInicio, x.JanelaHoraFim, x.JanelaDiasSemana })
            .FirstOrDefaultAsync(ct);

        return e is null
            ? JanelaAtendimento.Padrao
            : new JanelaAtendimento(e.JanelaHoraInicio, e.JanelaHoraFim, e.JanelaDiasSemana);
    }

    private async Task<TimeZoneInfo> FusoDaEmpresaAsync(CancellationToken ct) =>
        FusoDeNegocio.Resolver(await db.Empresas.AsNoTracking()
            .Select(x => x.FusoHorario).FirstOrDefaultAsync(ct));
}
