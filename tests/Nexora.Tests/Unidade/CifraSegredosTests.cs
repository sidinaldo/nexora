using System.Security.Cryptography;
using Nexora.Core.Seguranca;

namespace Nexora.Tests.Unidade;

/// <summary>OS SEGREDOS DA CLOUD API, CIFRADOS EM REPOUSO (INT-XX).
///
/// O que importa provar: o que sai do banco so abre com a chave e a finalidade certas, e qualquer
/// alteracao e recusada — devolver lixo mandaria o token errado para a Meta, e a falha apareceria
/// longe daqui.</summary>
public class CifraSegredosTests
{
    private static string Chave() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static CifraSegredos Nova(string? chave = null) =>
        new(new OpcoesCifra { ChaveCifra = chave ?? Chave() });

    [Fact]
    public void O_QUE_CIFRA_ABRE_DE_VOLTA()
    {
        var cifra = Nova();

        var guardado = cifra.Cifrar("EAAG-token-de-verdade", FinalidadeSegredo.AccessToken);

        Assert.StartsWith("v1.", guardado);
        Assert.DoesNotContain("EAAG", guardado);
        Assert.Equal("EAAG-token-de-verdade", cifra.Decifrar(guardado, FinalidadeSegredo.AccessToken));
    }

    /// <summary>O nonce e sorteado: o mesmo token cifrado duas vezes nao gera o mesmo texto, e quem
    /// le o banco nao descobre que duas conexoes usam o mesmo token.</summary>
    [Fact]
    public void O_MESMO_SEGREDO_NAO_SAI_IGUAL_DUAS_VEZES()
    {
        var cifra = Nova();

        Assert.NotEqual(
            cifra.Cifrar("igual", FinalidadeSegredo.AppSecret),
            cifra.Cifrar("igual", FinalidadeSegredo.AppSecret));
    }

    [Fact]
    public void BYTE_ALTERADO_E_RECUSADO()
    {
        var cifra = Nova();
        var guardado = cifra.Cifrar("segredo", FinalidadeSegredo.AppSecret);

        var bytes = Convert.FromBase64String(guardado[3..]);
        bytes[^1] ^= 0x01;
        var alterado = "v1." + Convert.ToBase64String(bytes);

        Assert.ThrowsAny<CryptographicException>(() => cifra.Decifrar(alterado, FinalidadeSegredo.AppSecret));
    }

    [Fact]
    public void OUTRA_CHAVE_NAO_ABRE()
    {
        var guardado = Nova().Cifrar("segredo", FinalidadeSegredo.AccessToken);

        Assert.ThrowsAny<CryptographicException>(
            () => Nova().Decifrar(guardado, FinalidadeSegredo.AccessToken));
    }

    /// <summary>O token copiado para a coluna do app secret (ou o contrario) nao abre: a
    /// finalidade entra como dado associado do GCM.</summary>
    [Fact]
    public void OUTRA_FINALIDADE_NAO_ABRE()
    {
        var cifra = Nova();
        var guardado = cifra.Cifrar("segredo", FinalidadeSegredo.AccessToken);

        Assert.ThrowsAny<CryptographicException>(
            () => cifra.Decifrar(guardado, FinalidadeSegredo.AppSecret));
    }

    [Theory]
    [InlineData("texto-sem-versao")]
    [InlineData("v1.nao-e-base64!!")]
    [InlineData("v1.AAAA")]
    public void FORMATO_DESCONHECIDO_E_RECUSADO(string valor)
    {
        Assert.ThrowsAny<CryptographicException>(() => Nova().Decifrar(valor, FinalidadeSegredo.AccessToken));
    }

    /// <summary>⚠️ A API NAO SOBE SEM A CHAVE CERTA. Vazia, curta ou fora de base64 tem de falhar
    /// na subida — nao na primeira conexao oficial, longe da causa.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curta")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]  // 31 bytes
    public void CHAVE_INVALIDA_IMPEDE_A_SUBIDA(string? chave)
    {
        var erro = Assert.Throws<InvalidOperationException>(() => CifraSegredos.ChaveValida(chave));
        Assert.Contains("openssl rand -base64 32", erro.Message);
    }
}
