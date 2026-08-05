namespace Nexora.Core.Seguranca;

/// <summary>Endurecimento do login. Complementa o rate limit por janela (na Api, por IP)
/// com um bloqueio PERSISTENTE por CONTA: N falhas de senha consecutivas travam a conta por
/// M minutos — cross-IP, o que o limite por IP nao pega (ataque distribuido contra uma unica
/// conta). Zerado no login bem-sucedido.</summary>
public static class PoliticaLogin
{
    public const int MaxFalhasConsecutivas = 10;
    public static readonly TimeSpan Bloqueio = TimeSpan.FromMinutes(15);

    /// <summary>Hash "descartavel" para gastar o MESMO tempo de PBKDF2 quando o e-mail nao existe
    /// (ou o convidado ainda nao definiu senha). Sem isto, a resposta mais rapida denunciaria que a
    /// conta nao existe (timing oracle). Gerado uma unica vez.</summary>
    public static readonly string HashDummy = HashSenha.Gerar("equalizador-de-tempo-nao-e-senha-de-ninguem");

    /// <summary>Mascara o e-mail para o log de auditoria: joao@dominio.com -> j***@dominio.com.
    /// Nunca logar o e-mail inteiro; JAMAIS logar a senha.</summary>
    public static string MascararEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "(vazio)";
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        return $"{email[..1]}***{email[at..]}";
    }
}
