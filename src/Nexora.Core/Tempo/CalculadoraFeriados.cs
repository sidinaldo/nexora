namespace Nexora.Core.Tempo;

/// <summary>Calcula os feriados brasileiros de um ano. Função PURA (sem I/O, sem banco) — o
/// seed anual chama isto e grava o resultado. Feriados móveis derivam da Páscoa (algoritmo
/// anônimo gregoriano de Meeus/Butcher).</summary>
public static class CalculadoraFeriados
{
    /// <summary>UFs com feriados estaduais semeados. Estrutura pronta; ver a nota em Estaduais.</summary>
    public static readonly IReadOnlyList<string> UfsConhecidas = ["RN"];

    /// <summary>Domingo de Páscoa do ano (algoritmo anônimo gregoriano — "Computus" de Meeus).</summary>
    public static DateOnly Pascoa(int ano)
    {
        int a = ano % 19;
        int b = ano / 100;
        int c = ano % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int mes = (h + l - 7 * m + 114) / 31;
        int dia = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(ano, mes, dia);
    }

    /// <summary>Feriados NACIONAIS do ano: fixos + móveis (derivados da Páscoa).</summary>
    public static IReadOnlyList<(DateOnly Data, string Nome)> Nacionais(int ano)
    {
        var pascoa = Pascoa(ano);
        return
        [
            (new DateOnly(ano, 1, 1),   "Confraternização Universal"),
            (pascoa.AddDays(-48),       "Carnaval"),                       // segunda
            (pascoa.AddDays(-47),       "Carnaval"),                       // terça
            (pascoa.AddDays(-2),        "Sexta-feira Santa"),
            (new DateOnly(ano, 4, 21),  "Tiradentes"),
            (new DateOnly(ano, 5, 1),   "Dia do Trabalho"),
            (pascoa.AddDays(60),        "Corpus Christi"),
            (new DateOnly(ano, 9, 7),   "Independência do Brasil"),
            (new DateOnly(ano, 10, 12), "Nossa Senhora Aparecida"),
            (new DateOnly(ano, 11, 2),  "Finados"),
            (new DateOnly(ano, 11, 15), "Proclamação da República"),
            (new DateOnly(ano, 11, 20), "Dia da Consciência Negra"),      // nacional desde 2024
            (new DateOnly(ano, 12, 25), "Natal"),
        ];
    }

    /// <summary>Feriados ESTADUAIS do ano para a UF.
    ///
    /// ===================== COBERTURA PARCIAL, E ISSO É ESPERADO =====================
    /// Só as UFs em `UfsConhecidas` têm cadastro. UF sem entrada devolve lista VAZIA — nunca
    /// exceção: a empresa recebe os feriados nacionais e o sistema segue funcionando, que é o
    /// comportamento correto enquanto o cadastro não estiver completo.
    ///
    /// Falhar aqui derrubaria o seed diário do agendador por causa de um dado que falta, e o
    /// efeito colateral seria não semear nem os NACIONAIS — trocando uma lacuna pequena por uma
    /// grande. As UFs faltantes estão listadas em docs/PI-5.md.
    /// ================================================================================</summary>
    public static IReadOnlyList<(DateOnly Data, string Nome)> Estaduais(int ano, string? uf) =>
        (uf ?? "").ToUpperInvariant() switch
        {
            "RN" => [(new DateOnly(ano, 10, 3), "Mártires de Cunhaú e Uruaçu")],
            _ => []
        };
}
