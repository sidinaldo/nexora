using System.Security.Cryptography;
using System.Text;
using Nexora.Core.Whatsapp;

namespace Nexora.Core.Conversoes;

/// <summary>O DADO PESSOAL COMO A META O QUER: SHA-256, hex minúsculo (INT-4).
///
/// ===================== POR QUE ISTO PRECISA ESTAR CERTO =====================
/// Erro aqui não dá erro. A Meta responde 200, o evento entra, e o casamento simplesmente não
/// acontece — o cliente vê "0 correspondências" num painel que ele não abre, e conclui que o
/// produto não funciona. Nenhuma das regras abaixo tem sintoma visível quando é violada.
/// ============================================================================
///
/// ===================== O QUE **NÃO** É HASHEADO =====================
/// IP, User-Agent, `fbp` e `fbc` vão EM CLARO. A Meta os usa como estão; hasheá-los é o erro mais
/// silencioso de todos, porque parece mais seguro e destrói exatamente a atribuição que o bloco
/// existe para fazer.
/// ====================================================================</summary>
public static class HashPessoal
{
    /// <summary>O e-mail: `trim`, minúsculo, SHA-256.
    ///
    /// Minúsculo porque a Meta normaliza assim do lado dela — `Joao@X.com` e `joao@x.com` são a
    /// mesma pessoa, e dois hashes diferentes são duas pessoas diferentes para ela.</summary>
    public static string? Email(string? email)
    {
        var limpo = (email ?? "").Trim().ToLowerInvariant();
        return limpo.Length == 0 ? null : Sha256(limpo);
    }

    /// <summary>O telefone: só dígitos com o código do país, SHA-256.
    ///
    /// Reusa `CanonicalizadorTelefone` de propósito — o formato que o Nexora já guarda
    /// (`5584988887777`) É o que a Meta pede. Escrever uma segunda normalização aqui criaria duas
    /// definições de "o mesmo telefone", e a divergência apareceria como lead que não casa.</summary>
    public static string? Telefone(string? telefone)
    {
        if (!CanonicalizadorTelefone.EhValido(telefone)) return null;

        return Sha256(CanonicalizadorTelefone.Canonicalizar(telefone!));
    }

    /// <summary>===================== CAMPO VAZIO É AUSENTE, NUNCA `sha256("")` =====================
    ///
    /// `sha256("")` é a constante `e3b0c442…`. Mandá-la faria TODO lead sem e-mail casar com todo
    /// lead sem e-mail do mundo — e o resultado não é "não casou": é casou com a pessoa errada, e a
    /// Meta aprendendo com um público que não existe.
    ///
    /// É a razão pela qual estes métodos devolvem `string?` em vez de `string`.
    /// ==================================================================================</summary>
    private static string Sha256(string texto) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto))).ToLowerInvariant();
}
