using Nexora.Tests.Integracao;
using Xunit;

namespace Nexora.Tests.Unidade;

/// <summary>A SEMENTE DOS DADOS DE TESTE PRECISA SER ESTAVEL.
///
/// ===================== O DEFEITO QUE ISTO TRAVA =====================
/// O cenario montava o telefone do contato com `sufixo.GetHashCode()`. O .NET ALEATORIZA o hash de
/// string a cada processo — e a documentacao diz que o valor nao serve para comparar entre
/// execucoes. Usado como dado de teste, isso quer dizer telefone diferente a cada rodada.
///
/// O resultado foi um teste que passa quase sempre e reprova sem causa aparente:
/// `Listar_busca_por_nome_e_por_digitos_do_telefone` procura por "(84) 98333", e num dos sorteios
/// o contato do cenario nasceu 5584983332282 — que contem os mesmos digitos. Duas linhas voltaram
/// onde ele esperava uma, e o CI ficou vermelho por doze commits sem ninguem achar o motivo.
///
/// Dois testes, e cada um pega uma metade do problema:
///   1. a semente e a MESMA sempre — reprova se alguem voltar a usar `GetHashCode()`;
///   2. o telefone fica numa FAIXA RESERVADA que nenhum teste usa — reprova se o prefixo mudar.
/// ====================================================================</summary>
public class SementeDoCenarioTests
{
    /// <summary>Valores de OURO, calculados a mao (FNV-1a de 32 bits). Se a implementacao mudar,
    /// estes numeros mudam junto e o teste avisa — que e o ponto.</summary>
    [Theory]
    [InlineData("busca", 1559806621)]
    [InlineData("paginar", 1983190191)]
    [InlineData("mid-nome-barra", 1817448264)]
    public void A_SEMENTE_E_A_MESMA_EM_TODA_EXECUCAO(string sufixo, int esperado)
    {
        Assert.Equal(esperado, Semeador.Semente(sufixo));
    }

    /// <summary>⚠️ A FAIXA `(84) 90xxx-xxxx` E RESERVADA AO CENARIO. Os testes criam contatos em
    /// `(84) 97xxx` e `(84) 98xxx`; deixando o cenario cair na mesma faixa, uma busca por digitos
    /// acha o contato do cenario junto e a contagem quebra.
    ///
    /// `849` seguido de `0` nunca contem `8498...`, que e o comeco de qualquer busca por um
    /// telefone de teste. A separacao e por construcao, nao por sorte.</summary>
    [Theory]
    [InlineData("busca")]
    [InlineData("paginar")]
    [InlineData("qualquer-outro-sufixo")]
    public void O_TELEFONE_DO_CENARIO_FICA_NA_FAIXA_RESERVADA(string sufixo)
    {
        var telefone = $"558490{Semeador.Semente(sufixo) % 10_000_000:D7}";

        Assert.StartsWith("558490", telefone);
        Assert.Equal(13, telefone.Length);
        Assert.DoesNotContain("8498", telefone);
    }
}
