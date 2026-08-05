using Nexora.Core.Seguranca;

namespace Nexora.Tests;

public class HashSenhaTests
{
    [Fact]
    public void Gera_e_confere_a_propria_senha()
    {
        var hash = HashSenha.Gerar("senha-do-vendedor-123");

        Assert.True(HashSenha.Confere("senha-do-vendedor-123", hash));
        Assert.False(HashSenha.Confere("senha-errada", hash));
    }

    [Fact]
    public void Duas_geracoes_da_mesma_senha_dao_hashes_diferentes()
    {
        // Salt aleatorio por hash: dois usuarios com a mesma senha nao podem ter a mesma
        // linha no banco, senao um vazamento revela quem compartilha senha.
        var a = HashSenha.Gerar("mesma-senha");
        var b = HashSenha.Gerar("mesma-senha");

        Assert.NotEqual(a, b);
        Assert.True(HashSenha.Confere("mesma-senha", a));
        Assert.True(HashSenha.Confere("mesma-senha", b));
    }

    [Fact]
    public void Formato_carrega_as_iteracoes_para_poder_aumenta_las_depois()
    {
        var partes = HashSenha.Gerar("x").Split('$');

        Assert.Equal(4, partes.Length);
        Assert.Equal("pbkdf2", partes[0]);
        Assert.Equal(100_000, int.Parse(partes[1]));
    }

    [Theory]
    [InlineData(null)]                                   // sem hash (convidado)
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nao-e-um-hash")]                        // sem os 4 campos
    [InlineData("md5$1000$c2FsdA==$aGFzaA==")]           // algoritmo trocado
    [InlineData("pbkdf2$abc$c2FsdA==$aGFzaA==")]         // iteracoes nao numericas
    [InlineData("pbkdf2$100000$nao-base64$aGFzaA==")]    // salt corrompido
    public void Rejeita_hash_adulterado_sem_lancar(string? adulterado)
    {
        Assert.False(HashSenha.Confere("qualquer-senha", adulterado));
    }

    [Fact]
    public void Hash_com_ultimo_byte_trocado_nao_confere()
    {
        var hash = HashSenha.Gerar("senha-real");
        var partes = hash.Split('$');

        // Vira um bit do digest e remonta: o FixedTimeEquals tem que recusar.
        var digest = Convert.FromBase64String(partes[3]);
        digest[^1] ^= 0x01;
        var adulterado = $"{partes[0]}${partes[1]}${partes[2]}${Convert.ToBase64String(digest)}";

        Assert.False(HashSenha.Confere("senha-real", adulterado));
    }
}
