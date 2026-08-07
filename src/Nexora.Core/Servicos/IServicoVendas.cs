namespace Nexora.Core.Servicos;

/// <summary>Uma venda na tela do contato. `CanceladaEm` vem preenchida em vez de a linha sumir:
/// a lista mostra a venda riscada, porque "o valor mudou de 5.000 para 3.000" e informacao, e
/// uma linha que desaparece nao explica nada a quem confere o mes depois.</summary>
public record VendaDto(
    long Id,
    decimal Valor,
    DateTime FechadaEm,
    long? ResponsavelId,
    string? ResponsavelNome,
    string? Observacao,
    DateTime? CanceladaEm);

/// <summary>O que a tela precisa saber sobre o histórico do contato ANTES de reabrir.
///
/// Um contato que ja comprou nao e um lead: e cliente. O vendedor precisa saber disso na hora
/// em que vai falar com ele — e informacao comercial, nao enfeite.</summary>
public record ResumoVendasContato(int Quantidade, decimal Total, DateTime? UltimaEm);

public interface IServicoVendas
{
    Task<IReadOnlyList<VendaDto>> DoContatoAsync(long contatoId, CancellationToken ct);

    /// <summary>Desfazer — "marquei errado" —, que NAO e reabrir — "o cliente voltou".
    ///
    /// Marca `cancelada_em`/`cancelada_por`; NUNCA apaga a linha. Se for a venda vigente do
    /// contato, limpa tambem o carimbo (`ganho_em`/`valor`) e devolve o card ao quadro, senao
    /// ele ficaria na etapa de ganho sem venda nenhuma por tras.
    ///
    /// So DONO e GESTOR: cancelar tira faturamento da contagem.</summary>
    Task CancelarAsync(long vendaId, CancellationToken ct);
}
