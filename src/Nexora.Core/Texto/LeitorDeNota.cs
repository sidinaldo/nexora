namespace Nexora.Core.Texto;

/// <summary>O que o leitor concluiu sobre a mensagem.</summary>
public enum LeituraDeNota
{
    /// <summary>Mensagem normal. A pesquisa continua aberta.</summary>
    NaoEhNota,

    /// <summary>É a nota, com confiança suficiente para registrar sozinho.</summary>
    Nota,

    /// <summary>Tem um número de 0 a 10, mas o jeito como ele aparece não permite afirmar. O
    /// vendedor vê "Isto é uma nota?" na conversa e decide.</summary>
    PossivelNota
}

/// <summary>A nota lida, e o que sobrou da frase.</summary>
public record NotaLida(LeituraDeNota Resultado, int? Nota, string? Comentario)
{
    public static readonly NotaLida NaoEh = new(LeituraDeNota.NaoEhNota, null, null);
}

/// <summary>===================== "ISTO É UMA NOTA?" =====================
///
/// A pesquisa pergunta "de 0 a 10, quanto você nos recomendaria?" e o cliente responde no
/// WhatsApp, em texto livre. Esta classe decide se aquele texto é a nota.
///
/// ⚠️ ERRAR PARA O LADO DE "É NOTA" É PIOR QUE ERRAR PARA O OUTRO. Uma nota inventada entra no
/// relatório, e uma nota baixa inventada dispara lembrete para o vendedor e aviso para o dono — o
/// cliente é incomodado por uma reclamação que ele não fez. Uma nota perdida só deixa a pesquisa
/// aberta até expirar, e a `PossivelNota` existe justamente para o caso duvidoso não ser jogado
/// fora: ele vai para um humano.
///
/// ===================== O SINAL NÃO É "TEM UM NÚMERO" =====================
/// "quero 2 unidades" e "chego às 10h" têm números de 0 a 10 e não são notas. O que separa é
/// COMO o número aparece:
///
///   1. a mensagem CITA a pesquisa    → é a nota, qualquer que seja o resto;
///   2. vem depois de um puxador      → "nota 9", "dou 8 pra vocês";
///   3. abre a mensagem e termina ali  → "10", "9!", "10, adorei o atendimento";
///   4. está colado numa letra        → "10h" NÃO é candidato: é hora, unidade, medida;
///   5. qualifica a palavra seguinte  → "2 caixas chegaram quebradas" não é nota.
///
/// ⚠️ O CASO 5 É O QUE ME FEZ ABANDONAR "NÚMERO NA PRIMEIRA POSIÇÃO" COMO REGRA. Ele tem o
/// número na posição zero, como "10, adorei", e é uma reclamação sobre quantidade. O que os
/// separa é a PONTUAÇÃO depois do número: "10," anuncia que o número acabou e o comentário
/// começa; "2 caixas" usa o número para contar caixas. Sem essa distinção, uma reclamação de
/// entrega virava nota 2 — e nota 2 é detrator, que avisa o dono.
///
/// ===================== OS LIMITES, E POR QUE ESTES =====================
/// O prompt pede os limites exatos numa classe isolada. São dois:
///
///   · `MaximoDePalavrasParaPossivel` = 6 — veio do prompt, e governa só a `PossivelNota`:
///     número solto numa frase curta vale uma pergunta ao humano; numa frase longa é assunto.
///
///   · NÃO HÁ limite de tamanho no caminho da NOTA, e isso foi uma escolha. A primeira versão
///     tinha um teto de 12 palavras, e ele jogava fora "10, vocês foram muito atenciosos e
///     entregaram antes do prazo combinado, recomendo a todos" — um dez genuíno com elogio, que é
///     exatamente o que a pesquisa quer colher. O puxador e a pontuação já são sinal forte o
///     bastante; o tamanho não acrescenta nada.
///
/// ⚠️ 11 NÃO É NOTA, e nem 0 a 10 fora de faixa: a escala é fechada. "11" é outra coisa.
/// ==============================================================</summary>
public static class LeitorDeNota
{
    /// <summary>Acima disso, número solto no meio da frase é assunto, não nota em dúvida. Veio do
    /// prompt, e governa só a `PossivelNota`.</summary>
    public const int MaximoDePalavrasParaPossivel = 6;

