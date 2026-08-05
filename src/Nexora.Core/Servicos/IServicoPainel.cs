namespace Nexora.Core.Servicos;

/// <summary>O payload BARATO do shell: so o que muda o tempo todo e cabe num polling.
///
/// Separado do payload rico do dashboard de proposito — o desenho vem do Recupera, que tem um
/// `status()` enxuto para o poll de 45s e um `dashboard()` caro sob demanda. Sem essa separacao,
/// o poll do badge carregaria funil, series e agregacoes a cada 45 segundos.</summary>
public record StatusPainel(
    int NaoLidas,
    int Aguardando,
    bool WhatsappConectado,
    string? Numero,
    bool TrocouDeNumero,
    short SemaforoAmareloMinutos,
    short SemaforoVermelhoMinutos,
    // A JANELA DE ATENDIMENTO da empresa. Vai junto do semaforo porque o cliente e quem pinta a
    // cor, e a cor nao pode acender de madrugada: sem a janela, o navegador contaria as 12 horas
    // da noite como espera e toda conversa amanheceria vermelha.
    short JanelaHoraInicio,
    short JanelaHoraFim,
    short JanelaDiasSemana,
    // Os feriados dos ultimos 30 dias, para o desconto do tempo util no cliente. O navegador nao
    // tem como saber que a terca-feira foi feriado.
    IReadOnlyList<DateOnly> FeriadosRecentes);

public interface IServicoPainel
{
    Task<StatusPainel> StatusAsync(CancellationToken ct);
}
