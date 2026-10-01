namespace Nexora.Core;

/// <summary>Quem está operando, quando a borda sabe dizer.
///
/// ===================== ENRIQUECIMENTO, NUNCA REQUISITO =====================
/// A credencial da área do operador é a chave no cabeçalho, e ela não tem identidade: todo mundo
/// que a tem é "a chave". O Cloudflare Access, posto na frente das rotas, injeta
/// `Cf-Access-Authenticated-User-Email` — e aí a trilha passa a saber QUEM mudou o plano de um
/// cliente, e não só que foi o operador.
///
/// ⚠️ MAS A APLICAÇÃO NÃO PODE EXIGIR O CABEÇALHO. Exigi-lo trocaria um controle que funciona (a
/// chave, comparada em tempo constante) por outro que não está nas nossas mãos: no dia em que o
/// Access tiver problema, a área do operador ficaria indisponível — e é justamente o dia em que se
/// quer entrar para desativar alguém.
///
/// ⚠️ E É AFIRMAÇÃO DA BORDA, NÃO PROVA. Sem o Access na frente, qualquer um manda esse cabeçalho.
/// Por isso ele entra no `alteracoes` da trilha como informação, e NUNCA no `usuario_id`: um e-mail
/// que a borda afirmou não é um usuário deste sistema, e gravá-lo como autor seria a mesma autoria
/// falsa que o `AtorAuditoria.Operador` existe para evitar.
///
/// Mesmo arranjo do `ContextoDeFundo`: objeto simples aqui, preenchido na borda. O Core não conhece
/// ASP.NET, e quem lê cabeçalho é quem tem um.
/// ===========================================================================</summary>
public sealed class IdentidadeDoOperador
{
    public const string CabecalhoAccess = "Cf-Access-Authenticated-User-Email";

    /// <summary>Nulo quando a borda não disse nada — e nulo é o caso normal em desenvolvimento.</summary>
    public string? Email { get; set; }

    /// <summary>Aceita só o que PARECE e-mail.
    ///
    /// O valor vai parar num `jsonb` que uma tela mostra, então não entra cru: um cabeçalho de 8 KB
    /// com marcação dentro viraria uma linha de trilha gigante e, no melhor caso, ilegível.</summary>
    public static string? Sanitizar(string? bruto)
    {
        var limpo = (bruto ?? "").Trim();
        if (limpo.Length is 0 or > 120) return null;
        if (!limpo.Contains('@') || limpo.Any(char.IsControl)) return null;
        return limpo;
    }
}