    /// <summary>===================== OS PUXADORES =====================
    /// Palavras que, antes do número, dizem que o que vem é uma avaliação.
    ///
    /// Lista CURTA e explícita de propósito: cada palavra aqui é uma licença para transformar
    /// número em nota, e uma lista generosa ("acho", "foi", "é") pegaria "acho 3 caixas", "foi 2
    /// dias". As formas com e sem acento estão as duas, em vez de normalizar — são poucas palavras,
    /// e tirar acento daria uma função a mais para manter.
    ///
    /// ⚠️ SAO DOIS TIPOS, E A PRIMEIRA VERSAO OS TRATAVA IGUAL (revisao NPS-1):
    ///
    ///   · FORTES — "nota", "notas": a palavra JA DIZ que o que vem e avaliacao. "nota 9 muito bom"
    ///     e nove, com o resto de comentario.
    ///   · FRACOS — "dou", "dei", "daria", "meu", "minha": dizem que pode vir uma avaliacao, mas
    ///     tambem precedem quantidade. Com eles valendo como os fortes, "dou 5 estrelas" virava
    ///     nota 5 — um DETRATOR, para um cliente que deu a nota maxima noutra escala — e "meu 2
    ///     pedidos chegaram errados" virava nota 2. O falso positivo que esta classe diz ser pior
    ///     que a nota perdida.
    ///
    /// O fraco so libera o numero quando ele FECHA a ideia: ultima palavra, pontuacao depois, ou
    /// uma das `Pontes` em seguida ("dou 8 pra vocês"). Fora disso, a frase curta vira duvida para
    /// o vendedor — que e o lugar certo para "dou 5 estrelas".
    /// ========================================================</summary>
    private static readonly string[] PuxadoresFortes = ["nota", "notas"];

    private static readonly string[] PuxadoresFracos = ["dou", "daria", "dei", "minha", "meu"];

    /// <summary>O que pode vir DEPOIS do numero de um puxador fraco sem que o numero esteja contando
    /// a palavra seguinte: a quem a nota e dada. Lista curta de proposito — "com" e "sem" ficam de
    /// fora ("dei 2 com defeito"), e "dou 10 com certeza" vira duvida, o que custa um clique.</summary>
    private static readonly string[] Pontes =
    [
        "pra", "para", "pro", "pros", "a", "à", "ao", "aos",
        "vocês", "voces", "vcs", "vc", "você", "voce"
    ];

    /// <summary>Lê a mensagem recebida.
    ///
    /// `citouAPesquisa` vem de `contextInfo.stanzaId` do payload do Evolution casando com o
    /// `wa_message_id` do envio da pesquisa. ⚠️ MEDIDO NO `nexora_dev`: de 1440 mensagens de
    /// entrada com payload, 69 têm `contextInfo` e só 3 têm `stanzaId` — quase ninguém responde
    /// citando. É o sinal mais forte quando aparece, e os outros caminhos é que carregam o
    /// trabalho.</summary>
    public static NotaLida Ler(string? texto, bool citouAPesquisa)
    {
        if (string.IsNullOrWhiteSpace(texto)) return NotaLida.NaoEh;

        var palavras = texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (palavras.Length == 0) return NotaLida.NaoEh;

        // ---- 1. Citou a pesquisa: o primeiro número de 0 a 10 é a nota --------------------
        // A citação é uma resposta deliberada AQUELA mensagem. O prompt põe esta regra em
        // primeiro lugar, e ela vence até o caso 5: quem cita e escreve "quero 2 unidades" está
        // respondendo a pesquisa, por estranho que seja o texto.
        if (citouAPesquisa)
        {
            for (var i = 0; i < palavras.Length; i++)
            {
                int valor;
                bool pontuada;

                if (Candidato(palavras[i], out valor, out pontuada))
                    return new NotaLida(LeituraDeNota.Nota, valor, Comentario(palavras, i + 1));
            }

            return NotaLida.NaoEh;
        }

        // ---- 2. Puxadores, e depois deles o número ----------------------------------------
        var indice = 0;
        var puxadorForte = false;
        var puxadorFraco = false;

        // "minha nota 5": os dois tipos em sequencia. Basta um forte para a frase ser avaliacao.
        while (indice < palavras.Length)
        {
            if (EstaNaLista(palavras[indice], PuxadoresFortes)) puxadorForte = true;
            else if (EstaNaLista(palavras[indice], PuxadoresFracos)) puxadorFraco = true;
            else break;

            indice++;
        }

        if (indice < palavras.Length)
        {
            int valor;
            bool pontuada;

            if (Candidato(palavras[indice], out valor, out pontuada))
            {
                // ===================== O NÚMERO ACABOU AQUI? =====================
                // ⚠️ A PONTUAÇÃO PODE SER TOKEN PRÓPRIO, e a primeira versão só olhava a colada ao
                // número. `"10 !"` — dez com ponto de exclamação separado — caía como `PossivelNota`,
                // uma dúvida sobre uma nota óbvia. Quem achou foi o teste do comentário vazio.
                //
                // Três formas de dizer "o número terminou": nada depois dele, pontuação colada
                // ("10,"), ou a palavra seguinte COMEÇANDO com pontuação ("10 , adorei", "10 !").
                // ================================================================
                var ultima = indice == palavras.Length - 1;
                var seguinteEhPontuacao =
                    !ultima && Array.IndexOf(Pontuacao, palavras[indice + 1][0]) >= 0;

                // ⚠️ AQUI MORA O CASO 5. Número que NÃO termina a mensagem e NÃO tem pontuação
                // depois está contando a palavra seguinte — "2 caixas chegaram quebradas". O
                // puxador FORTE vence isso ("nota 9 muito bom"); o FRACO, so com uma ponte depois
                // ("dou 8 pra vocês") — sem ela, "dou 5 estrelas" seria nota 5. Ver `PuxadoresFracos`.
                var seguinteEhPonte = !ultima && EstaNaLista(palavras[indice + 1], Pontes);

                if (puxadorForte || ultima || pontuada || seguinteEhPontuacao
                    || (puxadorFraco && seguinteEhPonte))
                    return new NotaLida(
                        LeituraDeNota.Nota, valor, Comentario(palavras, indice + 1));
            }
        }

        // ---- 3. Número solto em frase curta: pergunta ao humano ---------------------------
        if (palavras.Length > MaximoDePalavrasParaPossivel) return NotaLida.NaoEh;

        var quantos = 0;
        var unico = 0;

        for (var i = 0; i < palavras.Length; i++)
        {
            int valor;
            bool pontuada;

            if (Candidato(palavras[i], out valor, out pontuada))
            {
                quantos++;
                unico = valor;
            }
        }

        // "apenas se não houver OUTRO número na frase": dois candidatos não geram dúvida, geram
        // ruído — e perguntar "é nota 2 ou nota 6?" não é pergunta que o vendedor saiba responder.
        if (quantos == 1) return new NotaLida(LeituraDeNota.PossivelNota, unico, null);

        return NotaLida.NaoEh;
    }

