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
/// Três tentativas. Repetir para sempre transforma um receptor quebrado numa fila que só cresce —
/// e no dia em que ele volta, recebe semanas de eventos velhos de uma vez, o que costuma ser pior
/// que não receber. Depois da terceira a linha vira `falhou` e fica no registro, e o dono reenvia
/// à mão o que importar.
///
/// O espaçamento cobre as três falhas reais e distintas: o deploy do cliente (1 min), a queda
/// curta (5 min), e a manutenção (30 min). Além disso não é mais intermitência — é o sistema dele
/// fora do ar, e esperar não resolve.
/// ==============================================================</summary>
public static class PoliticaEntrega
{
    public const int MaximoTentativas = 3;

    /// <summary>Timeout de CADA tentativa. Curto de propósito: receptor que passa de 10s não vai
    /// melhorar esperando 60, e a espera segura um slot da rodada que outros eventos precisam.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan[] Espera =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30)
    ];

    /// <summary>Quantos dias de registro ficam. Depois disso a linha é expurgada na rodada
    /// diária — sem isso a tabela cresce para sempre, e a de maior volume aqui é
    /// `mensagem.recebida`.</summary>
    public const int DiasDeRetencao = 30;

    /// <summary>Quando tentar de novo depois de `tentativasFeitas` falhas, ou NULL quando acabou.
    ///
    /// `tentativasFeitas` é a contagem DEPOIS de incrementar: 1 falha → daqui a 1 min;
    /// 3 falhas → null, e a linha vira `falhou`.</summary>
    public static TimeSpan? EsperaApos(int tentativasFeitas) =>
        tentativasFeitas >= 1 && tentativasFeitas < MaximoTentativas
            ? Espera[tentativasFeitas - 1]
            : null;

    /// <summary>O receptor aceitou? Qualquer 2xx serve — exigir 200 quebraria com quem responde
    /// 202 (aceito para processar depois), que é a resposta correta de um receptor assíncrono.</summary>
    public static bool Aceitou(int codigo) => codigo >= 200 && codigo < 300;
}
