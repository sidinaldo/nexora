namespace Nexora.Core.Servicos;

/// <summary>A etiqueta como ela aparece num CHIP — no card do funil, na linha da caixa, no
/// seletor. Tres campos e nada mais, de proposito: esta projecao roda por contato em toda lista
/// do produto, e cada campo a mais e multiplicado pelo numero de linhas na tela.</summary>
public record EtiquetaDto(long Id, string Nome, string Cor);

/// <summary>A etiqueta na TELA DE GESTAO, com quantos contatos a usam.
///
/// ===================== POR QUE UM DTO SEPARADO =====================
/// A contagem e uma subconsulta por linha. Colocar ela no `EtiquetaDto` obrigaria todo card do
/// quadro e toda linha da caixa a pagar por um numero que nenhum dos dois mostra.
/// ===================================================================
///
/// ⚠️ A CONTAGEM E CRUA: conta as marcacoes que existem, incluindo as de contato perdido ou
/// anonimizado. Nao e descuido — e o numero que responde "de quantos contatos esta etiqueta vai
/// sair se eu apagar", que e a pergunta que o dono faz antes de apagar. Mesma decisao, pelo mesmo
/// motivo, de `ServicoEtapas.ListarAsync`, que conta contato sem aplicar `RegrasNegociacao.NoQuadro`
/// porque o numero dele responde "o que trava a remocao".</summary>
public record EtiquetaNaLista(long Id, string Nome, string Cor, int Contatos);

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
    Recentes,

    /// <summary>Da mais usada para a menos usada.
    ///
    /// Este valor estava RESERVADO em comentario desde a issue #4, com a observacao de que
    /// dependia da tabela de ligacao. Ela existe agora.
    ///
    /// E a ordem que responde "quais etiquetas a equipe de fato usa" — que e a pergunta de quem
    /// vai limpar o vocabulario, e a unica em que as nao usadas precisam aparecer no fim em vez
    /// de espalhadas pelo alfabeto.</summary>
    Uso
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
    Task<IReadOnlyList<EtiquetaNaLista>> ListarAsync(
        string? busca, OrdemEtiqueta ordem, CancellationToken ct);

    Task<long> CriarAsync(NovaEtiqueta nova, CancellationToken ct);

    Task AtualizarAsync(long id, EditarEtiqueta dados, CancellationToken ct);

    /// <summary>Apaga. Diferente de `etapas_funil`, que é `ON DELETE RESTRICT` e exige destino:
    /// lá o contato ficaria sem coluna e o kanban não desenha; aqui ele só perde um rótulo.</summary>
    Task RemoverAsync(long id, CancellationToken ct);

    // ==================================================================== aplicar
    /// <summary>As etiquetas coladas num contato, em ordem de nome.</summary>
    Task<IReadOnlyList<EtiquetaDto>> DoContatoAsync(long contatoId, CancellationToken ct);

    /// <summary>Substitui o conjunto INTEIRO de etiquetas do contato.
    ///
    /// ===================== POR QUE SUBSTITUIR, E NAO ADICIONAR/REMOVER =====================
    /// Mandar a lista toda torna a operacao IDEMPOTENTE: repetir por duplo clique ou por retry de
    /// rede da o mesmo resultado. "Adiciona uma" repetida nao faz mal, mas "remove uma" faz — e as
    /// duas chegariam pelo mesmo caminho instavel.
    ///
    /// E o mesmo argumento que `IServicoEtapas.ReordenarAsync` ja usa para a ordem das etapas, com
    /// a mesma consequencia pratica: a tela sabe o estado final e manda o estado final.
    /// ====================================================================================
    ///
    /// ⚠️ De QUALQUER papel. Criar etiqueta e configuracao e so o dono faz; APLICAR e trabalho do
    /// dia, e quem esta atendendo e quem marca. E a mesma assimetria que o `ListarAsync` ja tem.</summary>
    Task AplicarAsync(long contatoId, IReadOnlyList<long> etiquetaIds, CancellationToken ct);

    /// <summary>As etiquetas coladas numa NEGOCIACAO, em ordem de nome.</summary>
    Task<IReadOnlyList<EtiquetaDto>> DaNegociacaoAsync(long negociacaoId, CancellationToken ct);

    /// <summary>===================== A ETIQUETA DO NEGOCIO =====================
    /// Substitui o conjunto inteiro, pelas MESMAS razoes do par acima — idempotencia e qualquer
    /// papel. O que muda e onde a marca gruda.
    ///
    /// Relatado assim: "incluí o contato Ysia em Vendas e Pós-venda e ela ficou com a mesma
    /// etiqueta em pipeline diferente". O card mostrava as etiquetas da PESSOA, entao os dois
    /// cards dela saiam identicos e nao havia como dizer "este negocio esta urgente" sem dizer o
    /// mesmo do outro.
    ///
    /// ⚠️ NAO SUBSTITUI `AplicarAsync`. Quem esta so na caixa de entrada nao tem negocio nenhum
    /// (E6) e continua marcavel pela pessoa; "Revendedor" vale em todos os negocios dela e nao
    /// deve ser remarcado em cada um.
    /// ==========================================================</summary>
    Task AplicarNaNegociacaoAsync(
        long negociacaoId, IReadOnlyList<long> etiquetaIds, CancellationToken ct);

    /// <summary>Quantas MARCACOES perdem esta etiqueta se ela for apagada — de contato E de
    /// negocio somadas.
    ///
    /// ⚠️ SOMA AS DUAS DESDE QUE `negociacoes_etiquetas` NASCEU. Contar so contato passaria a
    /// mentir: o dono apagaria uma etiqueta vendo "3" e perderia trinta marcacoes de negocio.
    ///
    /// ⚠️ EXISTE MESMO COM A CONTAGEM JA NA LISTA, e a razao e o tempo: a lista foi carregada
    /// quando a tela abriu, e entre aquele instante e o clique em "Apagar" outra pessoa pode ter
    /// marcado mais vinte contatos. O numero que aparece na confirmacao tem de ser o de AGORA —
    /// e a confirmacao e o ultimo lugar onde o dono ainda pode desistir.</summary>
    Task<int> ImpactoAsync(long id, CancellationToken ct);
}
