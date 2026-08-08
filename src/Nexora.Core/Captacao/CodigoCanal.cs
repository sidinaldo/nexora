using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Nexora.Core.Captacao;

/// <summary>O CÓDIGO DE RASTREIO que viaja no texto do WhatsApp.
///
/// Vive no Core, sem dependência de nada, porque os dois lados precisam concordar caractere a
/// caractere: quem GERA (a tela de canais) e quem LÊ (o processador do webhook). Duas cópias
/// dessa regra divergiriam, e o sintoma seria um canal que nunca atribui — sem erro em lugar
/// nenhum.</summary>
public static partial class CodigoCanal
{
    /// <summary>Quatro caracteres. 28^4 = 614.656 combinações por empresa, o que é ordens de
    /// grandeza a mais do que qualquer PME vai criar de canais.
    ///
    /// Por que CURTO: o código vai colado numa frase que a pessoa lê antes de mandar. Bloco longo
    /// de caracteres aleatórios parece erro ou spam, e ela apaga por estranhamento — o que
    /// destrói exatamente a coisa que estamos medindo. Quatro caracteres passam por detalhe.</summary>
    public const int Tamanho = 4;

    /// <summary>SEM VOGAIS e sem `l`, `0`, `1`.
    ///
    /// Sem vogais porque o código é IMPRESSO em panfleto e cartão de visita: um alfabeto completo
    /// gera, mais cedo ou mais tarde, uma palavra que ninguém quer ver no material de um cliente.
    /// Com só consoantes e dígitos isso não acontece.
    ///
    /// Sem `l`/`0`/`1` porque alguém vai digitar à mão em algum momento — e `l` contra `1` e `0`
    /// contra `O` é o par que mais erra.</summary>
    public const string Alfabeto = "23456789bcdfghjkmnpqrstvwxyz";

    /// <summary>Quantos candidatos são considerados numa mensagem. Uma pessoa manda um código;
    /// mais que isso é texto colado ou script, e não vale uma consulta por hashtag.</summary>
    private const int MaximoCandidatos = 5;

    /// <summary>`#` + 4 do alfabeto, e NADA alfanumérico grudado depois.
    ///
    /// O `#` é obrigatório: sem ele, qualquer palavra de quatro consoantes viraria candidato, e o
    /// sistema começaria a atribuir origem por acidente — que é pior que não atribuir.
    ///
    /// O lookahead negativo é o que impede `#bcdfghjk` de casar como `#bcdf`: hashtag comprida é
    /// hashtag de campanha, não código nosso.</summary>
    [GeneratedRegex(@"#([23456789bcdfghjkmnpqrstvwxyz]{4})(?![0-9a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Padrao();

    /// <summary>Gera um código novo. RNG criptográfico e não `Random`: dois canais criados no
    /// mesmo tick com a mesma semente colidiriam, e a colisão só apareceria como um `Criar` que
    /// falha sem explicação.</summary>
    public static string Gerar()
    {
        var chars = new char[Tamanho];
        for (var i = 0; i < Tamanho; i++)
            chars[i] = Alfabeto[RandomNumberGenerator.GetInt32(Alfabeto.Length)];
        return new string(chars);
    }

    /// <summary>Os códigos encontrados no texto, na ORDEM em que aparecem, sem repetição e em
    /// minúsculas.
    ///
    /// Devolve lista e não um só porque a pessoa pode mandar duas frases coladas (ex.: encaminhou
    /// a mensagem de um amigo). Quem decide qual vale é quem consulta o banco: o primeiro que
    /// resolver para um canal ATIVO da empresa. Escolher aqui exigiria conhecer os canais, e este
    /// arquivo não fala com banco de propósito.</summary>
    public static IReadOnlyList<string> Extrair(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return [];

        List<string>? achados = null;
        foreach (Match m in Padrao().Matches(texto))
        {
            var codigo = m.Groups[1].Value.ToLowerInvariant();
            achados ??= [];
            if (!achados.Contains(codigo)) achados.Add(codigo);
            if (achados.Count >= MaximoCandidatos) break;
        }

        return achados ?? (IReadOnlyList<string>)[];
    }

    /// <summary>O teto da frase que o dono do canal escreve.
    ///
    /// Existe pela MESMA razão do código curto: texto pré-preenchido longo parece spam, e a
    /// pessoa apaga tudo antes de enviar — levando o código junto e matando a atribuição. 120
    /// dá umas duas linhas: cabe uma frase de verdade, não cabe um panfleto.</summary>
    public const int LimiteMensagem = 120;

    /// <summary>A frase de quem não escreveu nenhuma.</summary>
    public const string MensagemPadrao = "Olá! Tenho interesse.";

    /// <summary>O texto pré-preenchido do link. Frase NATURAL na frente, código curto no fim.
    ///
    /// A ordem não é estética. "Olá! Tenho interesse. #k7m2" tem muito mais chance de ser enviado
    /// inteiro que um código solto: a pessoa lê uma saudação que faz sentido, reconhece como sua,
    /// e manda. Um campo com só `#k7m2` parece lixo e é apagado.
    ///
    /// ===================== A FRASE É DO CLIENTE; O CÓDIGO É NOSSO =====================
    /// O dono do canal escreve o que faz sentido para a campanha dele. O que ele NÃO decide é se
    /// o código vai junto: sem código não há atribuição, e canal que não atribui não serve para
    /// nada — que é exatamente o problema que ele existe para resolver.
    ///
    /// Por isso esta função ACRESCENTA e nunca substitui. E não duplica: quem copiar o texto
    /// final da tela de volta para o campo já traz o código dentro, e `Extrair` devolveria o
    /// mesmo código duas vezes.
    /// ==============================================================================</summary>
    public static string TextoDoLink(string codigo, string? mensagem = null)
    {
        var frase = string.IsNullOrWhiteSpace(mensagem) ? MensagemPadrao : mensagem.Trim();
        var marca = $"#{codigo}";

        return Extrair(frase).Contains(codigo) ? frase : $"{frase} {marca}";
    }
}
