namespace Nexora.Core.Servicos;

/// <summary>Um mês da conversão de alguém.
///
/// `Conversao` é NULO quando não houve nada decidido — e nulo não é zero: zero por cento diz "ele
/// tentou e não fechou", e ausência diz "não houve o que medir". Pintar as duas iguais faria férias
/// parecer fracasso.</summary>
public record MesDaConversao(
    int Ano,
    int Mes,
    int Decididos,
    int Ganhos,
    double? Conversao,
    bool Parcial,
    bool AmostraInsuficiente);

/// <summary>A evolução de uma pessoa — ou da equipe inteira, que usa o MESMO tipo porque é a mesma
/// conta sobre todo mundo junto.</summary>
public record EvolucaoDoVendedor(
    long? UsuarioId,
    string Nome,
    DateTime? NoNexoraDesde,
    int Decididos,
    int Ganhos,
    double? Conversao,
    double? VariacaoPontos,
    string Tendencia,
    IReadOnlyList<MesDaConversao> Meses);

/// <summary>O que a tela recebe: a régua primeiro, as pessoas depois.</summary>
public record EvolucaoDaEquipe(
    EvolucaoDoVendedor? Equipe,
    IReadOnlyList<EvolucaoDoVendedor> Pessoas,
    DateOnly De,
    DateOnly Ate);

/// <summary>===================== QUANDO UM MÊS CONTA, E QUANDO A TENDÊNCIA EXISTE =====================
///
/// Função pura, sem banco: são as regras que decidem se um número é SINAL ou RUÍDO, e elas precisam
/// ser exercitadas nos limites exatos sem montar cenário de Postgres.
///
/// ⚠️ DUAS EXCLUSÕES, E AS DUAS SÃO SOBRE HONESTIDADE:
///
///   · MÊS EM ANDAMENTO não entra. Ele está pela metade, e metade de um mês comparada com meses
///     inteiros inventa uma queda todo dia 5.
///   · AMOSTRA PEQUENA não entra. "2 de 3 = 67%" é aritmética correta e sinal nenhum — e, num
///     relatório que julga pessoas, premiar ou punir alguém por três negócios é pior que não dizer
///     nada. O mês continua VISÍVEL com o volume ao lado; ele só não vota.
///
/// ⚠️ A VARIAÇÃO É EM PONTOS PERCENTUAIS, não em porcentagem sobre a porcentagem. De 25% para 30%
/// são +5 p.p.; chamar isso de "+20%" está certo na conta e lê errado na tela.
/// ============================================================================================</summary>
public static class RegrasTendencia
{
    /// <summary>Abaixo disto o mês não vota. Dez decididos é pouco para estatística e é o bastante
    /// para não ser anedota — e o número está aqui, num lugar só, para ser discutido em vez de
    /// aparecer espalhado em três consultas.</summary>
    public const int AmostraMinima = 10;

    /// <summary>A faixa morta, em pontos percentuais. Sem ela, três décimos de ponto viram "está
    /// piorando" e o selo pisca de cor a cada mês sem nada ter acontecido.</summary>
    public const double FaixaEstavel = 3d;

    /// <summary>Quantos meses válidos a tendência precisa para existir.</summary>
    public const int MinimoDeMesesValidos = 4;

    /// <summary>A conversão, num lugar só.
    ///
    /// ⚠️ NULO E NÃO ZERO quando nada foi decidido — ver o comentário de `MesDaConversao`. O
    /// relatório antigo devolve `0` nos dois casos, e é a diferença que faz um mês de férias
    /// aparecer como um mês de fracasso.</summary>
    public static double? Conversao(int ganhos, int decididos) =>
        decididos == 0 ? null : (double)ganhos / decididos;

