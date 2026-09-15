namespace Nexora.Core.Servicos;

/// <summary>As abas da caixa de entrada. `Aguardando` e a default: e a que responde a promessa
/// do produto ("nenhum cliente sem resposta").</summary>
public enum FiltroConversa
{
    Aguardando,
    Minhas,
    NaoAtribuidas,
    Todas,
    Resolvidas
}

/// <summary>Uma linha da lista da caixa.
///
/// `AguardandoDesde` vai como TIMESTAMP, nunca como cor. A cor do semaforo envelhece sozinha
/// entre requisicoes — se o servidor mandasse "amarelo", a lista ficaria amarela para sempre
/// ate o proximo fetch. Quem calcula e o cliente.</summary>
public record ConversaResumo(
    long Id,
    long ContatoId,
    string ContatoNome,
    string Telefone,
    string? UltimaMensagemPrevia,
    string? UltimaMensagemDirecao,
    DateTime UltimaMensagemEm,
    DateTime? AguardandoDesde,
    int NaoLidas,
    string Status,
    long? ResponsavelId,
    string? ResponsavelNome,
    /// <summary>⚠️ NULOS desde o E6: contato sem negociação não está em funil nenhum, e esse é
    /// o estado normal de quem acabou de chegar. Era `long`/`string` com fallback para 0 e "" —
    /// o zero teria virado um id de etapa inventado na tela.</summary>
    long? EtapaId,
    string? EtapaNome,
    /// <summary>⚠️ NOMEADO PELA PERGUNTA QUE RESPONDE: a tela mostra o botão "Abrir negociação"?
    ///
    /// Nasceu hoje como `SemNegocioAberto` e o nome estava meio certo — ele descrevia UMA das
    /// condições em vez do que decide, e por isso não havia onde pôr a segunda sem mentir.
    ///
    /// Quem NÃO pode:
    ///   · quem já tem negócio em aberto — renderia 409 no clique;
    ///   · quem foi ANONIMIZADO — `RecusarSeAnonimizado` lança, e a faixa ficava oferecendo um
    ///     botão que sempre erra. Antes do E6 isso não aparecia porque a condição era
    ///     `ContatoGanhou`; ao abrir para todos os sem-negócio, os anonimizados entraram junto.
    ///
    /// Quem pode, e os três casos importam: o lead que acabou de chegar e nunca teve negócio (o
    /// comum desde o E6), o cliente recorrente, e aquele cuja negociação foi PERDIDA — este
    /// último a caixa nunca ofereceu reabrir, e ninguém notou porque o botão estava amarrado ao
    /// carimbo de venda.</summary>
    bool PodeAbrirNegociacao,
    /// <summary>Os funis onde este contato JÁ tem negociação aberta — os que o seletor da faixa
    /// não deve oferecer.
    ///
    /// ⚠️ É O DETALHE DE `PodeAbrirNegociacao`, e os dois saem da MESMA leitura na mesma
    /// projeção: o booleano responde "mostra o botão?" e a lista responde "quais opções?". Não
    /// podem divergir porque não há duas fontes.
    ///
    /// Quase sempre vazia ou com um item — o teto é 5 funis por empresa.</summary>
    IReadOnlyList<long> FunisComNegocioAberto,
    /// <summary>⚠️ O PAR DO DE CIMA, e ele faltava: a tela mostra "Registrar venda"?
    ///
    /// Era decidido no cliente por `!ContatoGanhou` — "nunca ganhou" — e errava nas duas pontas:
    ///
    ///   · MOSTRAVA para quem não tem negócio nenhum. Desde o E6 esse é o lead que acabou de
    ///     chegar, e o clique levava "Este contato não tem negócio em aberto";
    ///   · ESCONDIA do cliente recorrente que tem um negócio ABERTO. Quem comprou em março e está
    ///     negociando de novo em agosto simplesmente não tinha o botão — e esse é o caso que mais
    ///     importa, porque é venda pronta para fechar.
    ///
    /// A segunda metade errava desde o E4c/2, quando as duas linhas passaram a coexistir.
    ///
    /// O anonimizado entra aqui pelo mesmo motivo do par: `MarcarGanhoAsync` recusa, e um botão
    /// que sempre erra é pior que não oferecer.
    ///
    /// ⚠️ NÃO É `!PodeAbrirNegociacao`. Os dois são falsos ao mesmo tempo para o anonimizado, e
    /// derivar um do outro esconderia isso.</summary>
    bool PodeRegistrarVenda,
    /// <summary>NEG-3 · o contato JÁ COMPROU alguma vez. Não decide mais se o botão aparece —
    /// decide o que a faixa DIZ: "cliente recorrente" em vez de "ainda não é um negócio".
    ///
    /// Sai de `contatos.ganho_em`, que a projeção já lê pelo mesmo join da etapa: nenhum custo a
    /// mais por linha. A CONTAGEM de compras não vem aqui de propósito — ela exigiria um
    /// subselect em `vendas` por linha da lista, e só é usada na conversa aberta, que a busca
    /// quando precisa.</summary>
    bool ContatoGanhou,
    /// <summary>NEG-3 · o nome da campanha detectada NESTE ciclo, ou nulo. Um join a mais numa
    /// tabela de dezenas de linhas — e é o único lugar onde o vendedor vê, ANTES de fechar, por
    /// que a pessoa está falando com ele.</summary>
    string? CanalDoCiclo,
    /// <summary>===================== PEDIDO EM ABERTO, OU JA ENTREGUE =====================
    ///
    /// Quantas vendas deste contato ainda estao `fechada` — ou seja, pedido vivo.
    ///
    /// A caixa mostrava a etiqueta da etapa crua, e para quem comprou e recebeu ela dizia
    /// "Venda" — apontando para uma coluna do funil onde o card NAO esta, porque o NEG-2 tira da
    /// coluna quem nao tem pedido em aberto. Duas telas do mesmo produto discordando sobre o
    /// mesmo contato, e a caixa era a que mentia: "Venda" le como negocio acontecendo.
    ///
    /// O MESMO predicado do kanban (`RegrasContato.ComVendaEmAberto`), para as duas telas nao
    /// divergirem de novo. Subconsulta agregada contra `ix_vendas_contato_status` — o kanban ja
    /// faz exatamente isto por card.</summary>
    int VendasEmAberto,
    /// <summary>As etiquetas coladas neste contato.
    ///
    /// ⚠️ VEM PELA NAVEGACAO (`c.Contato.Etiquetas`), e nao por `db.ContatosEtiquetas`. A projecao
    /// desta lista e uma `static readonly Expression` — uma so, para a lista e para a busca por id,
    /// de proposito — e citar um campo do construtor primario dentro dela e CS9105. E o mesmo
    /// caminho que `c.Contato.Vendas.Count(...)` ja usa duas linhas acima.
    ///
    /// O EF materializa a colecao numa segunda consulta por pagina, nao uma por linha.</summary>
    IReadOnlyList<EtiquetaDto> Etiquetas);

