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
    /// <summary>Quantas vendas ja foram CONCLUIDAS nesta etapa (NEG-2). So tem sentido na etapa
    /// de ganho; zero nas outras.
    ///
    /// O cabecalho mostra "2 em aberto · 41 concluidas" — sem esse segundo numero, a coluna
    /// esvaziando pareceria perda de dado, e o vendedor deixaria de concluir.</summary>
    int Concluidas,
    IReadOnlyList<CardFunil> Contatos,
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
/// <summary>O card do kanban. Projeção MAIS ENXUTA que a da lista de propósito: o quadro carrega
/// dezenas de cards por coluna e não mostra e-mail, origem nem data de criação. Cada campo a
/// mais aqui é multiplicado pelo número de cards na tela.
///
/// ===================== POR QUE ELE DEIXOU DE SE CHAMAR `CardFunil` =====================
/// Porque o card DEIXOU DE SER O CONTATO. Até o E4c/1 os dois eram a mesma linha, e o nome
/// estava certo. Agora `Id` é o id da NEGOCIAÇÃO, e a mesma pessoa pode ter dois cards.
///
/// Manter o nome antigo com o significado novo seria a pior combinação possível: todo mundo que
/// lesse `CardFunil.Id` e passasse esse número para uma API de contato escreveria um bug que
/// compila.
/// ======================================================================================</summary>
public record CardFunil(
    /// <summary>O id da NEGOCIAÇÃO — é ele que vai no arrasto.</summary>
    long Id,
    /// <summary>O id do CONTATO, que é outra coisa desde o E4c/2.
    ///
    /// Tudo que é da PESSOA continua indo por aqui: abrir o contato, aplicar etiqueta, registrar
    /// a venda, carregar os canais do fechamento. Só a posição no quadro é da negociação.</summary>
    long ContatoId,
    string Nome,
    string Telefone,
    decimal OrdemKanban,
    decimal? Valor,
    long? ResponsavelId,
    string? ResponsavelNome,
    long? ConversaId,
    DateTime? AguardandoDesde,
    int NaoLidas,
    DateTime? UltimaMensagemEm,
    /// <summary>NEG-3 · a campanha detectada NESTE ciclo, ou nulo.
    ///
    /// Responde, sem abrir o card, a pergunta que o vendedor faz olhando o quadro: "por que este
    /// lead esta aqui". Sem ela o codigo do QR ficava gravado e invisivel ate a venda fechar.</summary>
    string? CanalDoCiclo,
    /// <summary>O `xmin` da NEGOCIAÇÃO. O cliente devolve isto ao arrastar, e o servidor recusa
    /// (409) se outra pessoa mexeu no card no meio do caminho.
    ///
    /// ⚠️ Era o do contato até o E4c/1. Trocou junto com o dono da posição no quadro: é a
    /// negociação que se move, e é a linha dela que o UPDATE precisa proteger.</summary>
    uint Versao,
    /// <summary>As etiquetas coladas neste contato.
    ///
    /// ⚠️ O card CORTA no que couber numa linha (`.chips-linha`): o quadro perde valor se cada
    /// card crescer porque alguem marcou oito. Quem quer a lista inteira abre o contato.</summary>
    IReadOnlyList<EtiquetaDto> Etiquetas);

/// <summary>Para onde o card vai.
///
/// ⚠️ `AposNegociacaoId`, e não `AposContatoId`: desde o E4c/2 a posição é da negociação, e um
/// contato pode ter duas no quadro. Mandar o id do contato aqui posicionaria contra a linha
/// errada — e o card cairia num lugar diferente do que a pessoa soltou.</summary>
public record MoverContato(long EtapaId, long? AposNegociacaoId, uint? Versao = null);

public interface IServicoFunil
{
    /// <summary>O quadro inteiro: todas as etapas, cada uma com contagem, soma e os `porColuna`
    /// primeiros cards.
    ///
    /// PAGINA POR COLUNA, sempre. Uma empresa com 3.000 leads em "Novo Lead" derrubaria a tela se
    /// o quadro carregasse a coluna inteira — e é o tipo de problema que só aparece no cliente
    /// grande, que é o pior momento para descobrir.</summary>
    /// <param name="pipelineId">⚠️ QUAL FUNIL. Antes das pipelines a empresa tinha um só e a
    /// pergunta não existia. Sem este parâmetro o quadro montaria com as etapas de TODOS os funis
    /// lado a lado — dez colunas de três processos diferentes, sem nada indicando onde um termina
    /// e o outro começa.</param>
    Task<QuadroFunil> QuadroAsync(long pipelineId, int porColuna, CancellationToken ct);

    /// <summary>Mais cards de UMA coluna, por cursor.
    ///
    /// Aqui é cursor e não offset, ao contrário da lista de contatos: a coluna do kanban se
    /// reordena o tempo todo — é literalmente a tela onde o vendedor arrasta cards — e offset
    /// pularia ou repetiria card entre páginas. O cursor é o par (ordem_kanban, id) do último
    /// card carregado, que é a mesma ordenação do índice ix_contatos_kanban.</summary>
    Task<PaginaCursor<CardFunil>> ColunaAsync(
        long etapaId, decimal? cursorOrdem, long? cursorId, int tamanho, CancellationToken ct);

    /// <summary>Move o card entre etapas ou o reordena dentro da própria etapa. É a MESMA
    /// operação: o cliente manda etapa de destino e o card de cima, e não precisa saber se
    /// mudou de coluna.
    ///
    /// RECUSA a etapa de ganho — ver IServicoContatos.MarcarGanhoAsync para o porquê.
    ///
    /// Devolve a nova `ordem_kanban` para o cliente conferir contra o valor otimista que ele
    /// pintou na tela: se divergir (porque houve renormalização), ele recarrega a coluna.</summary>
    Task<decimal> MoverAsync(long negociacaoId, MoverContato destino, CancellationToken ct);
}
