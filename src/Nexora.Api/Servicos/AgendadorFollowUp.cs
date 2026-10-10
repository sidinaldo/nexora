using Nexora.Core.FollowUp;
using Nexora.Core.Nps;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;
using Nexora.Infra.Conversoes;
using Nexora.Infra.Webhooks;

namespace Nexora.Api.Servicos;

public class OpcoesAgendador
{
    /// <summary>Hora LOCAL (0-23), no fuso de negócio, das tarefas DIÁRIAS: feriados, expurgos,
    /// conclusão automática e o resumo por e-mail. As automações (follow-up e pesquisa) rodam de
    /// hora em hora, e cada empresa recebe no horário dela — ver `EnvioAutomatico`.</summary>
    public int HoraDaRodada { get; set; } = 8;

    /// <summary>Roda uma vez no boot. SÓ para desenvolvimento — em produção deixe false, senão
    /// todo deploy dispara follow-up.</summary>
    public bool RodarNoBoot { get; set; }

    /// <summary>Fuso do AGENDAMENTO. Cada empresa ainda tem o seu (usado dentro da rodada); este
    /// decide só a que horas o job acorda.</summary>
    public string FusoHorario { get; set; } = FusoDeNegocio.PadraoBrasil;
}

/// <summary>A rodada das automações, DE HORA EM HORA (BUG-XX), e as tarefas diárias às `HoraDaRodada`.
///
/// ⚠️ ERA UMA RODADA SÓ, ÀS 8H DE BRASÍLIA, e ela só postava se a empresa estivesse no horário
/// naquela hora: quem abria às 9h, ou ficava em outro fuso, nunca recebia follow-up. Agora a rodada
/// passa toda hora cheia, e cada empresa recebe na primeira hora em que pode (`EnvioAutomatico`).
///
/// É SEGURO rodar mais de uma vez: as invariantes de banco (teto diário, uq_msg_lembrete,
/// uq_msg_nps) impedem lembrete e mensagem duplicados, e a drenagem só pega o que não saiu. E uma
/// rodada por vez: `TravaDaRodada` (advisory lock) faz a segunda instância pular a hora.</summary>
public class AgendadorFollowUp(
    IServiceProvider provedor,
    OpcoesAgendador opcoes,
    TimeProvider relogio,
    ILogger<AgendadorFollowUp> log) : BackgroundService
{
    private readonly TimeZoneInfo _fuso = FusoDeNegocio.Resolver(opcoes.FusoHorario);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Semeia os feriados no boot para a primeira rodada já ter calendário.
        await GarantirFeriadosAsync(ct);

        if (opcoes.RodarNoBoot) await RodarAsync(diaria: true, ct);

        while (!ct.IsCancellationRequested)
        {
            var (espera, hora) = AteAProximaHora();

            try { await Task.Delay(espera, relogio, ct); }
            catch (OperationCanceledException) { break; }

            await RodarAsync(diaria: hora == opcoes.HoraDaRodada, ct);
        }
    }

    /// <summary>Com a trava, ou nada: outra rodada em andamento (outra instância, ou esta ainda na
    /// hora anterior) faz esta hora pular — a próxima tenta de novo.</summary>
    private async Task RodarAsync(bool diaria, CancellationToken ct)
    {
        try
        {
            using var escopoDaTrava = provedor.CreateScope();
            await using var trava = await TravaDaRodada.TentarAsync(
                escopoDaTrava.ServiceProvider.GetRequiredService<NexoraDbContext>(), ct);

            if (trava is null)
            {
                log.LogInformation("Outra rodada de automações está em andamento — esta hora pula.");
                return;
            }

            await RodarComTravaAsync(diaria, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Registrar(ex, "Não foi possível pegar a trava da rodada. O agendador segue de pé.");
        }
    }

    private async Task RodarComTravaAsync(bool diaria, CancellationToken ct)
    {
        try
        {
            // O motor é Scoped (usa DbContext); este BackgroundService é Singleton.
            using var escopo = provedor.CreateScope();

            // Garante os feriados do ano (idempotente) — cobre a virada de ano sem ninguém
            // lembrar de rodar nada em janeiro.
            if (diaria)
                await escopo.ServiceProvider.GetRequiredService<IServicoFeriados>()
                    .GarantirAtualEProximoAsync(ct);

            await escopo.ServiceProvider.GetRequiredService<MotorFollowUp>().ExecutarAsync(ct);

            if (diaria)
                await TarefasDiariasAsync(escopo.ServiceProvider, ct);

            // ===== E A PESQUISA PÓS-VENDA, POR ÚLTIMO (NPS-1) =====
            // ⚠️ DEPOIS DA CONCLUSÃO AUTOMÁTICA, e a ordem é a regra: é ela que produz as vendas
            // concluídas de hoje, e o agendamento do NPS trabalha procurando venda concluída sem
            // pesquisa. Antes dela, a venda que acabou de fechar sozinha só seria agendada amanhã.
            await escopo.ServiceProvider.GetRequiredService<MotorNps>().ExecutarAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NUNCA deixar a exceção subir: ela derrubaria o BackgroundService, e o follow-up
            // pararia de rodar EM SILÊNCIO até o próximo deploy. Não é zelo excessivo — é o
            // modo de falha mais caro deste componente.
            Registrar(ex, "A rodada de follow-up falhou. O agendador segue de pé.");
        }

        // ===== O RESUMO DE ONTEM, PARA O DONO (RES-XX) =====
        // FORA do try de cima, com o seu: o follow-up falhar não pode calar o e-mail, nem o contrário.
        if (diaria)
            await EnviarResumosAsync(ct);
    }

    /// <summary>O trabalho DIÁRIO, uma vez por dia às `HoraDaRodada`. Entre o follow-up e a
    /// pesquisa, na ordem de sempre.</summary>
    private async Task TarefasDiariasAsync(IServiceProvider servicos, CancellationToken ct)
    {
        // ===== O EXPURGO DE ENTREGAS DE WEBHOOK MORA AQUI (INT-3) =====
        // Ele é trabalho DIÁRIO, e esta é a rodada diária. Pendurá-lo no agendador de webhooks
        // — que acorda a cada 30s — obrigaria a inventar um controle de "já rodei hoje", que é
        // exatamente o problema que este agendador já resolve.
        //
        // DEPOIS do follow-up e dentro do mesmo try: se o expurgo falhar, o agendador segue de
        // pé e a rodada de amanhã tenta de novo. Registro velho não é urgência.
        await servicos.GetRequiredService<MotorWebhooks>().ExpurgarAntigasAsync(ct);

        // ===== E O DE CONVERSOES, NA MESMA RODADA (INT-4) =====
        // Mesma razao, e vale registrar o que NAO e expurgado aqui: o RASTRO do lead
        // (`rastreios_lead`) nao tem retencao de 30 dias. A venda pode fechar em tres meses, e
        // o `Purchase` precisa do `fbc` do clique original — ele morre com a anonimizacao do
        // contato, nao com o calendario.
        await servicos.GetRequiredService<MotorConversoes>()
            .ExpurgarAntigosAsync(ct);

        // ===== E O DA TRILHA JUNTO (AUD-1) =====
        // Mesma rodada, mesmo try, mesma razão: é trabalho diário e não merece um
        // BackgroundService próprio — que precisaria reimplementar estas mesmas proteções
        // (catch que não deixa exceção subir, log protegido, fuso de negócio).
        var apagadas = await ExpurgoTrilha.ExpurgarAsync(
            servicos.GetRequiredService<NexoraDbContext>(),
            servicos.GetRequiredService<TimeProvider>(), ct);

        if (apagadas > 0)
            log.LogInformation("Expurgo da trilha: {N} eventos além da retenção.", apagadas);

        // ===== E A CONCLUSÃO AUTOMÁTICA DE VENDA (NEG-2) =====
        // Terceiro trabalho diário nesta mesma rodada, pelo mesmo motivo dos outros dois: um
        // `BackgroundService` próprio teria de reimplementar as proteções que já existem aqui
        // (o catch que não deixa exceção subir, o log protegido, o fuso de negócio).
        //
        // DEPOIS do follow-up: se o prazo de conclusão passasse antes, uma venda concluída
        // hoje poderia sumir do Meu Dia do vendedor antes de ele abrir a tela.
        var concluidas = await ConclusaoAutomatica.ExecutarAsync(
            servicos.GetRequiredService<NexoraDbContext>(),
            servicos.GetRequiredService<TimeProvider>(), ct);

        if (concluidas > 0)
            log.LogInformation("Conclusão automática: {N} vendas além do prazo.", concluidas);
    }

    /// <summary>⚠️ UM ESCOPO POR EMPRESA. O motor assume a empresa (ver `ContextoDeFundo`), e o
    /// `DbContext` e os serviços do escopo passam a ser dela — a próxima tem de começar limpa. E
    /// uma empresa com dado ruim não impede o resumo das outras.</summary>
    private async Task EnviarResumosAsync(CancellationToken ct)
    {
        IReadOnlyList<long> empresas;
        try
        {
            using var escopo = provedor.CreateScope();
            empresas = await escopo.ServiceProvider.GetRequiredService<MotorResumoDiario>().EmpresasAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Registrar(ex, "Não foi possível listar as empresas do resumo diário.");
            return;
        }

        foreach (var id in empresas)
        {
            try
            {
                using var escopo = provedor.CreateScope();
                await escopo.ServiceProvider.GetRequiredService<MotorResumoDiario>().EnviarAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Registrar(ex, $"O resumo diário da empresa {id} falhou. As outras seguem.");
            }
        }
    }

    private async Task GarantirFeriadosAsync(CancellationToken ct)
    {
        try
        {
            using var escopo = provedor.CreateScope();
            await escopo.ServiceProvider.GetRequiredService<IServicoFeriados>()
                .GarantirAtualEProximoAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Registrar(ex, "Falha ao semear feriados no boot. Seguindo.");
        }
    }

    /// <summary>Log que NÃO pode lançar.
    ///
    /// ===================== CUSTOU UM DIAGNÓSTICO DE VERDADE =====================
    /// O `catch` acima protege a operação, mas o `LogError` DENTRO dele não estava protegido — e
    /// ele lança. No Windows, o provider de EventLog estoura ObjectDisposedException quando a
    /// aplicação já está desligando; a exceção sai do catch, sobe pelo ExecuteAsync e derruba o
    /// BackgroundService. Ou seja: o mecanismo que existe para o serviço NUNCA cair era
    /// exatamente por onde ele caía.
    ///
    /// O Console.Error é o último recurso: se nem ele funcionar, engolimos. Perder uma linha de
    /// log é aceitável; perder o agendador não é.
    /// ============================================================================</summary>
    private void Registrar(Exception ex, string mensagem)
    {
        try { log.LogError(ex, "{Mensagem}", mensagem); }
        catch
        {
            try { Console.Error.WriteLine($"[follow-up] {mensagem} {ex}"); } catch { /* nada a fazer */ }
        }
    }

    /// <summary>Tempo até a próxima HORA CHEIA, e que hora ela é no fuso de negócio — é por ela que
    /// as tarefas diárias sabem que chegou a vez delas. O Brasil não tem horário de verão desde
    /// 2019, então a diferença de parede é igual à duração real do Delay.
    ///
    /// Os 5 segundos a mais são margem: um Delay que acorde um instante antes da hora cheia faria a
    /// empresa ainda estar "às 7h" para `EnvioAutomatico`, e ela perderia a hora.</summary>
    private (TimeSpan Espera, int Hora) AteAProximaHora()
    {
        var agora = FusoDeNegocio.AgoraNo(relogio, _fuso);
        var proxima = new DateTime(agora.Year, agora.Month, agora.Day, agora.Hour, 0, 0).AddHours(1);
        return (proxima - agora + TimeSpan.FromSeconds(5), proxima.Hour);
    }
}
