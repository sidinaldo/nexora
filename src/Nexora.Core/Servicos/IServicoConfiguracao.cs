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
    short DiasParaConcluirVenda,

    /// <summary>A conclusão automática está ligada? (POS-1). O prazo acima só vale quando isto é
    /// verdadeiro, e o número é guardado mesmo desligado — é o que faz religar devolver o prazo
    /// antigo em vez de 7 por acidente.
    ///
    /// E o prazo conta SÓ enquanto o card está na etapa de venda: quem avançou para pós-venda está
    /// sendo trabalhado, e o relógio para.</summary>
    bool ConclusaoAutomatica,

    // ===================== A PESQUISA POS-VENDA (NPS-1) =====================
    /// <summary>Nasce DESLIGADA. Ligada por padrao, toda empresa existente comecaria a mandar
    /// mensagem automatica para os clientes dela no dia do deploy.</summary>
    bool NpsAtivo,

    /// <summary>Dias entre a conclusao da venda e a pergunta. Tres, e nao zero: perguntar no mesmo
    /// dia mede o ATENDIMENTO, nao o produto — o cliente ainda nao usou o que comprou. Zero segue
    /// legitimo para quem vende servico na hora.</summary>
    short NpsDiasAposConclusao,

    /// <summary>Dias esperando a nota antes de desistir. SEM REENVIO depois disso.</summary>
    short NpsDiasExpiracao,

    /// <summary>A pergunta. `{{saudacao}}`, `{{nome}}` e `{{empresa}}` sao substituidos no envio —
    /// ver `Empresa.NpsTexto` para por que o padrao usa `{{saudacao}}`.</summary>
    string NpsTexto,

    /// <summary>Agradecimentos OPCIONAIS. Vazio = nao envia, e e o padrao: uma segunda automatica
    /// depois da primeira dobra o risco do numero. ⚠️ A acao humana do detrator acontece de
    /// qualquer jeito — a mensagem e que e opcional.</summary>
    string? NpsMensagemPromotor,
    string? NpsMensagemDetrator);

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
    short DiasParaConcluirVenda,

    /// <summary>===================== POR QUE `bool?` E NÃO `bool` (POS-1) =====================
    /// Este PUT manda o documento INTEIRO, e um campo omitido no corpo desserializa para o default
    /// do tipo. Nos vizinhos `short` isso dá 0 — ruim, e o `Validar` já pega a maioria. Num `bool`
    /// dá `false`, que é um valor VÁLIDO: a conclusão automática seria desligada em silêncio, por
    /// um valor que ninguém escolheu, revertendo uma decisão que o dono tomou.
    ///
    /// Anulável, com o `Validar` recusando nulo, o campo omitido vira erro em vez de desligar a
    /// feature. Quebra a simetria com os vizinhos, e é o único campo onde o default do tipo
    /// desfaz configuração em vez de só ficar fora de faixa.
    /// ==============================================================================</summary>
    bool? ConclusaoAutomatica);

/// <summary>===================== A CONFIGURACAO DA PESQUISA =====================
///
/// Grupo proprio, e nao campos a mais em `EditarAtendimento`: aquele PUT manda o documento inteiro,
/// e misturar os dois faria quem salva o horario de atendimento reescrever o texto da pesquisa.
///
/// ⚠️ `bool?` EM `NpsAtivo`, pela MESMA razao do `ConclusaoAutomatica` ao lado (POS-1): campo
/// omitido no corpo desserializa para o default do tipo, e em `bool` isso da `false` — um valor
/// VALIDO. A pesquisa seria desligada em silencio, por um valor que ninguem escolheu. Anulavel, com
/// o `Validar` recusando nulo, o campo omitido vira erro em vez de desfazer configuracao.</summary>
public record EditarPesquisaNps(
    bool? NpsAtivo,
    short NpsDiasAposConclusao,
    short NpsDiasExpiracao,
    string NpsTexto,
    string? NpsMensagemPromotor,
    string? NpsMensagemDetrator);

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

    /// <summary>A configuracao da pesquisa pos-venda. Dono apenas.
    ///
    /// ⚠️ NAO REPROCESSA NADA, como a vizinha: pesquisa ja agendada mantem a `data_agendada` e a
    /// `data_limite` — elas congelam no nascimento justamente para a configuracao nao mover o
    /// limite de uma pesquisa viva. O texto novo vale no proximo ENVIO.
    ///
    /// ⚠️ ESTA E AUDITADA, e a vizinha de atendimento nao e. A assimetria e deliberada: isto decide
    /// mensagem SAINDO para cliente, e "quem mudou o texto que o cliente recebeu" e pergunta que
    /// aparece depois. Os limites do semaforo nao tem esse peso.</summary>
    Task AtualizarPesquisaNpsAsync(EditarPesquisaNps dados, CancellationToken ct);
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
