using Nexora.Core.Entidades;

namespace Nexora.Tests.Unidade;

/// <summary>===================== A MATRIZ DO MOVIMENTO, SEM BANCO (POS-1) =====================
///
/// Aqui a regra fica presa. Os testes de integração provam a FIAÇÃO — que `MoverAsync` e
/// `ValidarEtapaAsync` chamam isto e com os valores certos; a regra em si é uma tabela, e tabela se
/// testa como tabela.
///
/// O funil de referência em todos os casos é o que o produto semeia mais a etapa que o bloco
/// libera:
///
///   1 Novo Lead   2 Proposta   3 Venda (ganho)   4 Pós-Venda   5 Entregue
///
/// ⚠️ CADA LINHA RECUSADA AFIRMA UM TRECHO DA FRASE, e não só "recusou". As frases são o produto:
/// "não volta para a negociação" e "só avança" são as duas recusas que o vendedor mais vai ler, e
/// são diferentes de propósito — uma diz o motivo, a outra o mecanismo. Um teste que só contasse
/// recusas deixaria as duas se trocarem sem reclamar.
/// =============================================================================</summary>
public class RegrasDoQuadroTests
{
    private const short NovoLead = 1, Proposta = 2, Venda = 3, PosVenda = 4, Entregue = 5;

    private static string? Mover(
        StatusNegociacao status, short de, short para,
        bool destinoEGanho = false, short? ganho = Venda, bool trocaDeFunil = false) =>
        RegrasDoQuadro.Recusa(new RegrasDoQuadro.Destino(
            status, de, para, destinoEGanho, ganho, trocaDeFunil));

    // ==================================================================== a porta única do ganho

    [Theory]
    [InlineData(StatusNegociacao.Aberta)]
    [InlineData(StatusNegociacao.Ganha)]
    public void A_ETAPA_DE_VENDA_NAO_RECEBE_ARRASTO_DE_NINGUEM(StatusNegociacao status)
    {
        // A regra mais antiga das três, e a única que vale para TODO status: a venda entra pela
        // tela de venda, com valor. Sem ela existiria card na coluna Venda com status aberta e sem
        // valor — na tela, e invisível no faturamento.
        var r = Mover(status, Proposta, Venda, destinoEGanho: true);

        Assert.Contains("valor fechado", r);
    }

    [Fact]
    public void O_CARD_GANHO_TAMBEM_NAO_REORDENA_DENTRO_DA_PROPRIA_COLUNA_DE_GANHO()
    {
        // ⚠️ ASSIMETRIA ACEITA, e dita aqui para ninguém "consertar" por simetria. A regra 1 é a
        // mais crítica do arquivo; abrir uma exceção de "mesma etapa" nela, para ganhar reordenação
        // numa coluna que esvazia sozinha pelo prazo, é trocar risco real por conforto nenhum.
        var r = Mover(StatusNegociacao.Ganha, Venda, Venda, destinoEGanho: true);

        Assert.NotNull(r);
    }

    // ==================================================================== os estados terminais

    [Fact]
    public void O_PERDIDO_PEDE_PARA_SER_REABERTO()
    {
        Assert.Contains("Reabra", Mover(StatusNegociacao.Perdida, Proposta, NovoLead));
    }

    [Theory]
    [InlineData(StatusNegociacao.Concluida)]
    [InlineData(StatusNegociacao.Cancelada)]
    public void O_PEDIDO_ENCERRADO_NAO_SE_MOVE(StatusNegociacao status)
    {
        // ⚠️ A FRASE MUDOU NESTE BLOCO. Ela era "este negócio já foi fechado e não se move mais no
        // quadro", escrita quando `Ganha` caía neste mesmo ramo. `Ganha` saiu, e uma frase que
        // continuasse dizendo "fechado" passaria a mentir para quem acabou de arrastar um card
        // vendido com sucesso na coluna ao lado.
        var r = Mover(status, Venda, PosVenda);

        Assert.Contains("já foi concluída", r);
        Assert.DoesNotContain("não se move mais no quadro", r);
    }

    // ==================================================================== em negociação

    [Theory]
    [InlineData(NovoLead, Proposta)]   // para frente
    [InlineData(Proposta, NovoLead)]   // e para trás, que é o ponto
    public void O_CARD_EM_NEGOCIACAO_ANDA_NOS_DOIS_SENTIDOS(short de, short para)
    {
        // Só o card VENDIDO é proibido de voltar. Em negociação, soltar na coluna errada é comum e
        // o vendedor desfaz na hora — travar isso transformaria um arrasto errado em chamado.
        Assert.Null(Mover(StatusNegociacao.Aberta, de, para));
    }

    [Theory]
    [InlineData(PosVenda)]
    [InlineData(Entregue)]
    public void O_CARD_EM_NEGOCIACAO_NAO_ENTRA_NA_POS_VENDA(short para)
    {
        // ===================== O DEFEITO QUE ESTA LINHA FECHA =====================
        // Hoje isto PASSA. A etapa de pós-venda não é de ganho, então o único guarda que existia
        // (`if (etapa.EGanho)`) a deixa entrar — e dá para pôr em "Entregue" um negócio que nunca
        // foi vendido, com o faturamento sem saber de nada.
        // =========================================================================
        var r = Mover(StatusNegociacao.Aberta, Proposta, para);

        Assert.Contains("pós-venda", r);
        Assert.Contains("Registre a venda", r);
    }

