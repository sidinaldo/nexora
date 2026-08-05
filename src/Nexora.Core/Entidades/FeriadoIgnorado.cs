namespace Nexora.Core.Entidades;

/// <summary>Uma empresa que ATENDE num feriado global.
///
/// ===================== POR QUE ISTO É UMA TABELA =====================
/// Feriado nacional é linha GLOBAL (`feriados.empresa_id IS NULL`), compartilhada por todos os
/// tenants. Não dá para apagá-lo — apagaria o Natal de todo mundo — nem para marcar `ativo=false`
/// na própria linha, pelo mesmo motivo.
///
/// A dispensa é, por natureza, POR EMPRESA: o comércio de rua fecha no Corpus Christi e o
/// e-commerce não. Uma linha por (empresa, feriado) é o modelo honesto disso, e é a mesma forma
/// que `feriados` já usa para o caminho inverso (feriado manual, que só o tenant enxerga).
/// =====================================================================
///
/// A chave é composta (empresa_id, feriado_id): a mesma empresa não ignora o mesmo feriado duas
/// vezes, e reativar é apagar a linha.</summary>
public class FeriadoIgnorado
{
    public long EmpresaId { get; set; }
    public long FeriadoId { get; set; }
    public DateTime CriadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Feriado Feriado { get; set; } = null!;
}
