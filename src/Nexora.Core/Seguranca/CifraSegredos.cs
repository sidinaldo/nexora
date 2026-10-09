using System.Security.Cryptography;
using System.Text;

namespace Nexora.Core.Seguranca;

/// <summary>A chave da `CifraSegredos`: 32 bytes em base64, em `Segredos:ChaveCifra`.</summary>
public sealed class OpcoesCifra
{
    public string ChaveCifra { get; set; } = "";
}

/// <summary>O motivo de cada segredo cifrado. Entra como dado associado do AES-GCM: um token
/// copiado da coluna de um campo para a de outro nao abre, porque o motivo nao bate.</summary>
public static class FinalidadeSegredo
{
    public const string AccessToken = "conexoes.access_token";
    public const string AppSecret = "conexoes.app_secret";
}

/// <summary>===================== SEGREDO DE TERCEIRO, CIFRADO EM REPOUSO (INT-XX) =====================
///
/// O token da Cloud API envia mensagem em nome do cliente, e o app secret valida o que a Meta
/// manda. Em claro no banco, quem lesse um backup — ou uma consulta mal filtrada — teria os dois.
///
/// AES-GCM com uma chave da configuracao, conferida na subida (como `Conversoes:SegredoExternalId`).
/// O ASP.NET Data Protection foi descartado: ele exige guardar e proteger um chaveiro proprio, troca
/// de chave sozinho a cada 90 dias, e perder o chaveiro perde os dados em silencio.
///
/// Formato: `v1.` + base64(nonce de 12 bytes + texto cifrado + tag de 16 bytes). O prefixo de
/// versao e o que permite trocar de chave um dia sem adivinhar qual abre qual.
///
/// ⚠️ SEM A CHAVE, O BACKUP DO BANCO NAO DEVOLVE AS CONEXOES OFICIAIS. Ela vai para o
/// gerenciador de senhas junto com a `BACKUP_SENHA`.
/// ==========================================================================================</summary>
public sealed class CifraSegredos
{
    private const string Versao = "v1.";
    private const int TamanhoNonce = 12;
    private const int TamanhoTag = 16;

    private readonly byte[] chave;

    public CifraSegredos(OpcoesCifra opcoes)
    {
        chave = ChaveValida(opcoes.ChaveCifra);
    }

    /// <summary>A chave em bytes, ou erro com o comando que a gera. Publico para a subida da API
    /// falhar alto, com a mensagem certa, antes de qualquer requisicao.</summary>
    public static byte[] ChaveValida(string? base64)
    {
        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(base64))
        {
            try
            {
                bytes = Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                bytes = null;
            }
        }

        if (bytes == null || bytes.Length != 32)
            throw new InvalidOperationException(
                "Segredos:ChaveCifra precisa de 32 bytes em base64. Gere com "
              + "`openssl rand -base64 32` e guarde no gerenciador de senhas: sem ela, os tokens "
              + "das conexoes da Cloud API nao abrem.");

        return bytes;
    }

    public string Cifrar(string texto, string finalidade)
    {
        var nonce = RandomNumberGenerator.GetBytes(TamanhoNonce);
        var claro = Encoding.UTF8.GetBytes(texto);
        var cifrado = new byte[claro.Length];
        var tag = new byte[TamanhoTag];

        using (var gcm = new AesGcm(chave, TamanhoTag))
        {
            gcm.Encrypt(nonce, claro, cifrado, tag, Encoding.UTF8.GetBytes(finalidade));
        }

        var tudo = new byte[TamanhoNonce + cifrado.Length + TamanhoTag];
        nonce.CopyTo(tudo, 0);
        cifrado.CopyTo(tudo, TamanhoNonce);
        tag.CopyTo(tudo, TamanhoNonce + cifrado.Length);

        return Versao + Convert.ToBase64String(tudo);
    }

    /// <summary>O texto de volta. LANCA `CryptographicException` se o valor foi alterado, se a
    /// chave e outra ou se a finalidade nao bate — devolver lixo seria pior: o token errado iria
    /// para a Meta e a falha apareceria longe daqui.</summary>
    public string Decifrar(string valor, string finalidade)
    {
        if (!valor.StartsWith(Versao, StringComparison.Ordinal))
            throw new CryptographicException("Segredo em formato desconhecido.");

        byte[] tudo;
        try
        {
            tudo = Convert.FromBase64String(valor.Substring(Versao.Length));
        }
        catch (FormatException)
        {
            throw new CryptographicException("Segredo em formato desconhecido.");
        }

        if (tudo.Length < TamanhoNonce + TamanhoTag)
            throw new CryptographicException("Segredo truncado.");

        var tamanhoCifrado = tudo.Length - TamanhoNonce - TamanhoTag;
        var claro = new byte[tamanhoCifrado];

        using (var gcm = new AesGcm(chave, TamanhoTag))
        {
            gcm.Decrypt(
                tudo.AsSpan(0, TamanhoNonce),
                tudo.AsSpan(TamanhoNonce, tamanhoCifrado),
                tudo.AsSpan(TamanhoNonce + tamanhoCifrado, TamanhoTag),
                claro,
                Encoding.UTF8.GetBytes(finalidade));
        }

        return Encoding.UTF8.GetString(claro);
    }
}
