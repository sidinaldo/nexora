namespace Nexora.Core.Entidades;

/// <summary>Abrangência do feriado. Nacional e Estadual são GLOBAIS (empresa_id NULL, visíveis a
/// todos); Manual é do próprio tenant.</summary>
public enum AbrangenciaFeriado
{
    Nacional,
    Estadual,
    Manual
}

/// <summary>Um dia em que a empresa não atende — e portanto não dispara follow-up e não conta
/// para o semáforo.
///
/// A tabela é a ÚNICA de tenant com `empresa_id` anulável: NULL = global (nacional/estadual,
/// semeado pelo job anual), preenchido = feriado local da empresa. O query filter admite os dois
/// casos, e é por isso que ele é diferente de todos os outros do sistema.</summary>
public class Feriado
{
    public long Id { get; set; }

    /// <summary>NULL = global. É a exceção deliberada ao "toda entidade tem empresa_id NOT NULL".</summary>
    public long? EmpresaId { get; set; }

    public DateOnly Data { get; set; }
    public string Nome { get; set; } = null!;
    public AbrangenciaFeriado Abrangencia { get; set; }

    /// <summary>UF do feriado estadual. Estrutura pronta, mas hoje nenhum estadual é semeado —
    /// `empresas` não tem coluna `uf`, então não há como saber a qual estado a empresa pertence.</summary>
    public string? Uf { get; set; }

    public DateTime CriadoEm { get; set; }

    public Empresa? Empresa { get; set; }
}