/// <summary>Uma mensagem da thread.</summary>
public record MensagemDto(
    long Id,
    string Direcao,
    string? Texto,
    short? Ack,
    DateTime? EnviadaEm,
    DateTime? RecebidaEm,
    DateTime? ExpiradaEm,
    string? Erro,
    string TipoMidia,
    string? MidiaNome,
    string? MidiaMime,
    /// <summary>Tamanho do anexo. A tela mostra "proposta.pdf · 340 KB" — sem isto o vendedor
    /// nao sabe se vale a pena baixar antes de clicar.</summary>
    int? MidiaBytes,
    /// <summary>Duracao do audio em segundos; NULL nas outras midias. O player a mostra ANTES de
    /// tocar — ouvir 90 segundos e uma decisao, e o vendedor precisa toma-la olhando.</summary>
    int? MidiaDuracaoSegundos,
    long? EnviadoPor,
    string? EnviadoPorNome,
    bool DeLembrete,
    /// <summary>Instante em que esta mensagem ATRASADA foi gravada; NULL no caso normal (REC-1).
    /// A thread continua em ordem cronologica pelo timestamp da mensagem — o carimbo so explica
    /// por que ela apareceu agora numa posicao ja passada.</summary>
    DateTime? RecuperadaEm);

/// <summary>O aviso de recuperacao da caixa de entrada (REC-1). NULL quando nao houve queda
/// recente — que e o normal, e por isso o campo e anulavel em vez de um objeto com zeros.
///
/// A JANELA e o intervalo em que o CLIENTE escreveu (`recebida_em`), nao o instante em que
/// gravamos: e o periodo que o vendedor precisa reconstruir na cabeca.</summary>
public record AvisoRecuperacao(int Mensagens, int Conversas, DateTime De, DateTime Ate);

public interface IServicoCaixa
{
    /// <summary>A lista, paginada por CURSOR — nao por offset.
    ///
    /// Ordena por (ultima_mensagem_em DESC, id DESC), o MESMO par do cursor. A lista se reordena
    /// em tempo real (conversa nova sobe pro topo); com offset, a pagina seguinte pula ou repete
    /// linha. Toda a paginacao acontece no SQL — o ServicoInbox do Recupera materializa todos os
    /// tickets antes de cortar a pagina, e o proprio comentario de la admite que isso cresce.</summary>
    Task<PaginaCursor<ConversaResumo>> ConversasAsync(
        FiltroConversa filtro, string? busca, long? etiquetaId, DateTime? cursorEm, long? cursorId, int tamanho,
        CancellationToken ct);

    /// <summary>UMA conversa, pelo id. `null` quando não existe OU é de outro tenant.
    ///
    /// ===================== POR QUE ISTO PRECISOU EXISTIR =====================
    /// A lista da caixa é por CURSOR, e o cliente carrega só a primeira página. O Meu Dia manda
    /// o vendedor direto para uma conversa (`/caixa?conversa=N`); se ela estiver na página 4,
    /// não havia o que selecionar e a tela abria vazia — sem erro, sem explicação.
    ///
    /// Procurar rolando até achar não serve: a lista se reordena em tempo real, e o alvo pode
    /// nunca aparecer. Buscar pelo id é a única forma de a ação clicada SEMPRE abrir.
    ///
    /// O `null` não distingue "não existe" de "é de outra empresa", e é de propósito: distinguir
    /// contaria a quem sonda que a conversa existe noutro tenant.
    /// ======================================================================== */</summary>
    Task<ConversaResumo?> ConversaAsync(long conversaId, CancellationToken ct);

    /// <summary>A thread de uma conversa, tambem por cursor: as `tamanho` mensagens mais NOVAS
    /// antes de `antesDeId` (null = as ultimas). O cliente carrega as ultimas e busca as
    /// anteriores sob demanda.</summary>
    Task<PaginaCursor<MensagemDto>> MensagensAsync(
        long conversaId, long? antesDeId, int tamanho, CancellationToken ct);

    /// <summary>Marca a conversa como lida (zera o contador). Nao mexe em aguardando_desde: ler
    /// nao e responder.</summary>
    Task MarcarLidaAsync(long conversaId, CancellationToken ct);
}
