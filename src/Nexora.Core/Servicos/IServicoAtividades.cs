namespace Nexora.Core.Servicos;

/// <summary>Um evento do feed.
///
/// `Chave` é `tipo:id` e existe porque o feed é uma UNIÃO de quatro tabelas: o `id` sozinho
/// colide entre elas (mensagem 7 e contato 7 existem ao mesmo tempo), e sem um desempate estável
/// a paginação por cursor pularia ou repetiria linha no empate de timestamp.</summary>
public record Atividade(
    string Tipo,
    string Chave,
    DateTime Quando,
    long ContatoId,
    string ContatoNome,
    string Titulo,
    string? Detalhe,
    decimal? Valor,
    long? ResponsavelId,
    string? ResponsavelNome);

public record PaginaAtividades(IReadOnlyList<Atividade> Itens, bool TemMais);

public interface IServicoAtividades
{
    /// <summary>Últimos eventos do tenant, mais novo primeiro.
    ///
    /// `responsavelId` é sugestão, não ordem: para o papel Vendedor o serviço IGNORA o que vier e
    /// impõe o próprio usuário. Filtro de visibilidade que confia no parâmetro do cliente não é
    /// filtro.</summary>
    Task<PaginaAtividades> ListarAsync(
        DateTime? cursorEm, string? cursorChave, long? responsavelId, int tamanho,
        CancellationToken ct);
}
