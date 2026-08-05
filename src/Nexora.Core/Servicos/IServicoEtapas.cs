namespace Nexora.Core.Servicos;

/// <summary>Uma etapa do funil, com quantos contatos estão nela AGORA.
///
/// A contagem vem junto porque é ela que decide se o dono pode apagar a etapa sem escolher
/// destino — mostrar o botão "apagar" e só então descobrir que há 38 contatos ali seria empurrar
/// o erro para depois do clique.</summary>
public record EtapaDto(
    long Id,
    string Nome,
    short Ordem,
    string Cor,
    bool EGanho,
    int Contatos);

public record NovaEtapa(string Nome, string? Cor);

public record EditarEtapa(string Nome, string? Cor);

/// <summary>Configuração do funil. Só o DONO.
///
/// ===================== POR QUE ISTO NÃO É CRUD =====================
/// `etapas_funil` tem três invariantes que o banco garante e uma que ele NÃO garante:
///
///   • `uq_etapas_ordem` — (empresa_id, ordem) único. É um ÍNDICE, não uma constraint, logo
///     NÃO é adiável: uma troca ingênua de posições viola no meio do UPDATE.
///   • `uq_etapas_ganho` — no máximo uma etapa de ganho por empresa.
///   • `fk_contatos_etapa ON DELETE RESTRICT` — apagar etapa com contato estoura no banco.
///   • O que o banco NÃO garante: que sobre ao menos uma etapa. Empresa sem etapa é empresa
///     quebrada — `contatos.etapa_id` é NOT NULL e o kanban não desenha.
/// ===================================================================</summary>
public interface IServicoEtapas
{
    Task<IReadOnlyList<EtapaDto>> ListarAsync(CancellationToken ct);

    /// <summary>Entra no fim do funil. Nunca como etapa de ganho: a de ganho é única, já existe,
    /// e trocar qual é ela é operação própria (`DefinirGanhoAsync`).</summary>
    Task<long> CriarAsync(NovaEtapa nova, CancellationToken ct);

    /// <summary>Nome e cor. Renomear a etapa de GANHO é permitido — a flag existe justamente
    /// para a empresa poder chamar "Venda" de "Fechado" sem quebrar a conversão.</summary>
    Task AtualizarAsync(long id, EditarEtapa dados, CancellationToken ct);

    /// <summary>Recebe a lista COMPLETA de ids na ordem desejada. Lista parcial é recusada:
    /// aplicar uma permutação parcial deixaria buracos e colisões de `ordem`.</summary>
    Task ReordenarAsync(IReadOnlyList<long> idsNaOrdem, CancellationToken ct);

    /// <summary>Move a marca de ganho para outra etapa. Operação separada de `AtualizarAsync`
    /// porque muda o significado de todo o histórico de conversão, e merece um clique próprio.</summary>
    Task DefinirGanhoAsync(long id, CancellationToken ct);

    /// <summary>Apaga. Se a etapa tiver contatos, `destinoId` é obrigatório e todos vão para lá.
    ///
    /// Não existe apagar em cascata: contato é o ativo do cliente, e apagar uma coluna do kanban
    /// nunca pode significar perder as pessoas que estavam nela.</summary>
    Task RemoverAsync(long id, long? destinoId, CancellationToken ct);
}
