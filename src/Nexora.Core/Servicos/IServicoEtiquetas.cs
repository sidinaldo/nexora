namespace Nexora.Core.Servicos;

public record EtiquetaDto(long Id, string Nome, string Cor);

public record NovaEtiqueta(string Nome, string? Cor);

public record EditarEtiqueta(string Nome, string? Cor);

/// <summary>Como a lista sai ordenada.
///
/// ===================== POR QUE ENUM, E NAO NOME DE COLUNA =====================
/// Nenhum controller do projeto aceitava ordenacao da query string antes deste. O padrao da casa
/// para parametro de valor fechado e enum — `FiltroContato` e o precedente —, e o model binder o
/// valida sozinho: "ordem=drop table" nao chega ao servico.
///
/// Aceitar nome de coluna cru daria ao cliente poder sobre o plano de execucao do banco sem
/// nenhum ganho: sao duas ordens, e as duas estao aqui.
/// ==============================================================================
///
/// ⚠️ FALTA `Uso` — ordenar por quantas vezes a etiqueta foi aplicada. Ela depende da tabela de
/// ligacao, que ainda nao existe, e entra junto com a contagem. Nao esta declarada aqui de
/// proposito: valor de enum que o servico nao sabe atender e 500 esperando acontecer.</summary>
public enum OrdemEtiqueta
{
    /// <summary>A a Z. O padrao, porque quem procura "Urgente" numa lista procura alfabeticamente.
    /// </summary>
    Nome,

    /// <summary>Da mais nova para a mais antiga. Serve a quem acabou de cadastrar um punhado e
    /// quer revisar o que fez.</summary>
    Recentes
}

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
    /// <summary>A lista da empresa.
    ///
    /// `busca` casa por trecho do nome, sem diferenciar maiúscula — a mesma insensibilidade do
    /// índice `uq_etiquetas_nome`, senão procurar "vip" não acharia a "VIP" que o próprio sistema
    /// impediu de duplicar.
    ///
    /// ⚠️ SEM PAGINAÇÃO, e isso é uma decisão, não um esquecimento. `MaximoEtiquetas` é 60 e o
    /// servidor recusa a 61ª — a lista inteira cabe numa resposta por construção. Paginar aqui
    /// custaria um `Pagina&lt;T&gt;` e um cursor na tela para percorrer, no pior caso, três telas de
    /// celular.</summary>
    Task<IReadOnlyList<EtiquetaDto>> ListarAsync(
        string? busca, OrdemEtiqueta ordem, CancellationToken ct);

    Task<long> CriarAsync(NovaEtiqueta nova, CancellationToken ct);

    Task AtualizarAsync(long id, EditarEtiqueta dados, CancellationToken ct);

    /// <summary>Apaga. Diferente de `etapas_funil`, que é `ON DELETE RESTRICT` e exige destino:
    /// lá o contato ficaria sem coluna e o kanban não desenha; aqui ele só perde um rótulo.</summary>
    Task RemoverAsync(long id, CancellationToken ct);
}