    private static bool EstaNaLista(string palavra, string[] lista)
    {
        var limpa = Nucleo(palavra);

        for (var i = 0; i < lista.Length; i++)
        {
            if (string.Equals(limpa, lista[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>O número de 0 a 10 dentro da palavra, se houver um.
    ///
    /// `pontuada` diz se sobrou pontuação DEPOIS do número ("10," → true). É o que separa o
    /// comentário anunciado da quantidade contando a palavra seguinte.</summary>
    private static bool Candidato(string palavra, out int valor, out bool pontuada)
    {
        valor = 0;
        pontuada = false;

        var nucleo = Nucleo(palavra);

        if (nucleo.Length == 0) return false;

        pontuada = nucleo.Length < palavra.TrimStart(Pontuacao).Length;

        // ⚠️ "10/10" É DEZ, e é como muita gente escreve nota. O divisor só vale quando a segunda
        // metade é o TOPO da escala: "10/10" e "9/10" são notas, "2/3" é uma fração qualquer.
        var barra = nucleo.IndexOf('/');

        if (barra > 0)
        {
            var esquerda = nucleo.Substring(0, barra);
            var direita = nucleo.Substring(barra + 1);

            if (SoDigitos(direita) && direita.TrimStart('0') == "10" && SoDigitos(esquerda))
                return EmFaixa(esquerda, out valor);

            return false;
        }

        // ⚠️ TODO O NÚCLEO TEM DE SER DÍGITO. É o que descarta "10h" — hora, unidade, medida —
        // sem precisar de lista de sufixos.
        if (!SoDigitos(nucleo)) return false;

        return EmFaixa(nucleo, out valor);
    }

    private static bool EmFaixa(string digitos, out int valor)
    {
        valor = 0;

        // Mais de dois dígitos nunca é nota, e `int.TryParse` em "00000000010" seria verdadeiro.
        if (digitos.Length > 2) return false;

        if (!int.TryParse(digitos, out var n)) return false;

        if (n < 0 || n > 10) return false;

        valor = n;
        return true;
    }

    private static bool SoDigitos(string s)
    {
        if (s.Length == 0) return false;

        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsAsciiDigit(s[i])) return false;
        }

        return true;
    }

    private static readonly char[] Pontuacao =
        ['.', ',', '!', '?', ';', ':', ')', '(', '"', '\'', '-', '…', '*', '_'];

    private static string Nucleo(string palavra) => palavra.Trim(Pontuacao);

    /// <summary>O que sobrou depois da nota. Vazio vira nulo: comentário em branco na tela é pior
    /// que comentário ausente, porque parece que o cliente escreveu algo que não apareceu.</summary>
    private static string? Comentario(string[] palavras, int de)
    {
        if (de >= palavras.Length) return null;

        var resto = string.Join(' ', palavras, de, palavras.Length - de).Trim();

        // A pontuação que sobrou do número fica na frente do comentário: "10, adorei" deixaria
        // ", adorei" se a junção começasse antes.
        resto = resto.TrimStart(Pontuacao).Trim();

        if (resto.Length == 0) return null;

        return resto;
    }
}
