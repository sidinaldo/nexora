using Nexora.Core.Entidades;

namespace Nexora.Tests.Unidade;

/// <summary>O SELO DO CONTATO, sem banco: a expressão compilada sobre negociações na memória.
///
/// O teste de banco (`ContatosDbTests`) prova que o SQL gerado concorda com as abas; este cobre
/// as combinações que o fluxo do produto demora a produzir — e a trava de `SituacaoDe` fora de
/// `Projetar`, que é o erro que ninguém veria no navegador.</summary>
public class SituacaoContatoTests
{
    private static readonly Func<Contato, SituacaoContato> Situacao =
        RegrasNegociacao.Situacao.Compile();

    private static Contato Com(params StatusNegociacao[] status) => new()
    {
        Nome = "Maria", Telefone = "5584988887777",
        Negociacoes = status.Select(s => new Negociacao { Status = s }).ToList()
    };

    [Theory]
    [InlineData(SituacaoContato.SemNegocio)]
    [InlineData(SituacaoContato.Aberto, StatusNegociacao.Aberta)]
    [InlineData(SituacaoContato.Ganho, StatusNegociacao.Ganha)]
    [InlineData(SituacaoContato.Ganho, StatusNegociacao.Concluida)]
    [InlineData(SituacaoContato.Perdido, StatusNegociacao.Perdida)]
    // ⚠️ O CASO DA YSIA: comprou antes e tem negócio aberto hoje. A regra velha do painel dizia
    // "venda fechada"; a aba dizia "Em aberto".
    [InlineData(SituacaoContato.Aberto, StatusNegociacao.Concluida, StatusNegociacao.Aberta)]
    [InlineData(SituacaoContato.Aberto, StatusNegociacao.Ganha, StatusNegociacao.Aberta, StatusNegociacao.Aberta)]
    // Perdeu uma, mas já comprou: é CLIENTE, e está em "Ganhos" — não volta para "Perdidos".
    [InlineData(SituacaoContato.Ganho, StatusNegociacao.Perdida, StatusNegociacao.Concluida)]
    [InlineData(SituacaoContato.Aberto, StatusNegociacao.Perdida, StatusNegociacao.Aberta)]
    public void O_SELO_SEGUE_A_ORDEM_DAS_ABAS(SituacaoContato esperado, params StatusNegociacao[] status)
    {
        Assert.Equal(esperado, Situacao(Com(status)));
    }

    /// <summary>E cada selo cai na aba certa: `Aberto` e `SemNegocio` em "Em aberto", os outros na
    /// aba com o mesmo nome. Compilado das MESMAS expressões que o `ServicoContatos` usa.</summary>
    [Theory]
    [InlineData]
    [InlineData(StatusNegociacao.Aberta)]
    [InlineData(StatusNegociacao.Ganha)]
    [InlineData(StatusNegociacao.Concluida)]
    [InlineData(StatusNegociacao.Perdida)]
    [InlineData(StatusNegociacao.Concluida, StatusNegociacao.Aberta)]
    [InlineData(StatusNegociacao.Perdida, StatusNegociacao.Concluida)]
    [InlineData(StatusNegociacao.Perdida, StatusNegociacao.Ganha, StatusNegociacao.Aberta)]
    public void O_SELO_E_A_ABA_CONCORDAM(params StatusNegociacao[] status)
    {
        var c = Com(status);

        var abas = new[]
        {
            RegrasNegociacao.ContatoEmAberto.Compile()(c),
            RegrasNegociacao.ContatoGanho.Compile()(c),
            RegrasNegociacao.ContatoPerdido.Compile()(c)
        };

        // Uma aba e só uma.
        Assert.Equal(1, abas.Count(x => x));

        var esperada = Situacao(c) switch
        {
            SituacaoContato.Aberto or SituacaoContato.SemNegocio => 0,
            SituacaoContato.Ganho => 1,
            _ => 2
        };
        Assert.True(abas[esperada], $"selo {Situacao(c)} fora da aba dele");
    }

    /// <summary>⚠️ FORA DE `Projetar`, `SituacaoDe` LANÇA. O EF avaliaria a chamada no cliente, com
    /// o contato sem as negociações carregadas, e o selo sairia "sem negócio" para todo mundo — em
    /// silêncio. Lançar transforma esse esquecimento num erro no primeiro teste.</summary>
    [Fact]
    public void SITUACAO_DE_FORA_DE_PROJETAR_LANCA()
    {
        Assert.Throws<InvalidOperationException>(() => RegrasNegociacao.SituacaoDe(Com()));
    }

    /// <summary>E dentro de `Projetar` a chamada vira a expressão — o mesmo resultado dela.</summary>
    [Fact]
    public void PROJETAR_TROCA_A_CHAMADA_PELA_REGRA()
    {
        var projecao = RegrasNegociacao.Projetar((Contato c) => new
        {
            c.Nome,
            Selo = RegrasNegociacao.SituacaoDe(c)
        }).Compile();

        var r = projecao(Com(StatusNegociacao.Concluida, StatusNegociacao.Aberta));

        Assert.Equal("Maria", r.Nome);
        Assert.Equal(SituacaoContato.Aberto, r.Selo);
    }
}
