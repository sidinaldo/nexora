using System.Linq.Expressions;

namespace Nexora.Core.Entidades;

/// <summary>===================== O QUE ESTE VENDEDOR TEM PARA FAZER HOJE =====================
///
/// Uma pergunta, uma definição. Ela é feita em DOIS lugares — a lista do Meu Dia e o contador ao
/// lado do item no menu —, e os dois precisam concordar sempre.
///
/// ⚠️ ESCREVER O PREDICADO POR EXTENSO NOS DOIS É O DEFEITO, não a economia. Este projeto já tem a
/// cicatriz: `RegrasNegociacao.NoQuadro` nasceu porque o mesmo recorte, escrito à mão em dois
/// serviços, divergiu — e o cliente via 72 no painel e contava 69 cards no quadro. Ele não conclui
/// "há um filtro diferente"; conclui que os números do sistema não são confiáveis.
///
/// Aqui seria o contador dizendo 3 e a tela mostrando 2, lado a lado, na mesma janela.
/// ==============================================================================</summary>
public static class RegrasLembrete
{
    /// <summary>Os pendentes que são DESTE vendedor e já venceram ou vencem hoje.
    ///
    /// ⚠️ `DataAlvo &lt;= hoje`, NUNCA `==`. Com igualdade estrita, um dia de folga faz a tarefa
    /// sumir da lista para sempre — ela vence numa terça em que ninguém abriu o painel e nunca
    /// mais aparece. O `&lt;=` é o que faz o atrasado continuar cobrando.
    ///
    /// ⚠️ `ResponsavelId == null` CONTA, e é a metade fácil de esquecer. Lembrete sem dono é de
    /// todo mundo — e é justamente o mais provável de ser esquecido, porque ninguém o reivindicou.
    /// Deixá-lo de fora faria o produto calar sobre a tarefa órfã.
    ///
    /// É `Expression` e não `Func` porque ela precisa virar SQL: um `Func` traria a tabela inteira
    /// para a memória e filtraria aqui — o que num cliente com dois anos de uso é a diferença entre
    /// uma contagem indexada e uma varredura, a cada 45 segundos.
    ///
    /// Espelha `ix_lembretes_dia` (`empresa_id, data_alvo, responsavel_id` com
    /// `WHERE status = 'pendente'`), que foi criado para esta pergunta.</summary>
    public static Expression<Func<Lembrete, bool>> MeusDeHoje(long meuId, DateOnly hoje) =>
        l => l.Status == StatusLembrete.Pendente
             && l.DataAlvo <= hoje
             && (l.ResponsavelId == meuId || l.ResponsavelId == null);
}
