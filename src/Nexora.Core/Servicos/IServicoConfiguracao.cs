namespace Nexora.Core.Servicos;

/// <summary>Tudo que a empresa pode ajustar, numa leitura só.
///
/// Cada campo aqui MUDA COMPORTAMENTO em algum lugar do sistema — se não muda, não é
/// configuração e não entra nesta tela.</summary>
public record ConfiguracaoEmpresa(
    string Nome,
    string? Documento,
    string FusoHorario,
    /// <summary>Sigla da UF. Só serve para semear os feriados ESTADUAIS; nula = só nacionais.</summary>
    string? Uf,

    /// <summary>Governa quando o follow-up dispara e quando o semáforo acende.</summary>
    short JanelaHoraInicio,
    short JanelaHoraFim,
    /// <summary>Bitmask por DayOfWeek do .NET: bit 0 = domingo … bit 6 = sábado. 126 = seg a sáb.</summary>
    short JanelaDiasSemana,

    /// <summary>Minutos ÚTEIS até o amarelo e até o vermelho. Zero DESLIGA a faixa — é
    /// comportamento legítimo, não erro.</summary>
    short SemaforoAmareloMinutos,
    short SemaforoVermelhoMinutos,

    /// <summary>Dias de conversa parada até o follow-up automático. Mínimo 1.</summary>
    short DiasSemRespostaFollowUp,

    /// <summary>Dias até a venda ser concluída sozinha (NEG-2). **ZERO = concluir na hora**, e é
    /// valor legítimo: padaria, salão, loja de balcão — a venda nasce e termina no mesmo
    /// atendimento.</summary>
    short DiasParaConcluirVenda);

/// <summary>`FusoHorario` e `Uf` entram aqui, com os dados cadastrais, e não na tela de
/// atendimento: são identidade da empresa, não regra de operação. E o fuso, diferente da janela,
/// não tem efeito retroativo nenhum sobre o que já foi agendado — ver a nota em
/// `AtualizarDadosAsync`.</summary>
public record EditarDadosEmpresa(string Nome, string? Documento, string FusoHorario, string? Uf);

public record EditarAtendimento(
    short JanelaHoraInicio,
    short JanelaHoraFim,
    short JanelaDiasSemana,
    short SemaforoAmareloMinutos,
    short SemaforoVermelhoMinutos,
    short DiasSemRespostaFollowUp,
    short DiasParaConcluirVenda);

public interface IServicoConfiguracao
{
    /// <summary>LEITURA é para qualquer papel: o vendedor precisa saber que horas a empresa
    /// atende. Quem ALTERA é só o dono, e isso é enforcement do controller.</summary>
    Task<ConfiguracaoEmpresa> ObterAsync(CancellationToken ct);

    /// <summary>=========== O FUSO NÃO REPROCESSA NADA ===========
    /// Trocar o fuso muda o que "agora" significa da PRÓXIMA rodada em diante. Lembrete já
    /// reservado mantém a data-alvo e a mensagem já reservada mantém o `data_disparo` — os dois
    /// foram carimbados com o fuso antigo e continuam valendo. A tela avisa isso.
    ///
    /// Reprocessar seria reescrever a data de coisa já decidida, e o vendedor veria follow-up
    /// mudando de dia sozinho, sem nada na tela explicando por quê.
    /// ==================================================</summary>
    Task AtualizarDadosAsync(EditarDadosEmpresa dados, CancellationToken ct);

    /// <summary>Os fusos oferecidos na tela. Lista FECHADA: campo livre aceitaria id inexistente,
    /// e o `FusoDeNegocio.Resolver` cairia no fallback UTC-3 EM SILÊNCIO — a rodada dispararia na
    /// hora errada sem nenhum erro para investigar.</summary>
    IReadOnlyList<FusoDisponivel> FusosDisponiveis();

    /// <summary>=========== O QUE MUDA E QUANDO ===========
    ///
    /// A JANELA e os DIAS DE INATIVIDADE valem da PRÓXIMA rodada em diante. Lembrete já criado
    /// mantém a data-alvo que recebeu, e mensagem já reservada mantém o `data_disparo` — mudar a
    /// configuração NÃO reprocessa o que já foi carimbado. Reprocessar significaria reescrever
    /// data de coisa já decidida, e o vendedor perderia a noção de por que aquele follow-up
    /// mudou de dia sozinho.
    ///
    /// As FAIXAS DO SEMÁFORO valem na hora: a cor é calculada no cliente a partir do timestamp,
    /// e o painel relê os limites no próximo /api/painel/status (no máximo 45s depois).
    /// ============================================</summary>
    Task AtualizarAtendimentoAsync(EditarAtendimento dados, CancellationToken ct);
}

/// <summary>Um fuso oferecido na tela, já validado contra o host.</summary>
public record FusoDisponivel(string Id, string Rotulo, string OffsetAtual);

public static class ConfiguracaoRef
{
    /// <summary>As UFs do Brasil. Serve à validação e ao select da tela.</summary>
    public static readonly IReadOnlyList<string> Ufs =
    [
        "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG",
        "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC", "SP", "SE", "TO"
    ];

    /// <summary>Os fusos do Brasil, do mais usado ao menos. Quatro no total: o país tem UTC-2
    /// (Fernando de Noronha) a UTC-5 (Acre).</summary>
    public static readonly IReadOnlyList<(string Id, string Rotulo)> FusosBrasil =
    [
        ("America/Sao_Paulo",  "Brasília (UTC-3) — maior parte do país"),
        ("America/Manaus",     "Manaus (UTC-4) — AM, RR, RO, MT, parte do PA"),
        ("America/Rio_Branco", "Rio Branco (UTC-5) — AC e sudoeste do AM"),
        ("America/Noronha",    "Fernando de Noronha (UTC-2)")
    ];
}
