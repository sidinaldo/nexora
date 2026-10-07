namespace Nexora.Core.Tempo;

/// <summary>===================== "HOJE" É O DIA DA EMPRESA, NÃO O DO SERVIDOR (AUD-1) =====================
///
/// O servidor roda em UTC. Das 21h à meia-noite de Brasília, `DateTime.UtcNow` já está no dia
/// seguinte — e os relatórios e o gráfico do dashboard, que usavam isso como data final padrão,
/// passavam a noite inteira pedindo "até amanhã". Um ponto vazio no fim do gráfico, e o período
/// de comparação deslocado de um dia.
///
/// Quem precisa da data de hoje numa requisição pede aqui. O fuso é o da empresa logada
/// (`FusoDeNegocio`), o mesmo que os serviços já usam para cortar os períodos.
/// ==========================================================================================</summary>
public interface IHojeDaEmpresa
{
    Task<DateOnly> HojeAsync(CancellationToken ct);
}
