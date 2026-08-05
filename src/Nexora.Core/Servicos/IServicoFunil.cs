namespace Nexora.Core.Servicos;

/// <summary>Uma coluna do quadro: a etapa, seus números e a PRIMEIRA página de cards.
///
/// `Total` e `ValorTotal` vêm agregados do SQL e são do conjunto INTEIRO, não da página — o
/// cabeçalho da coluna mostra "38 · R$ 47.500" mesmo com 50 cards carregados.</summary>
public record ColunaFunil(
    long EtapaId,
    string Nome,
    short Ordem,
    string Cor,
    bool EGanho,
    int Total,
    decimal ValorTotal,
    IReadOnlyList<ContatoCard> Contatos,
    bool TemMais);

public record QuadroFunil(IReadOnlyList<ColunaFunil> Colunas);

/// <summary>Para onde o card foi solto.
///
/// `AposContatoId` é o card ACIMA do ponto de soltura (null = topo da coluna). Um campo só, em
/// vez de mandar os dois vizinhos: o cliente sabe onde soltou, e pedir os dois abre a porta para
/// o par chegar inconsistente — dois vizinhos que não são vizinhos entre si produziriam uma
/// ordem sem sentido, e o servidor não teria como saber.</summary>
/// <summary>`Versao` é o `xmin` que o cliente recebeu junto do card.
///
/// ===================== POR QUE ELE EXISTE =====================
/// Dois vendedores arrastando o mesmo card ao mesmo tempo: sem o token, o último a soltar
/// vence e o primeiro nunca sabe que sua ação foi desfeita — o card simplesmente aparece em
/// outro lugar no próximo carregamento.
///
/// NULO é aceito de propósito: `MarcarGanhoAsync` e outros caminhos que movem o card não vêm
/// do arrasto e não têm versão para mandar. Exigir sempre quebraria a porta única do ganho.
/// ==============================================================</summary>
public record MoverContato(long EtapaId, long? AposContatoId, uint? Versao = null);

public interface IServicoFunil
{
    /// <summary>O quadro inteiro: todas as etapas, cada uma com contagem, soma e os `porColuna`
    /// primeiros cards.
    ///
    /// PAGINA POR COLUNA, sempre. Uma empresa com 3.000 leads em "Novo Lead" derrubaria a tela se
    /// o quadro carregasse a coluna inteira — e é o tipo de problema que só aparece no cliente
    /// grande, que é o pior momento para descobrir.</summary>
    Task<QuadroFunil> QuadroAsync(int porColuna, CancellationToken ct);

    /// <summary>Mais cards de UMA coluna, por cursor.
    ///
    /// Aqui é cursor e não offset, ao contrário da lista de contatos: a coluna do kanban se
    /// reordena o tempo todo — é literalmente a tela onde o vendedor arrasta cards — e offset
    /// pularia ou repetiria card entre páginas. O cursor é o par (ordem_kanban, id) do último
    /// card carregado, que é a mesma ordenação do índice ix_contatos_kanban.</summary>
    Task<PaginaCursor<ContatoCard>> ColunaAsync(
        long etapaId, decimal? cursorOrdem, long? cursorId, int tamanho, CancellationToken ct);

    /// <summary>Move o card entre etapas ou o reordena dentro da própria etapa. É a MESMA
    /// operação: o cliente manda etapa de destino e o card de cima, e não precisa saber se
    /// mudou de coluna.
    ///
    /// RECUSA a etapa de ganho — ver IServicoContatos.MarcarGanhoAsync para o porquê.
    ///
    /// Devolve a nova `ordem_kanban` para o cliente conferir contra o valor otimista que ele
    /// pintou na tela: se divergir (porque houve renormalização), ele recarrega a coluna.</summary>
    Task<decimal> MoverAsync(long contatoId, MoverContato destino, CancellationToken ct);
}
