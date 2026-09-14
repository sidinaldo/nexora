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
    DateTime? CanceladaEm,
    /// <summary>`fechada`, `concluida` ou `cancelada` — em minusculas, como todo enum que sai
    /// da API.</summary>
    string Status,
    DateTime? ConcluidaEm);

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
    Task CancelarAsync(long negociacaoId, CancellationToken ct);

    /// <summary>"Esse pedido acabou" (NEG-2). Tira o card da coluna Venda SEM tirar o dinheiro do
    /// relatorio — e o que impede a coluna de acumular para sempre.
    ///
    /// EM LOTE desde o comeco: um id e uma lista de um. Sem lote o vendedor nao faz, e em tres
    /// meses a coluna volta a acumular — o bloco nao teria resolvido nada.
    ///
    /// QUALQUER PAPEL: e acao operacional do vendedor sobre o proprio pedido, nao decisao de
    /// gestao. E NAO TOCA o contato: `ganho_em` e `valor` ficam como estao, porque concluir e
    /// sobre o pedido, nao sobre o negocio.
    ///
    /// Devolve quantas foram concluidas — o que ja nao estava `fechada` e ignorado em silencio,
    /// para o lote nao falhar inteiro por causa de uma linha que outra pessoa mexeu no meio.</summary>
    /// ⚠️ EXISTIU UM `ConcluirDoContatoAsync` AO LADO DESTE, e ele foi removido por ser um
    /// defeito. A justificativa dele era "o kanban e montado por contato e o card nao conhece o
    /// id da venda" — verdade ate o E4c/2, quando o card passou a SER a negociacao e
    /// `CardFunil.Id` virou o id dela.
    ///
    /// Depois disso o metodo resolvia "as vendas em aberto do contato", e quem tinha dois cards
    /// ganhos via os DOIS sumirem ao concluir um. A premissa tinha caido; o codigo, nao.</summary>
    Task<int> ConcluirAsync(IReadOnlyList<long> negociacaoIds, CancellationToken ct);

}
