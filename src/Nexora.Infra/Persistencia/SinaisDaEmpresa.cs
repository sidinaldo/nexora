using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;

namespace Nexora.Infra.Persistencia;

/// <summary>===================== AS PERGUNTAS SOBRE O ESTADO DA EMPRESA (POS-1) =====================
///
/// Perguntas de "esta empresa já saiu do zero?" que MAIS DE UM serviço faz. Mora aqui pelo mesmo
/// motivo que `ConclusaoAutomatica` e `LiberacaoDeCiclo` moram aqui: regra respondida pelo banco,
/// que duas portas diferentes precisam responder IGUAL.
///
/// Nasceu de um defeito em produção. O dashboard tinha a própria resposta — "o quadro está vazio
/// agora" — e o onboarding tinha outra. A empresa que vendeu tudo e concluiu tudo tem o quadro
/// vazio, então a tela a chamava de nova e escondia as vendas dela atrás de um aviso de
/// boas-vindas. Duas respostas para a mesma pergunta, e a mais barata estava errada.
/// ============================================================================================</summary>
public static class SinaisDaEmpresa
{
    /// <summary>A empresa já recebeu alguma mensagem de cliente, em qualquer momento da vida dela?
    ///
    /// ===================== A VERDADE É A MENSAGEM, NÃO A COLUNA =====================
    /// `primeira_mensagem_em` nasceu numa migration, e o webhook só carimba dali em diante. Empresa
    /// que JÁ recebia mensagem antes disso tem a coluna NULL — e responder só por ela diria "nunca
    /// recebeu nada" sobre uma conta em plena operação.
    ///
    /// A coluna entra como ATALHO: preenchida, ela PROVA que a mensagem existiu (quem a escreve é o
    /// mesmo caminho que insere a linha) e poupa uma consulta em `mensagens`, a maior tabela do
    /// banco. NULL, quem responde é a tabela.
    ///
    /// ⚠️ A assimetria é a regra: o atalho pode ficar para trás, nunca pode mentir a favor. É por
    /// isso que o `||` está nesta ordem e não pode virar um `&amp;&amp;` nem perder o segundo lado.
    /// ==============================================================================
    ///
    /// O valor da coluna vem por PARÂMETRO, não de uma consulta aqui dentro: os dois chamadores já
    /// leem a linha de `empresas` para outras coisas, e fazer esta função buscá-la de novo
    /// acrescentaria uma ida ao banco em toda carga de painel para ler algo que o chamador tem na
    /// mão.</summary>
    public static async Task<bool> RecebeuMensagemAsync(
        NexoraDbContext db, DateTime? primeiraMensagemEm, CancellationToken ct)
    {
        if (primeiraMensagemEm is not null) return true;

        return await db.Mensagens.AsNoTracking()
            .AnyAsync(m => m.Direcao == DirecaoMensagem.Entrada, ct);
    }
}
