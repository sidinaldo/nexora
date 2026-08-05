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
}
