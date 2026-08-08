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
    long EtapaId,
    string EtapaNome,
    /// <summary>NEG-3 · o contato está com venda fechada carimbada, ou seja: JÁ COMPROU e não há
    /// negociação em aberto. É o que autoriza a caixa a oferecer "abrir nova negociação" — e
    /// oferecê-la para quem já está em aberto renderia 409 no clique.
    ///
    /// Sai de `contatos.ganho_em`, que a projeção já lê pelo mesmo join da etapa: nenhum custo a
    /// mais por linha. A CONTAGEM de compras não vem aqui de propósito — ela exigiria um
    /// subselect em `vendas` por linha da lista, e só é usada na conversa aberta, que a busca
    /// quando precisa.</summary>
    bool ContatoGanhou);

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
        FiltroConversa filtro, string? busca, DateTime? cursorEm, long? cursorId, int tamanho,
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
