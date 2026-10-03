namespace Nexora.Core.Conversoes;

/// <summary>O que fazer com uma tentativa que falhou.
///
/// `DesativarCredencial` é diferente de `Desistir`: o primeiro desliga o envio da empresa inteira,
/// porque insistir com token morto são três linhas idênticas na tabela e zero informação para quem
/// vai ler. O `Motivo` vai para a TELA, em português.</summary>
public record DecisaoMeta(
    bool TentarDeNovo, bool DesativarCredencial, string? Motivo = null, bool Rebaixar = false)
{
    public static readonly DecisaoMeta Tentar = new(true, false);
    public static readonly DecisaoMeta Desistir = new(false, false);

    /// <summary>Tira o `business_messaging` do corpo e manda de novo como `chat`. Ver
    /// `PoliticaConversao.Classificar`.</summary>
    public static readonly DecisaoMeta RebaixarParaChat = new(true, false, Rebaixar: true);
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
/// O backoff cobre as mesmas três falhas reais do webhook (1, 5 e 30 min) de propósito —
/// a intermitência, a queda curta e a manutenção.</summary>
public static class PoliticaConversao
{
    public const int MaximoTentativas = 4;

    /// <summary>A janela da Meta. Não é escolha nossa: evento com `event_time` mais velho que isto
    /// faz ela recusar a **requisição inteira**.</summary>
    public const int DiasDeValidade = 7;

    /// <summary>Quantos dias de registro ficam, como na fila de webhooks.</summary>
    public const int DiasDeRetencao = 30;

    /// <summary>===================== ATE QUANDO A LISTA DE NAO ENVIADAS SABE (INT-5) =====================
    ///
    /// A lista de vendas sem conversao e DERIVADA: venda fechada que nao tem evento. So que o
    /// expurgo apaga evento com mais de `DiasDeRetencao` dias — e a partir dali "nunca foi
    /// enfileirada" e "foi enfileirada, ENTREGUE, e a linha foi apagada" viram a mesma observacao.
    ///
    /// Alem da janela a tela acusaria o produto de perder venda que ele entregou. Numa tela cujo
    /// unico trabalho e ser confiavel, isso e pior do que nao mostrar.
    ///
    /// ⚠️ 21 E NAO 30, E A FOLGA E O PONTO. Em 30 o caso de borda ja acontece: a venda exatamente
    /// no limite tem o evento apagado no mesmo dia em que a lista ainda a olha. A invariante abaixo
    /// escreve a folga como conta, nao como comentario: `21 + 7 <= 30` quer dizer "a venda da ponta
    /// da lista, dispensada no ultimo instante do prazo, ainda tem a linha dela antes do expurgo".
    /// =========================================================================================</summary>
    public const int DiasDaListaDeNaoEnviadas = 21;

    /// <summary>Timeout por tentativa. Mais folgado que os 10 s do webhook: aquele é o servidor do
    /// cliente, este é a Graph API — e uma chamada que demora 15 s ainda vai ser aceita, enquanto
    /// desistir cedo custa uma tentativa das três.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Quantos por rodada, e de quanto em quanto tempo.</summary>
    public const int MaximoPorRodada = 50;
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(60);

    /// <summary>⚠️ TRÊS ESPERAS PARA QUATRO TENTATIVAS. Entre N tentativas cabem N-1 intervalos: a
    /// primeira sai na hora, e cada valor abaixo é o tempo ATÉ a seguinte.
    ///
    /// Esta conta já esteve errada aqui, copiada junto com o backoff do webhook: eram três esperas
    /// para três tentativas, e a de 30 minutos **nunca executou** — o índice máximo alcançável é
    /// `MaximoTentativas - 2`. O comentário acima e o `docs/INT-4.md` anunciavam "1/5/30" enquanto
    /// o motor entregava "1min, 5min, desisti".
    ///
    /// O `ConfereAInvariante` abaixo é o que impede isso de voltar.</summary>
    private static readonly TimeSpan[] Espera =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30)
    ];

    /// <summary>⚠️ ESTOURA NO ARRANQUE — mas só porque o `Program.cs` chama
    /// `ConferirInvariante()` de propósito.
    ///
    /// Sem essa chamada isto NÃO rodava no boot, e a revisão pegou: `MaximoTentativas` é
    /// `const`, então o compilador o embute no chamador e ler seu valor nunca toca a classe.
    /// O construtor estático só dispararia no primeiro acesso a um CAMPO — dentro da rodada
    /// do motor, onde o `try/catch` do agendador engole a exceção e tenta de novo a cada 30 s.
    ///
    /// Ou seja: a entrega ficaria 100% quebrada com a aplicação se reportando saudável — o
    /// exato silêncio que esta guarda existe para impedir.
    /// Falhar aqui é falhar alto; a alternativa é um degrau que some em silêncio.</summary>
    /// <summary>Chamada no arranque, pelo `Program.cs`. Ver o construtor estático.</summary>
    public static void ConferirInvariante() { }

    static PoliticaConversao()
    {
        if (Espera.Length != MaximoTentativas - 1)
            throw new InvalidOperationException(
                $"São {Espera.Length} esperas para {MaximoTentativas} tentativas; o certo é " +
                $"{MaximoTentativas - 1}. Entre N tentativas cabem N-1 intervalos.");

        // ⚠️ AQUI DENTRO, e não como `const` solto: `const` o compilador embute no chamador, e ler
        // o valor nunca tocaria esta classe — a guarda existiria e nunca rodaria. É a mesma lição
        // que o comentário do `ConferirInvariante()` acima registra.
        if (DiasDaListaDeNaoEnviadas + DiasDeValidade > DiasDeRetencao)
            throw new InvalidOperationException(
                $"A lista de não enviadas olha {DiasDaListaDeNaoEnviadas} dias e o evento vale "
              + $"{DiasDeValidade}, mas o registro só fica {DiasDeRetencao}. A lista passaria a "
              + "acusar de perdida uma venda cujo evento foi entregue e depois expurgado.");
    }

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
    public static DecisaoMeta Classificar(int? codigoMeta, int? subcodigo = null)
    {
        // ===================== O SUBCÓDIGO DECIDE ANTES DO CÓDIGO =====================
        // Os três vêm com `code: 100` — "parâmetro inválido" —, e `100` sozinho manda DESISTIR.
        // Desistir aqui joga fora uma conversão que sairia perfeitamente bem como `chat`.
        //
        // É a mesma regra que o `MontadorEventoMeta.Origem` já aplica na ida: **um evento recusado
        // vale menos que um aceito com casamento mais grosso.** Lá ela vale para dado AUSENTE; aqui
        // passa a valer para dado RECUSADO, que é a metade que faltava.
        //
        // Descobertos falando com a Graph API de verdade — a documentação não traz nenhum deles.
        // ==============================================================================
        // ⚠️ E SÓ COM `codigoMeta == 100`. Sem essa condição o subcódigo passava na frente de
        // TUDO — inclusive de `190` (token morto), que a Meta não tem razão nenhuma para não
        // reportar junto num evento de Clique-para-WhatsApp.
        //
        // O resultado seria o pior estado possível, e é o que o `MotorConversoes` existe para
        // evitar: a credencial nunca marcada como morta, `DesativadaMotivo` nunca chegando à
        // tela, e o motor queimando a escada de tentativas num token que não vai funcionar —
        // "ativa na tela enquanto nada sai".
        if (codigoMeta is 100 && subcodigo is CliqueInvalido or PaginaAusente or PaginaInvalida)
            return DecisaoMeta.RebaixarParaChat;

        return codigoMeta switch
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

    /// <summary>===================== OS TRÊS SUBCÓDIGOS DO CLIQUE-PARA-WHATSAPP =====================
    /// Os três significam a mesma coisa para nós: **este evento não pode sair como
    /// `business_messaging`** — e os três são resolvidos pelo mesmo rebaixamento.
    ///
    ///   • `2804087` — o `ctwa_clid` é inválido. Ele expira, e o motor pode tentar até 7 dias
    ///     depois do fato;
    ///   • `2804116` — falta a página. É o cliente que ainda não vinculou;
    ///   • `2804070` — a página é inválida. É o cliente que digitou o número errado.
    ///
    /// ⚠️ Os dois últimos são configuração ERRADA, não dado expirado — por isso o rebaixamento
    /// grita no log em vez de acontecer em silêncio. Rebaixar calado transformaria "a página está
    /// errada" em "a atribuição está mais grossa e ninguém sabe por quê".
    /// ===================================================================================</summary>
    public const int CliqueInvalido = 2804087;
    public const int PaginaAusente = 2804116;
    public const int PaginaInvalida = 2804070;
}
