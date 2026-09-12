namespace Nexora.Core.Servicos;

/// <summary>Regra de negocio violada. Os servicos de dominio lancam isto; o
/// FiltroRegraDeNegocio traduz para HTTP (409 se for conflito, 400 se for entrada invalida).
///
/// O dominio nao conhece HTTP — e por isso que ele nao pode devolver BadRequest().</summary>
public class RegraDeNegocioException(string mensagem, bool conflito = false) : Exception(mensagem)
{
    /// <summary>True quando o estado atual impede a operacao (vira 409), em vez de
    /// a entrada estar errada (400).</summary>
    public bool Conflito { get; } = conflito;

    /// <summary>===================== O TERCEIRO STATUS, QUANDO DOIS NAO BASTAM =====================
    /// O par 400/409 cobre quase tudo: ou a ENTRADA esta errada, ou o ESTADO impede. Ha um caso que
    /// nao e nenhum dos dois — a entrada esta perfeita, o estado nao conflita com ela, e mesmo assim
    /// a operacao nao pode acontecer porque esbarra num TETO. "A empresa ja tem 60 etiquetas" e
    /// exatamente isso: o nome e valido, nenhuma outra etiqueta o disputa, e a resposta ainda e nao.
    ///
    /// Nesse caso o servico informa o status aqui, e ele vence o `Conflito`. Fica como propriedade
    /// opcional em vez de um parametro a mais no construtor porque as ~30 chamadas existentes nao
    /// precisam saber que isto passou a existir.
    ///
    /// ⚠️ Use com parcimonia. Tres status ja e o limite do que vale a pena distinguir num cliente
    /// que, na pratica, so le a mensagem — a tela mostra `{ erro }` e nao olha o numero.
    /// ====================================================================================</summary>
    public int? StatusHttp { get; init; }
}