    // ==================================================================== vendido

    [Theory]
    [InlineData(Venda, PosVenda)]
    [InlineData(PosVenda, Entregue)]
    [InlineData(Venda, Entregue)]       // pular uma etapa é avançar
    [InlineData(PosVenda, PosVenda)]    // reordenar na própria coluna de pós-venda
    public void O_CARD_VENDIDO_AVANCA_NA_POS_VENDA(short de, short para)
    {
        Assert.Null(Mover(StatusNegociacao.Ganha, de, para));
    }

    [Theory]
    [InlineData(PosVenda, Venda)]
    [InlineData(PosVenda, Proposta)]
    [InlineData(Entregue, PosVenda)]
    public void O_CARD_VENDIDO_NAO_VOLTA(short de, short para)
    {
        Assert.NotNull(Mover(StatusNegociacao.Ganha, de, para));
    }

    [Fact]
    public void VOLTAR_PARA_ANTES_DA_VENDA_DIZ_O_MOTIVO_E_NAO_O_MECANISMO()
    {
        // ⚠️ ESTE TESTE É A ORDEM DAS REGRAS 7 E 8. De Pós-Venda para Proposta as duas se aplicam:
        // é antes do ganho E é para trás. Trocar a ordem das duas no arquivo não quebra nada
        // funcionalmente, e troca uma frase útil por uma frase seca — e só isto percebe.
        var r = Mover(StatusNegociacao.Ganha, PosVenda, Proposta);

        Assert.Contains("não volta para antes da venda", r);
    }

    [Fact]
    public void O_CARD_GANHO_PARADO_ANTES_DA_VENDA_SO_ANDA_PARA_FRENTE()
    {
        // Estado que EXISTE hoje, de dois jeitos: linhas antigas, de antes do guarda do E4c/2, e
        // funil sem etapa de ganho. Avançar dentro da negociação continua proibido para ele — a
        // regra 8 é o que o diz, e é o que torna a 7 e a 8 não-redundantes.
        var r = Mover(StatusNegociacao.Ganha, Proposta, NovoLead);

        Assert.Contains("não volta", r);
    }

    [Fact]
    public void O_CARD_VENDIDO_NAO_TROCA_DE_FUNIL()
    {
        // A ordem entre funis diferentes não quer dizer nada, e mover o card vendido para outro
        // funil apaga o registro de onde ele fechou — o mesmo invariante que a porta única protege.
        var r = Mover(StatusNegociacao.Ganha, PosVenda, Entregue, trocaDeFunil: true);

        Assert.Contains("não troca de funil", r);
    }

    [Fact]
    public void O_CARD_EM_NEGOCIACAO_CONTINUA_TROCANDO_DE_FUNIL()
    {
        // Só o vendido está preso ao funil. Mover um lead de "Varejo" para "Atacado" é operação
        // normal, e segue conferida contra a etapa de ganho do funil de DESTINO.
        Assert.Null(Mover(StatusNegociacao.Aberta, Proposta, NovoLead, trocaDeFunil: true));
    }

    // ==================================================================== funil sem etapa de ganho

    [Fact]
    public void SEM_ETAPA_DE_GANHO_O_CARD_EM_NEGOCIACAO_ANDA_EM_QUALQUER_ETAPA()
    {
        // Sem a fronteira, "depois da venda" não existe — e recusar a última coluna de um funil que
        // nunca teve etapa de ganho travaria o quadro de quem configurou assim de propósito.
        Assert.Null(Mover(StatusNegociacao.Aberta, NovoLead, Entregue, ganho: null));
    }

    [Fact]
    public void SEM_ETAPA_DE_GANHO_O_CARD_VENDIDO_AINDA_SO_AVANCA()
    {
        // "Antes da venda" perde o significado; "para frente" não. A regra 8 sobrevive sozinha.
        Assert.Null(Mover(StatusNegociacao.Ganha, Proposta, Entregue, ganho: null));
        Assert.NotNull(Mover(StatusNegociacao.Ganha, Entregue, Proposta, ganho: null));
    }

    // ==================================================================== entrada, e não movimento

    [Fact]
    public void CRIAR_CONTATO_NUMA_ETAPA_DE_POS_VENDA_E_RECUSADO()
    {
        // `OrdemAtual` nulo é a criação de contato com etapa escolhida à mão — não há "de onde".
        // A regra 4 continua valendo, e é o que impede entrar em "Entregue" pela porta do cadastro.
        var r = RegrasDoQuadro.Recusa(new RegrasDoQuadro.Destino(
            StatusNegociacao.Aberta, null, PosVenda, false, Venda, false));

        Assert.Contains("pós-venda", r);
    }

    [Fact]
    public void CRIAR_CONTATO_NUMA_ETAPA_COMUM_PASSA()
    {
        Assert.Null(RegrasDoQuadro.Recusa(new RegrasDoQuadro.Destino(
            StatusNegociacao.Aberta, null, Proposta, false, Venda, false)));
    }
}
