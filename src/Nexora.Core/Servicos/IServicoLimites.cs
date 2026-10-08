namespace Nexora.Core.Servicos;

/// <summary>Quanto de um teto já está em uso. `Cheio` é a mesma pergunta que o serviço faz antes de
/// recusar a criação (`EmUso >= Limite`).</summary>
public record UsoDoLimite(int EmUso, int Limite, bool Cheio);

/// <summary>===================== OS TETOS SÃO DO SERVIDOR (AUD-XX) =====================
/// As telas escreviam "N de M" com M copiado num `readonly maximo = 5` e N por `.length` da lista.
/// A cópia já tinha divergido: a de Pipelines dizia 5, e o servidor aceita 4 — a tela mostrava
/// "4 de 5", abria o formulário do quinto e levava 409.
///
/// Os números saem das MESMAS constantes que os serviços usam para recusar, e as contagens do
/// banco. `LimiteEtapasDoFunil` só vem quando a tela diz de qual funil está falando.
///
/// ⚠️ OS NOMES TÊM O PREFIXO `Limite` DE PROPÓSITO: `pipelines` e `etiquetas` já são nomes de lista
/// em outras respostas, e as suítes de tela respondem um corpo só para todas.
/// ===============================================================================</summary>
public record TetosDaEmpresa(
    UsoDoLimite LimitePipelines,
    UsoDoLimite LimiteEtiquetas,
    UsoDoLimite? LimiteEtapasDoFunil,
    int EtiquetasPorNegocio);

public interface IServicoLimites
{
    Task<TetosDaEmpresa> ObterAsync(long? pipelineId, CancellationToken ct);
}
