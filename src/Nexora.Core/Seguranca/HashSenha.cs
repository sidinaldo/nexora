using System.Security.Cryptography;

namespace Nexora.Core.Seguranca;

/// <summary>PBKDF2 (SHA-256, 100k iteracoes) — o mesmo algoritmo que o ASP.NET Identity
/// usa por baixo, mas sem arrastar o Identity inteiro nem uma dependencia nova.
///
/// Formato armazenado: pbkdf2$&lt;iteracoes&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;
/// As iteracoes vao no proprio hash para que dê para aumentá-las no futuro sem
/// invalidar as senhas ja cadastradas.</summary>
public static class HashSenha
{
    private const int Iteracoes = 100_000;
    private const int TamanhoSalt = 16;
    private const int TamanhoHash = 32;
    private const string Prefixo = "pbkdf2";

    public static string Gerar(string senha)
    {
        var salt = RandomNumberGenerator.GetBytes(TamanhoSalt);
        var hash = Derivar(senha, salt, Iteracoes);
        return $"{Prefixo}${Iteracoes}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Confere(string senha, string? hashArmazenado)
    {
        if (string.IsNullOrWhiteSpace(hashArmazenado)) return false;

        var partes = hashArmazenado.Split('$');
        if (partes.Length != 4 || partes[0] != Prefixo) return false;
        if (!int.TryParse(partes[1], out var iteracoes)) return false;

        try
        {
            var salt = Convert.FromBase64String(partes[2]);
            var esperado = Convert.FromBase64String(partes[3]);
            var calculado = Derivar(senha, salt, iteracoes);
            // Comparacao em tempo constante: comparar byte a byte com short-circuit
            // vazaria o tamanho do prefixo correto por timing.
            return CryptographicOperations.FixedTimeEquals(calculado, esperado);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derivar(string senha, byte[] salt, int iteracoes) =>
        Rfc2898DeriveBytes.Pbkdf2(senha, salt, iteracoes, HashAlgorithmName.SHA256, TamanhoHash);
}
