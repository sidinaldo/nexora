namespace Nexora.Core.Servicos;

public record LembreteDto(
    long Id, long ContatoId, string ContatoNome, long? ConversaId,
    string Origem, string Status, DateOnly DataAlvo, TimeOnly? HoraAlvo,
    string Titulo, string? Observacao, bool EnviaMensagem,
    long? ResponsavelId, string? ResponsavelNome, DateTime? ConcluidoEm);

public record NovoLembrete(
    long ContatoId, DateOnly DataAlvo, TimeOnly? HoraAlvo,
    string Titulo, string? Observacao,
    bool EnviaMensagem = false, string? TextoMensagem = null);

public interface IServicoLembretes
{
    Task<IReadOnlyList<LembreteDto>> DoContatoAsync(long contatoId, CancellationToken ct);

    /// <summary>Lembrete MANUAL: o vendedor marca na mão. NÃO entra no teto diário — aquele
    /// índice só cobre os automáticos, porque é a defesa contra o robô cansar o cliente. Uma
    /// pessoa marcando três tarefas para o mesmo contato sabe o que está fazendo.</summary>
    Task<long> CriarAsync(NovoLembrete novo, CancellationToken ct);

    Task ConcluirAsync(long id, CancellationToken ct);
    Task CancelarAsync(long id, CancellationToken ct);
}
