namespace Nexora.Core.Servicos;

/// <summary>Uma linha da lista de contatos.
///
/// `GanhoEm` e `PerdidoEm` vão como TIMESTAMP, não como rótulo "ganho"/"perdido": o cliente
/// precisa da data para exibir "fechado há 3 dias", e um rótulo pronto obrigaria o servidor a
/// decidir formatação. Mesmo princípio do `AguardandoDesde` no semáforo.</summary>
public record ContatoResumo(
    long Id,
    string Nome,
    string Telefone,
    string? Email,
    string Origem,
    long EtapaId,
    string EtapaNome,
    decimal OrdemKanban,
    long? ResponsavelId,
    string? ResponsavelNome,
    decimal? Valor,
    DateTime? GanhoEm,
    DateTime? PerdidoEm,
    DateTime CriadoEm,
    long? ConversaId,
    DateTime? AguardandoDesde,
    int NaoLidas);

/// <summary>O card do kanban. Projeção MAIS ENXUTA que a da lista de propósito: o quadro carrega
/// dezenas de cards por coluna e não mostra e-mail, origem nem data de criação. Cada campo a
/// mais aqui é multiplicado pelo número de cards na tela.</summary>
public record ContatoCard(
    long Id,
    string Nome,
    string Telefone,
    decimal OrdemKanban,
    decimal? Valor,
    /// <summary>Quantas vendas EM ABERTO este contato tem (NEG-2).
    ///
    /// O quadro e montado por CONTATO, e quem comprou tres vezes aparece num card so. O numero
    /// resolve o que a tela precisa mostrar sem trocar o modelo do kanban por um de vendas.</summary>
    int VendasEmAberto,
    long? ResponsavelId,
    string? ResponsavelNome,
    long? ConversaId,
    DateTime? AguardandoDesde,
    int NaoLidas,
    DateTime? UltimaMensagemEm,
    /// <summary>O `xmin` da linha. O cliente devolve isto ao arrastar, e o servidor recusa (409)
    /// se outra pessoa mexeu no card no meio do caminho.</summary>
    uint Versao);

/// <summary>O detalhe: tudo do contato mais a conversa e os lembretes dele.
///
/// Vem numa chamada só porque a tela de detalhe mostra as três coisas juntas — três requisições
/// para montar uma tela é latência que o vendedor sente ao abrir cada card.</summary>
public record ContatoDetalhe(
    ContatoResumo Contato,
    string? OrigemDetalhe,
    string? Observacoes,
    string? MotivoPerda,
    DateTime? AnonimizadoEm,
    DateTime? UltimaMensagemEm,
    IReadOnlyList<LembreteDto> Lembretes);

public record NovoContato(
    string Nome,
    string Telefone,
    string? Email = null,
    string? Origem = null,
    string? OrigemDetalhe = null,
    long? EtapaId = null,
    long? ResponsavelId = null,
    decimal? Valor = null,
    string? Observacoes = null);

public record EditarContato(
    string Nome,
    string Telefone,
    string? Email = null,
    string? Origem = null,
    string? OrigemDetalhe = null,
    long? ResponsavelId = null,
    decimal? Valor = null,
    string? Observacoes = null);

/// <summary>Recorte da lista. `Abertos` é o default: contato ganho ou perdido é histórico, e a
/// lista de trabalho do vendedor não deve começar cheia de coisa fechada.</summary>
public enum FiltroContato
{
    Abertos,
    Ganhos,
    Perdidos,
    Todos
}

public interface IServicoContatos
{
    /// <summary>A lista, paginada por OFFSET com total — não por cursor.
    ///
    /// Aqui offset é seguro, e cursor seria pior: a ordenação é por nome (ou por data de
    /// criação), que NÃO se reordena sozinha entre requisições. O cursor existe na caixa de
    /// entrada porque lá conversa nova sobe para o topo enquanto o vendedor rola. Um contato não
    /// muda de nome sozinho. E o total permite mostrar "142 contatos", que a lista precisa.
    ///
    /// Filtro, busca, contagem e corte acontecem TODOS no SQL.</summary>
    Task<Pagina<ContatoResumo>> ListarAsync(
        FiltroContato filtro, string? busca, long? etapaId, long? responsavelId,
        int pagina, int tamanho, CancellationToken ct);

    Task<ContatoDetalhe> DetalheAsync(long id, CancellationToken ct);

    Task<long> CriarAsync(NovoContato novo, CancellationToken ct);

    Task AtualizarAsync(long id, EditarContato dados, CancellationToken ct);

    /// <summary>=========== A PORTA ÚNICA DO GANHO ===========
    ///
    /// Exige valor. Além de carimbar `ganho_em`, MOVE o card para a etapa de ganho — é o que
    /// permite ao cliente tratar "arrastar para Venda" e "clicar em Venda fechada" como a mesma
    /// operação. O `MoverAsync` recusa a etapa de ganho justamente para forçar tudo por aqui: se
    /// as duas portas escrevessem por rotas diferentes, existiria contato na coluna Venda sem
    /// `ganho_em` e sem `valor` — e o dashboard não saberia contá-lo.
    ///
    /// Empresa sem etapa marcada `e_ganho` (o índice único permite zero) mantém a etapa atual: o
    /// carimbo é o que importa para o dashboard, a coluna é conveniência visual.</summary>
    Task MarcarGanhoAsync(long id, decimal valor, CancellationToken ct);

    /// <summary>Exige motivo. NÃO muda de etapa: o índice parcial ix_contatos_kanban já filtra
    /// `perdido_em IS NULL`, então o card sai do quadro sozinho, e preservar a etapa registra
    /// ONDE a negociação morreu — que é a informação útil depois.</summary>
    Task MarcarPerdidoAsync(long id, string motivo, CancellationToken ct);

    /// <summary>Desfaz ganho ou perda. PRESERVA o `valor`: ele é a estimativa do negócio, não o
    /// registro da venda, e apagá-lo obrigaria o vendedor a digitar de novo ao reabrir.</summary>
    Task ReabrirAsync(long id, CancellationToken ct);

    /// <summary>LGPD: zera a PII e preserva o histórico. Sem delete físico, sem soft delete —
    /// conversa, mensagens, lembretes, etapa e valor continuam de pé.</summary>
    Task AnonimizarAsync(long id, CancellationToken ct);
}
