namespace Nexora.Core.Texto;

/// <summary>Como tratar alguém pelo nome numa mensagem que SAI do produto.
///
/// ===================== POR QUE ISTO EXISTE, E O QUE SAIU ERRADO =====================
/// Relatado assim, com a mensagem colada:
///
///     "a régua está sendo enviada sem o nome ou numero do contato
///      Oi, (84)! Passando para saber se você ainda tem interesse."
///
/// Duas decisões corretas, tomadas longe uma da outra, produziram isso:
///
///   1. `CanonicalizadorTelefone.Formatar` vira o NOME do contato quando o WhatsApp não manda
///      `pushName` — e o comentário de lá diz por quê: `nome` é NOT NULL, e o telefone formatado
///      é melhor que vazio na tela. Está certo, e vale para toda a interface.
///
///   2. O primeiro nome saía de `nome.Split(' ')[0]` — a definição óbvia, e certa para gente.
///
/// Juntas: `"(84) 95278-7173".Split(' ')[0]` == `"(84)"`. O cliente recebeu no WhatsApp dele uma
/// mensagem que o chamava de "(84)". Nenhum dos dois lados estava errado sozinho; o que faltava
/// era alguém perguntar **"isto é um nome?"** antes de usar como nome.
///
/// ⚠️ ERAM TRÊS CÓPIAS IDÊNTICAS de `PrimeiroNome` — `MotorFollowUp`, `MontadorEmail` e
/// `ServicoSemente` — e por isso o defeito valia para os três de uma vez, incluindo o e-mail. Uma
/// cópia só, pelo mesmo motivo que `RegrasNegociacao` existe: consertar em um lugar e esquecer os
/// outros dois é como este projeto já viu o dashboard dizer 72 e o quadro mostrar 69.
/// ====================================================================================</summary>
public static class NomeDePessoa
{
    /// <summary>O primeiro nome para tratar a pessoa, ou NULO quando não há nome de verdade.
    ///
    /// ⚠️ NULO, E NÃO UM SUBSTITUTO. A versão anterior devolvia `"tudo bem"` para nome vazio, o
    /// que produzia "Olá, tudo bem!" — legível, mas é a função escolhendo o texto da mensagem, que
    /// não é problema dela. Quem escreve a frase é quem sabe se ela é "Oi" ou "Olá"; aqui só se
    /// responde "há nome?".
    ///
    /// ⚠️ O PRIMEIRO PEDAÇO COM LETRA, e não o primeiro pedaço. "(84) 95278-7173" não tem nenhum,
    /// então não há nome — é o caso do defeito. "123 Maria" tem, e devolve "Maria" em vez de
    /// desistir: pedaço sem letra não é nome, mas não invalida o que vem depois.
    ///
    /// Não tenta adivinhar mais que isso. Nome de gente é bagunçado de propósito — emoji, sobrenome
    /// na frente, letra sozinha — e o único erro que importa aqui é chamar alguém por um número.</summary>
    public static string? Primeiro(string? nomeCompleto)
    {
        if (string.IsNullOrWhiteSpace(nomeCompleto)) return null;

        return nomeCompleto
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(pedaco => pedaco.Any(char.IsLetter));
    }

    /// <summary>A abertura pronta: <c>"Oi, Maria!"</c> — ou <c>"Oi!"</c> quando não há nome.
    ///
    /// ⚠️ A SAUDAÇÃO INTEIRA, e não só o nome, porque é a PONTUAÇÃO que muda junto. Devolver "" e
    /// deixar o template com <c>$"Oi, {nome}!"</c> produziria "Oi, !" — trocar um defeito visível
    /// por outro. Aqui quem decide se a vírgula existe é quem sabe se o nome existe.
    ///
    /// <paramref name="abertura"/> é a palavra do canal: "Oi" no WhatsApp, "Olá" no e-mail.</summary>
    public static string Saudacao(string abertura, string? nomeCompleto) =>
        Primeiro(nomeCompleto) is { } nome ? $"{abertura}, {nome}!" : $"{abertura}!";
}
