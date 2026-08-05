using Nexora.Core.Entidades;

namespace Nexora.Core.Servicos;

/// <summary>Um feriado como a empresa o vê.
///
/// `Ignorado` só faz sentido em feriado GLOBAL: é a empresa dizendo "aqui a gente trabalha nesse
/// dia". Feriado manual não se ignora — apaga-se.</summary>
public record FeriadoDto(
    long Id, DateOnly Data, string Nome, string Abrangencia, bool EhManual, bool Ignorado);

public record NovoFeriado(DateOnly Data, string Nome);

public interface IServicoFeriados
{
    /// <summary>Semeia os feriados globais do ano atual e do próximo. IDEMPOTENTE — roda no boot
    /// e de novo na rodada diária, e cobre a virada de ano sem ninguém lembrar.
    ///
    /// Roda como JOB, sem tenant no contexto.</summary>
    Task GarantirAtualEProximoAsync(CancellationToken ct);

    /// <summary>Os feriados que valem para o tenant logado, de hoje em diante — inclusive os
    /// dispensados, marcados com `Ignorado = true`, para a tela poder mostrá-los apagados em vez
    /// de simplesmente sumir com eles.</summary>
    Task<IReadOnlyList<FeriadoDto>> ProximosAsync(CancellationToken ct);

    /// <summary>Feriado local da empresa (ponto facultativo, aniversário da cidade).</summary>
    Task<long> CriarManualAsync(NovoFeriado novo, CancellationToken ct);

    /// <summary>Só apaga MANUAL do próprio tenant. Global é de todo mundo — para não observá-lo,
    /// use <see cref="IgnorarAsync"/>.</summary>
    Task RemoverManualAsync(long id, CancellationToken ct);

    /// <summary>"Nesta empresa a gente atende nesse dia." Vale só para feriado GLOBAL, e só para
    /// o tenant que pediu.</summary>
    Task IgnorarAsync(long feriadoId, CancellationToken ct);

    /// <summary>Desfaz o ignorar: o feriado global volta a valer para a empresa.</summary>
    Task ReativarAsync(long feriadoId, CancellationToken ct);
}
