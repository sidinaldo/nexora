namespace Nexora.Core.Servicos;

/// <summary>O payload BARATO do shell: so o que muda o tempo todo e cabe num polling.
///
/// Separado do payload rico do dashboard de proposito — o desenho vem do Recupera, que tem um
/// `status()` enxuto para o poll de 45s e um `dashboard()` caro sob demanda. Sem essa separacao,
/// o poll do badge carregaria funil, series e agregacoes a cada 45 segundos.</summary>
public record StatusPainel(
    int NaoLidas,
    int Aguardando,
    // ===================== O BANNER COM N NUMEROS (ARQ-2) =====================
    // `WhatsappConectado` e falso quando ALGUMA conexao ja pareada esta fora do ar — nao quando
    // todas estao. Com dois numeros, exigir que os dois caiam para avisar significa que o
    // vendedor digita resposta num numero morto enquanto o painel diz que esta tudo bem.
    //
    // `ConexoesCaidas` traz os NOMES para o banner dizer QUAL caiu. Uma empresa com tres numeros
    // recebendo "WhatsApp desconectado" e um aviso que nao diz o que fazer.
    //
    // Conexao que NUNCA foi pareada nao entra: ela nao caiu, ela ainda nao subiu — e quem diz
    // isso e a tela de conexao, nao um alerta vermelho no topo de todas as telas.
    // ==========================================================================
    bool WhatsappConectado,
    IReadOnlyList<string> ConexoesCaidas,
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
    IReadOnlyList<DateOnly> FeriadosRecentes,
    // ===================== O AVISO DE RECUPERACAO (REC-1) =====================
    // Viaja no payload BARATO de proposito: ele ja roda em polling de 45s, entao o aviso aparece
    // sozinho quando as mensagens atrasadas entram — sem endpoint novo e sem o vendedor precisar
    // recarregar a pagina para descobrir que dez conversas mudaram debaixo dele.
    //
    // NULL = nao houve queda nas ultimas 24h. Ele SOME sozinho quando a janela passa: nao ha flag
    // de "dispensado" para alguem esquecer de limpar, pelo mesmo motivo do checklist de primeiros
    // passos — estado do sistema se deriva.
    // ==========================================================================
    AvisoRecuperacao? Recuperacao);

public interface IServicoPainel
{
    Task<StatusPainel> StatusAsync(CancellationToken ct);
}
