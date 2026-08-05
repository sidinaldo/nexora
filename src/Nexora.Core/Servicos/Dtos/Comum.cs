namespace Nexora.Core.Servicos;

// Os contratos que atravessam a fronteira Core -> Api. Existem porque o controller NAO pode
// conhecer o DbContext: sem DTO, a projecao teria que ser um tipo anonimo dentro de um
// IQueryable, e IQueryable so existe na Infra. Sao records de LEITURA — nao sao as entidades,
// e nao devem virar as entidades.

public record Pagina<T>(int Total, int NumeroPagina, int Tamanho, IReadOnlyList<T> Itens);

/// <summary>Pagina por CURSOR (nao por offset). Usada onde a lista se REORDENA em tempo real
/// (a caixa de entrada: conversa nova sobe pro topo) — com offset, uma pagina seguinte
/// duplicaria ou pularia itens. O cursor e o (ordenacao, id) do ULTIMO item carregado; o
/// cliente o devolve para pedir mais. TemMais indica se ha pagina seguinte.
///
/// A implementacao chega junto com a caixa de entrada. O que NAO se replica do Recupera e
/// paginar em memoria: la o ServicoInbox materializa todos os tickets do status antes de
/// cortar a pagina, e o proprio comentario admite que "Resolvidas cresce". No Nexora a
/// paginacao acontece no SQL.</summary>
public record PaginaCursor<T>(IReadOnlyList<T> Itens, bool TemMais);
