using Nexora.Core.Entidades;

namespace Nexora.Core.Conversoes;

/// <summary>Põe um evento de conversão na fila. Um INSERT, e nada mais (INT-4).
///
/// ===================== NUNCA LANÇA, NUNCA BLOQUEIA =====================
/// Os dois chamadores são caminhos que não podem falhar por causa disto: a captação de um lead e o
/// fechamento de uma venda. Perder a atribuição é ruim; recusar o lead ou impedir o vendedor de
/// marcar a venda como ganha é inaceitável.
///
/// E não toca a rede: quem entrega é o motor, numa rodada própria.
/// ======================================================================</summary>
public interface IPublicadorConversoes
{
    /// <summary>O contato acabou de entrar. `Lead` sai para TODO contato novo, com rastro ou sem —
    /// o casamento por telefone e e-mail funciona sozinho, e é o que faz isto valer para a PME que
    /// não tem site. O rastro MELHORA a atribuição, não a habilita.</summary>
    Task PublicarLeadAsync(Contato contato, CancellationToken ct = default);

    /// <summary>A venda fechou. Vai com o VALOR — é ele que ensina a Meta a procurar quem compra,
    /// em vez de quem preenche formulário.</summary>
    Task PublicarCompraAsync(long negociacaoId, CancellationToken ct = default);
}
