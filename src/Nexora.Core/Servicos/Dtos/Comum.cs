namespace Nexora.Core.Servicos;

// Os contratos que atravessam a fronteira Core -> Api. Existem porque o controller NAO pode
// conhecer o DbContext: sem DTO, a projecao teria que ser um tipo anonimo dentro de um
// IQueryable, e IQueryable so existe na Infra. Sao records de LEITURA — nao sao as entidades,
// e nao devem virar as entidades.

/// <summary>===================== A PÁGINA NUMERADA, COM TUDO PRONTO (AUD-XX) =====================
/// `{ itens, totalCount, pagina, tamanhoPagina, totalPaginas }`: a tela desenha "Página 3 de 12 ·
/// 230 eventos" sem fazer conta nenhuma. O `TotalCount` sai de um `CountAsync` com os MESMOS
/// filtros da página, e não de uma contagem lida de dentro das linhas — que some na página além do
/// fim (B5).
///
/// É a página de TODA tabela do painel (AUD-XX, #21). As listas que vinham inteiras e eram
/// recortadas no navegador (equipe, feriados, canais, formulários) passaram a paginar no banco, e
/// a tela parou de calcular "Página X de Y".
/// ==========================================================================================</summary>
public record PaginaComTotal<T>(
    IReadOnlyList<T> Itens,
    int TotalCount,
    int Pagina,
    int TamanhoPagina,
    int TotalPaginas)
{
    public static PaginaComTotal<T> De(IReadOnlyList<T> itens, int totalCount, int pagina, int tamanhoPagina) =>
        new(itens, totalCount, pagina, tamanhoPagina, Paginacao.TotalDePaginas(totalCount, tamanhoPagina));
}

public static class Paginacao
{
    /// <summary>Quantas páginas cabem `total` itens. Lista vazia tem UMA página — a vazia —, para a
    /// tela nunca mostrar "página 1 de 0".</summary>
    public static int TotalDePaginas(int total, int tamanhoPagina)
    {
        if (total == 0)
        {
            return 1;
        }

        return (total + tamanhoPagina - 1) / tamanhoPagina;
    }
}

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
