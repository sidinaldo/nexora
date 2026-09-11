namespace Nexora.Core.Servicos;

public record EtiquetaDto(long Id, string Nome, string Cor);

public record NovaEtiqueta(string Nome, string? Cor);

public record EditarEtiqueta(string Nome, string? Cor);

/// <summary>O vocabulário de etiquetas da empresa.
///
/// ===================== QUEM LÊ E QUEM ESCREVE SÃO DIFERENTES =====================
/// `ListarAsync` é de QUALQUER papel; o resto é só do dono, e a separação é o ponto:
///
///   • criar etiqueta é CONFIGURAÇÃO — define o vocabulário, e vocabulário que qualquer um
///     inventa vira "Revendedor", "revenda" e "Revendedores" na mesma semana;
///   • aplicar etiqueta é TRABALHO DO DIA — o vendedor está atendendo e marca. Para escolher,
///     ele precisa da lista.
///
/// Por isso o `[Authorize]` fica por AÇÃO no controller, e não na classe como no
/// `EtapasController` — lá tudo é do dono.
/// ================================================================================
///
/// ⚠️ SEM CONTAGEM DE USO, e é deliberado. `EtapaDto` carrega `Contatos` porque a contagem decide
/// se o dono pode apagar a etapa sem escolher destino. Aqui não existe tabela de ligação ainda, e
/// apagar é sempre livre — um número que é sempre zero só ensina a ignorá-lo. Ele entra junto com
/// a ligação, não antes.</summary>
public interface IServicoEtiquetas
{
    /// <summary>Ordenada por nome. Não há ordem manual: etiqueta não é etapa de funil, não tem
    /// sequência, e quem procura "Urgente" numa lista procura em ordem alfabética.</summary>
    Task<IReadOnlyList<EtiquetaDto>> ListarAsync(CancellationToken ct);

    Task<long> CriarAsync(NovaEtiqueta nova, CancellationToken ct);

    Task AtualizarAsync(long id, EditarEtiqueta dados, CancellationToken ct);

    /// <summary>Apaga. Diferente de `etapas_funil`, que é `ON DELETE RESTRICT` e exige destino:
    /// lá o contato ficaria sem coluna e o kanban não desenha; aqui ele só perde um rótulo.</summary>
    Task RemoverAsync(long id, CancellationToken ct);
}