    /// <summary>===================== QUEM DECIDE SE O MÊS VALE É ESTA FUNÇÃO =====================
    /// Fábrica, e não construtor direto do record, porque `Conversao` e `AmostraInsuficiente` são
    /// DERIVADOS e não dados.
    ///
    /// ⚠️ A PRIMEIRA VERSÃO DEIXOU A CONTA NO SERVIÇO e só a constante aqui. Era a regra partida em
    /// dois arquivos: o teste de unidade recebia `AmostraInsuficiente` como entrada e por isso NÃO
    /// conseguia afirmar nada sobre o limiar de 10 — subi-lo para 50 não derrubaria teste nenhum.
    /// Com a decisão aqui, o limiar é afirmável sem banco.
    ///
    /// ⚠️ ZERO DECIDIDOS NÃO É "AMOSTRA INSUFICIENTE", é ausência. A tela escreve "amostra
    /// insuficiente" ao lado de um número que existe e não vale; escrever isso num mês vazio
    /// acusaria de pouco quem simplesmente não teve nada para decidir.
    /// ============================================================================================</summary>
    public static MesDaConversao Mes(int ano, int mes, int decididos, int ganhos, bool parcial) =>
        new(ano, mes, decididos, ganhos,
            Conversao(ganhos, decididos),
            parcial,
            decididos > 0 && decididos < AmostraMinima);

    public static bool Vota(MesDaConversao m) =>
        !m.Parcial && !m.AmostraInsuficiente && m.Conversao is not null;

    /// <summary>A média dos até 3 últimos meses que votam contra a dos até 3 anteriores.</summary>
    public static (double? VariacaoPontos, string Tendencia) De(IReadOnlyList<MesDaConversao> meses)
    {
        var validos = meses.Where(Vota).ToList();

        if (validos.Count < MinimoDeMesesValidos) return (null, "sem_dados");

        var recentes = validos.TakeLast(3).ToList();
        var anteriores = validos
            .Take(validos.Count - recentes.Count)
            .TakeLast(3)
            .ToList();

        if (anteriores.Count == 0) return (null, "sem_dados");

        // Em PONTOS: a conversão já é uma fração de 0 a 1, então a diferença vira ponto percentual
        // multiplicando por 100 UMA vez.
        var media = (IEnumerable<MesDaConversao> m) => m.Average(x => x.Conversao!.Value) * 100d;
        var variacao = Math.Round(media(recentes) - media(anteriores), 1);

        string tendencia;
        if (variacao >= FaixaEstavel) tendencia = "melhorando";
        else if (variacao <= -FaixaEstavel) tendencia = "piorando";
        else tendencia = "estavel";

        return (variacao, tendencia);
    }
}

public interface IServicoEvolucao
{
    /// <summary>===================== A CONVERSÃO QUE NÃO MUDA O PASSADO =====================
    ///
    /// ⚠️ ESTA NÃO É A CONVERSÃO DO RELATÓRIO DE VENDEDORES, e a diferença é o motivo de este
    /// serviço existir. Aquela responde "neste período" e responde bem; ela NÃO sobrevive a virar
    /// série, por três razões medidas:
    ///
    ///   1. o denominador dela conta por `contatos.responsavel_id`, e `LiberacaoDeCiclo` ZERA essa
    ///      coluna quando a venda é concluída — inclusive pela rodada diária. O lead sai do
    ///      denominador do vendedor sozinho, meses depois, e a conversão dele SOBE sem que nada
    ///      tenha acontecido;
    ///   2. o numerador conta por `negociacoes.responsavel_id`, que não é zerado. São duas
    ///      atribuições diferentes na mesma fração;
    ///   3. o "perdidos" dela não tem recorte de data nenhum: registrar uma perda hoje muda a
    ///      conversão de março.
    ///
    /// Aqui as duas pontas usam `negociacoes.responsavel_id` (estável: nem o ganho nem a perda a
    /// tocam) e as duas são datadas pelo PRÓPRIO fechamento — ganho por `ganha_em`, perda por
    /// `perdida_em`. É uma coorte só, e o mês de março calculado hoje é o mesmo de março.
    ///
    /// Quem não tem `ver_numeros_da_equipe` recebe só a própria linha, e SEM a média da equipe —
    /// a média é número da equipe, e entregá-la a quem não pode vê-la seria vazar pela porta dos
    /// fundos o que a permissão fecha pela frente.
    /// ==============================================================================</summary>
    Task<EvolucaoDaEquipe> ObterAsync(int meses, CancellationToken ct);
}
