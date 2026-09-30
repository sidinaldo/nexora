namespace Nexora.Core.Webhooks;

/// <summary>QUANDO tentar de novo, e quando parar.
///
/// ===================== POR QUE AQUI HÁ RETRY, E NO E-MAIL NÃO =====================
/// `IFilaSegundoPlano` faz UMA tentativa de e-mail e desiste, de propósito: do outro lado há uma
/// PESSOA, e existe caminho alternativo — o link continua na tela.
///
/// Aqui não há nenhum dos dois. O receptor é um sistema, ninguém vai olhar uma tela, e um evento
/// perdido é um pedido que nunca chegou ao ERP. Uma reinicialização do servidor do cliente não
/// pode custar a venda do dia.
/// ==================================================================================
///
/// ===================== E POR QUE ELE PARA =====================
/// Sete tentativas, ao longo de ~20 horas. Repetir para sempre transforma um receptor quebrado numa
/// fila que só cresce — e no dia em que ele volta, recebe semanas de eventos velhos de uma vez, o
/// que costuma ser pior que não receber. Depois da última a linha vira `falhou` e fica no registro,
/// e o dono reenvia à mão o que importar.
///
/// O espaçamento cobre as falhas reais, e cada degrau é uma delas: o deploy do cliente (1 min), a
/// queda curta (5 min), a manutenção (30 min), o incidente de meio período (2 h), o de um turno
/// (6 h) — e a noite inteira (12 h), que é o caso em que o servidor dele cai às 19h e alguém só
/// religa na manhã seguinte. Esse último degrau é o que o arranjo antigo, de 3 tentativas em 6
/// minutos, perdia inteiro.
/// ==============================================================</summary>
public static class PoliticaEntrega
{
    /// <summary>⚠️ SEIS ESPERAS, E NÃO SETE. Entre N tentativas cabem N-1 intervalos: a primeira
    /// sai na hora, e cada valor abaixo é o tempo ATÉ a seguinte.
    ///
    /// Essa conta já esteve errada aqui. O arranjo anterior declarava três esperas para três
    /// tentativas, e a terceira — 30 minutos — **nunca executou**: o índice máximo alcançável é
    /// `MaximoTentativas - 2`. A documentação, os comentários e a tela anunciavam "1min-5min-30min"
    /// enquanto o código entregava "1min-5min-desisti", e ninguém percebeu porque o teste afirmava
    /// só o que o código fazia.
    ///
    /// O `ConfereAInvariante` abaixo é o que impede isso de voltar.</summary>
    private static readonly TimeSpan[] Espera =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12)
    ];

    public const int MaximoTentativas = 7;

    /// <summary>Timeout de CADA tentativa. Curto de propósito: receptor que passa de 10s não vai
    /// melhorar esperando 60, e a espera segura um slot da rodada que outros eventos precisam.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Quantos dias de registro ficam. Depois disso a linha é expurgada na rodada
    /// diária — sem isso a tabela cresce para sempre, e a de maior volume aqui é
    /// `mensagem.recebida`.</summary>
    public const int DiasDeRetencao = 30;

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
    ///
    /// Falhar aqui é falhar alto: a aplicação não sobe, e quem mexeu descobre em segundos. A
    /// alternativa — o que acontecia antes — é um degrau de backoff que some em silêncio e só
    /// aparece meses depois, quando alguém for conferir por que a entrega desistiu cedo.</summary>
    /// <summary>Chamada no arranque, pelo `Program.cs`. Ver o construtor estático.</summary>
    public static void ConferirInvariante() { }

    static PoliticaEntrega()
    {
        if (Espera.Length != MaximoTentativas - 1)
            throw new InvalidOperationException(
                $"São {Espera.Length} esperas para {MaximoTentativas} tentativas; o certo é " +
                $"{MaximoTentativas - 1}. Entre N tentativas cabem N-1 intervalos.");
    }

    /// <summary>Quanto esperar depois de `tentativasFeitas` falhas, ou NULL quando acabou.
    ///
    /// `tentativasFeitas` é a contagem DEPOIS de incrementar: 1 falha → daqui a 1 min;
    /// 7 falhas → null, e a linha vira `falhou`.</summary>
    public static TimeSpan? EsperaApos(int tentativasFeitas) =>
        tentativasFeitas >= 1 && tentativasFeitas < MaximoTentativas
            ? Espera[tentativasFeitas - 1]
            : null;

    /// <summary>O tempo total coberto pelas tentativas — o número que a tela promete ao cliente.
    /// Derivado, e não escrito à mão, para não virar a próxima frase desatualizada.</summary>
    public static TimeSpan JanelaTotal => Espera.Aggregate(TimeSpan.Zero, (soma, e) => soma + e);

    /// <summary>O receptor aceitou? Qualquer 2xx serve — exigir 200 quebraria com quem responde
    /// 202 (aceito para processar depois), que é a resposta correta de um receptor assíncrono.</summary>
    public static bool Aceitou(int codigo) => codigo >= 200 && codigo < 300;
}
