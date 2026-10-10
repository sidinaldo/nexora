using Nexora.Core.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>O selo da etapa e a faixa de "Abrir negociação" da caixa (BUG-XX). Eram calculados no
/// painel sem saber se havia negócio aberto: o cliente recorrente negociando de novo em "Proposta"
/// aparecia como "Pedido concluído", e a faixa dizia "Negociação encerrada."</summary>
public class RotuloDaCaixaTests
{
    private static ConversaResumo Conversa(
        string? etapa, bool ganhou, int vendasEmAberto, bool aberto) =>
        new(1, 2, "Maria", "5584988887777", null, null, DateTime.UtcNow, null, 0, "aberta",
            null, null, null, etapa, [], false, ganhou, null, vendasEmAberto, [],
            "evolution", null, null, aberto ? [$"Vendas · {etapa}"] : []);

    [Fact]
    public void VENDA_ENTREGUE_E_NADA_ABERTO_E_VENDA_CONCLUIDA() =>
        Assert.Equal(["Venda concluída"], Conversa("Venda", ganhou: true, vendasEmAberto: 0, aberto: false).SelosEtapa);

    [Fact]
    public void QUEM_COMPROU_E_NEGOCIA_DE_NOVO_MOSTRA_A_ETAPA_COM_O_FUNIL() =>
        Assert.Equal(["Vendas · Proposta"], Conversa("Proposta", ganhou: true, vendasEmAberto: 0, aberto: true).SelosEtapa);

    [Fact]
    public void COM_PEDIDO_A_CAMINHO_CONTINUA_A_ETAPA() =>
        Assert.Equal(["Venda"], Conversa("Venda", ganhou: true, vendasEmAberto: 1, aberto: false).SelosEtapa);

    [Fact]
    public void SEM_NEGOCIO_E_SEM_FUNIL() =>
        Assert.Equal(["Sem funil"], Conversa(null, ganhou: false, vendasEmAberto: 0, aberto: false).SelosEtapa);

    /// <summary>BUG-XX: em dois funis, a linha mostrava só a etapa da mais recente, sem o funil.</summary>
    [Fact]
    public void EM_DOIS_FUNIS_SAI_UM_SELO_POR_NEGOCIACAO()
    {
        var c = Conversa("Entrada", false, 0, false) with
        {
            EtapasAbertas = ["Vendas · Primeiro Atendimento", "Pós-venda · Entrada"]
        };
        Assert.Equal(["Vendas · Primeiro Atendimento", "Pós-venda · Entrada"], c.SelosEtapa);
        Assert.True(c.TemNegocioAberto);
    }

    [Fact]
    public void A_FAIXA_DIZ_COM_QUEM_O_VENDEDOR_FALA()
    {
        Assert.Equal("Ainda não tem negociação.", Conversa(null, false, 0, false).FaixaNegocio);
        Assert.StartsWith("Já há uma negociação em andamento", Conversa("Proposta", false, 0, true).FaixaNegocio);
        Assert.Equal("Cliente recorrente.", Conversa("Venda", true, 0, false).FaixaNegocio);
        Assert.Equal("Negociação encerrada.", Conversa("Proposta", false, 0, false).FaixaNegocio);
    }
}
