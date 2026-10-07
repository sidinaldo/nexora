using Nexora.Core.Entidades;

namespace Nexora.Core.Servicos;

/// <summary>Um evento da trilha, do jeito que a tela consome.
///
/// `Alteracoes` viaja como o JSON CRU. A traducao para portugues ("moveu de Negociacao para
/// Proposta") mora no CLIENTE: e texto de interface, muda com a redacao do produto, e faze-la no
/// servidor obrigaria a um deploy de backend para corrigir uma frase.</summary>
public record EventoTrilha(
    long Id,
    string Entidade,
    long EntidadeId,
    string Acao,
    string Alteracoes,
    long? UsuarioId,
    string? UsuarioNome,
    string Ator,
    DateTime Quando);

public interface IServicoTrilha
{
    /// <summary>A linha do tempo de UM registro, PAGINADA no banco com o total (AUD-XX). So DONO e
    /// GESTOR — ver a implementacao.
    ///
    /// Era uma lista cortada em 50 (ate 200 com "Ver historico completo"), paginada na tela: um
    /// cliente de anos tinha historia que nenhum botao alcancava, e a tela dizia "50 eventos".</summary>
    Task<PaginaComTotal<EventoTrilha>> DoRegistroAsync(
        EntidadeAuditada entidade, long id, int pagina, int tamanho, CancellationToken ct);
}
