using Nexora.Core.Entidades;

namespace Nexora.Core.FollowUp;

/// <summary>Uma conversa parada, candidata a follow-up. Projeção enxuta de propósito: a rodada
/// varre todas as empresas e não precisa das entidades inteiras.</summary>
public record ConversaInativa(
    long ConversaId,
    long ContatoId,
    string ContatoNome,
    string Telefone,
    long ConexaoId,
    string InstanceName,
    long? ResponsavelId,
    DateTime UltimaMensagemEm);

/// <summary>Um lembrete pronto para disparar.</summary>
public record LembreteParaDisparar(
    long LembreteId,
    long ContatoId,
    long? ConversaId,
    string Telefone,
    string Texto,
    long ConexaoId,
    string InstanceName);

/// <summary>Dados de que a rodada precisa. Interface no Core, EF na Infra.
///
/// TODAS as implementações rodam como JOB, sem tenant no contexto: usam IgnoreQueryFilters mais
/// filtro explícito por empresaId. Sem isso o filtro global compara com 0 e a rodada não vê
/// empresa nenhuma — em silêncio.</summary>
public interface IDadosFollowUp
{
    Task<IReadOnlyList<Empresa>> EmpresasAtivasAsync(CancellationToken ct);

    /// <summary>A conexão da empresa (id + instância). NULL se ela ainda não pareou um número.</summary>
    Task<(long Id, string InstanceName)?> ConexaoAsync(long empresaId, CancellationToken ct);

    /// <summary>Os feriados que valem para a empresa no intervalo (nacionais + os manuais dela).</summary>
    Task<HashSet<DateOnly>> FeriadosAsync(long empresaId, DateOnly de, DateOnly ate, CancellationToken ct);

    /// <summary>=========== A REGRA DE ELEGIBILIDADE ===========
    /// Conversas paradas que merecem follow-up. Ver MotorFollowUp para o porquê de cada
    /// condição. Filtra TUDO no SQL.</summary>
    Task<IReadOnlyList<ConversaInativa>> ConversasInativasAsync(
        long empresaId, DateTime limite, CancellationToken ct);

    /// <summary>Cria o lembrete automático. Devolve NULL quando o teto diário barrou — que é
    /// resultado ESPERADO, não erro.</summary>
    Task<long?> CriarLembreteAutomaticoAsync(
        long empresaId, long contatoId, long conversaId, long? responsavelId,
        DateOnly dataAlvo, string titulo, string texto, CancellationToken ct);

    /// <summary>Lembretes pendentes que já venceram (data_alvo &lt;= hoje) e enviam mensagem.</summary>
    Task<IReadOnlyList<LembreteParaDisparar>> LembretesADispararAsync(
        long empresaId, DateOnly hoje, CancellationToken ct);

    /// <summary>Marca o lembrete como concluído depois do envio.</summary>
    Task ConcluirLembreteAsync(long lembreteId, CancellationToken ct);

    /// <summary>O telefone do contato de uma mensagem pendente, para a drenagem poder reenviar.</summary>
    Task<string?> TelefoneDoContatoAsync(long contatoId, CancellationToken ct);
}
