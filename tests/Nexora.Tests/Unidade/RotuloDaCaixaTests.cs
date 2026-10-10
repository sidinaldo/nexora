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
            "evolution", null, null, aberto ? [new SeloEtapa(9, $"Vendas · {etapa}", [], "negociacao")] : []);

    [Fact]
    public void VENDA_ENTREGUE_E_NADA_ABERTO_E_VENDA_CONCLUIDA() =>
        Assert.Equal(["Venda concluída"], Conversa("Venda", ganhou: true, vendasEmAberto: 0, aberto: false).SelosEtapa.Select(s => s.Rotulo));

    [Fact]
    public void QUEM_COMPROU_E_NEGOCIA_DE_NOVO_MOSTRA_A_ETAPA_COM_O_FUNIL() =>
        Assert.Equal(["Vendas · Proposta"], Conversa("Proposta", ganhou: true, vendasEmAberto: 0, aberto: true).SelosEtapa.Select(s => s.Rotulo));

    [Fact]
    public void COM_PEDIDO_A_CAMINHO_CONTINUA_A_ETAPA() =>
        Assert.Equal(["Venda"], Conversa("Venda", ganhou: true, vendasEmAberto: 1, aberto: false).SelosEtapa.Select(s => s.Rotulo));

    [Fact]
    public void SEM_NEGOCIO_E_SEM_FUNIL() =>
        Assert.Equal(["Sem funil"], Conversa(null, ganhou: false, vendasEmAberto: 0, aberto: false).SelosEtapa.Select(s => s.Rotulo));

    /// <summary>BUG-XX: em dois funis, a linha mostrava só a etapa da mais recente, sem o funil.</summary>
    [Fact]
    public void EM_DOIS_FUNIS_SAI_UM_SELO_POR_NEGOCIACAO()
    {
        var c = Conversa("Entrada", false, 0, false) with
        {
            NegociacoesAbertas =
            [
                new SeloEtapa(1, "Vendas · Primeiro Atendimento", [new EtiquetaDto(5, "Urgente", "#C0392B")], "negociacao"),
                new SeloEtapa(2, "Pós-venda · Entrada", [], "negociacao")
            ]
        };
        Assert.Equal(["Vendas · Primeiro Atendimento", "Pós-venda · Entrada"], c.SelosEtapa.Select(s => s.Rotulo));
        Assert.Equal("Urgente", Assert.Single(c.SelosEtapa[0].Etiquetas).Nome);
        Assert.True(c.TemNegocioAberto);
    }

    /// <summary>O verde e o tracejado eram decididos no painel, com a conta dos selos repetida lá.</summary>
    [Fact]
    public void O_TIPO_DO_SELO_DIZ_COMO_PINTAR()
    {
        Assert.Equal("concluida", Conversa("Venda", true, 0, false).SelosEtapa[0].Tipo);
        Assert.Equal("sem_funil", Conversa(null, false, 0, false).SelosEtapa[0].Tipo);
        Assert.Equal("etapa", Conversa("Venda", true, 1, false).SelosEtapa[0].Tipo);
        Assert.Equal("negociacao", Conversa("Proposta", true, 0, true).SelosEtapa[0].Tipo);
    }

    private static readonly EtiquetaDto Inbound = new(1, "Inbound/Outbound", "#C2185B");
    private static readonly EtiquetaDto Urgente = new(2, "Urgente", "#C0392B");
    private static readonly EtiquetaDto Peca = new(3, "Aguardando peça", "#B07A00");

    /// <summary>BUG-XX: as da pessoa e as das negociações juntas numa linha, as da pessoa primeiro.</summary>
    [Fact]
    public void AS_ETIQUETAS_SAEM_JUNTAS_AS_DA_PESSOA_PRIMEIRO()
    {
        var c = Conversa("Entrada", false, 0, false) with
        {
            Etiquetas = [Inbound],
            NegociacoesAbertas = [new SeloEtapa(1, "Vendas · Entrada", [Urgente], "negociacao")]
        };

        Assert.Equal([("Inbound/Outbound", false), ("Urgente", true)],
            c.EtiquetasDaLinha.Select(e => (e.Nome, e.Clara)));
        Assert.Null(c.EtiquetasDaLinha[0].Titulo);
        Assert.Equal("Na negociação Vendas · Entrada", c.EtiquetasDaLinha[1].Titulo);
    }

    /// <summary>BUG-XX: "Inbound/Outbound" na pessoa e na negociação aparecia duas vezes na linha.</summary>
    [Fact]
    public void A_QUE_ESTA_NA_PESSOA_E_NA_NEGOCIACAO_SAI_UMA_VEZ_CHEIA()
    {
        var c = Conversa("Entrada", false, 0, false) with
        {
            Etiquetas = [Inbound],
            NegociacoesAbertas = [new SeloEtapa(1, "Vendas · Entrada", [Inbound, Urgente], "negociacao")]
        };

        Assert.Equal(["Inbound/Outbound", "Urgente"], c.EtiquetasDaLinha.Select(e => e.Nome));
        Assert.False(c.EtiquetasDaLinha[0].Clara);
    }

    [Fact]
    public void A_MESMA_EM_DUAS_NEGOCIACOES_SAI_UMA_VEZ_COM_AS_DUAS_NO_TITULO()
    {
        var c = Conversa("Entrada", false, 0, false) with
        {
            NegociacoesAbertas =
            [
                new SeloEtapa(1, "Vendas · Entrada", [Peca], "negociacao"),
                new SeloEtapa(2, "Pós-venda · Entrada", [Peca], "negociacao")
            ]
        };

        var unica = Assert.Single(c.EtiquetasDaLinha);
        Assert.True(unica.Clara);
        Assert.Equal("Nas negociações Vendas · Entrada e Pós-venda · Entrada", unica.Titulo);
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
