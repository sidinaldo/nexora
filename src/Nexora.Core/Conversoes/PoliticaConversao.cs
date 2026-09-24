namespace Nexora.Core.Conversoes;

/// <summary>O que fazer com uma tentativa que falhou.
///
/// `DesativarCredencial` é diferente de `Desistir`: o primeiro desliga o envio da empresa inteira,
/// porque insistir com token morto são três linhas idênticas na tabela e zero informação para quem
/// vai ler. O `Motivo` vai para a TELA, em português.</summary>
public record DecisaoMeta(bool TentarDeNovo, bool DesativarCredencial, string? Motivo = null)
{
    public static readonly DecisaoMeta Tentar = new(true, false);
    public static readonly DecisaoMeta Desistir = new(false, false);
}

/// <summary>QUANDO tentar de novo, quando parar, e quando desligar a credencial (INT-4).
///
/// ===================== POR QUE O RITMO É MAIS LENTO QUE O DO WEBHOOK =====================
/// O webhook drena 200 a cada 30 s porque do outro lado há um sistema esperando: o pedido tem de
/// aparecer no ERP antes de o cliente ir olhar.
///
/// Aqui não há ninguém esperando. A Meta atribui pelo `event_time` do FATO, não pela hora em que a
/// requisição chegou — **atrasar não custa atribuição**. Então 50 a cada 60 s: menos pressão na API
/// de terceiro, menos chance de bater em limite de taxa, e nenhum custo de produto.
/// ======================================================================================
///
/// O backoff é o mesmo do webhook (1/5/30 min) de propósito: as três falhas reais são as mesmas —
/// a intermitência, a queda curta e a manutenção.</summary>
public static class PoliticaConversao
{
    public const int MaximoTentativas = 3;

    /// <summary>A janela da Meta. Não é escolha nossa: evento com `event_time` mais velho que isto
    /// faz ela recusar a **requisição inteira**.</summary>
    public const int DiasDeValidade = 7;

    /// <summary>Quantos dias de registro ficam, como na fila de webhooks.</summary>
    public const int DiasDeRetencao = 30;

    /// <summary>Timeout por tentativa. Mais folgado que os 10 s do webhook: aquele é o servidor do
    /// cliente, este é a Graph API — e uma chamada que demora 15 s ainda vai ser aceita, enquanto
    /// desistir cedo custa uma tentativa das três.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Quantos por rodada, e de quanto em quanto tempo.</summary>
    public const int MaximoPorRodada = 50;
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan[] Espera =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30)
    ];

    /// <summary>Quando tentar de novo depois de `tentativasFeitas` falhas, ou NULL quando acabou.</summary>
    public static TimeSpan? EsperaApos(int tentativasFeitas) =>
        tentativasFeitas >= 1 && tentativasFeitas < MaximoTentativas
            ? Espera[tentativasFeitas - 1]
            : null;

    /// <summary>===================== O CÓDIGO DA META DECIDE, NÃO O HTTP =====================
    ///
    /// Ela responde **200 com `error` dentro do corpo** em vários casos, e 400 com o mesmo
    /// `error.code` em outros. Classificar pelo status HTTP daria "sucesso" para evento recusado.
    ///
    /// O que muda de verdade por código:
    ///
    ///   • `190` / `102` — token inválido ou expirado. Insistir 3× são três linhas idênticas e zero
    ///     informação; e sem a desativação, a credencial ficaria "ativa" enquanto nada sai, que é o
    ///     pior estado possível para quem olha a tela;
    ///   • `200` / `10` / `272` — o token não tem a permissão necessária. Mesma coisa: só um token
    ///     novo resolve;
    ///   • `100` — parâmetro inválido. É o nosso payload, ou o `event_time` fora da janela. Retentar
    ///     o mesmo corpo dá o mesmo erro;
    ///   • `1`, `2`, `4`, `17`, `32`, `341`, `613` — a Meta com problema ou limitando taxa. Aí sim
    ///     esperar resolve, e é para isso que o backoff existe;
    ///   • sem código (rede, DNS, timeout) — transitório por definição.
    ///
    /// O desconhecido TENTA DE NOVO: três tentativas custam pouco, e um código que ainda não
    /// existia quando este arquivo foi escrito é mais provavelmente transitório do que permanente.
    /// ==============================================================================</summary>
    public static DecisaoMeta Classificar(int? codigoMeta) => codigoMeta switch
    {
        190 or 102 => new DecisaoMeta(false, true,
            "A Meta recusou o token. Gere um novo em Gerenciador de Eventos → Configurações e "
            + "cole aqui."),

        200 or 10 or 272 => new DecisaoMeta(false, true,
            "O token não tem permissão para enviar eventos deste pixel. Gere um novo com a "
            + "permissão de Conversões."),

        // Parâmetro inválido: não desativa a credencial — o problema é DESTE evento, não do token.
        100 => DecisaoMeta.Desistir,

        1 or 2 or 4 or 17 or 32 or 341 or 613 => DecisaoMeta.Tentar,

        _ => DecisaoMeta.Tentar
    };
}
