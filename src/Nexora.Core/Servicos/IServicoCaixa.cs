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
    string EtapaNome);

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
    long? EnviadoPor,
    string? EnviadoPorNome,
    bool DeLembrete);

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

    /// <summary>A thread de uma conversa, tambem por cursor: as `tamanho` mensagens mais NOVAS
    /// antes de `antesDeId` (null = as ultimas). O cliente carrega as ultimas e busca as
    /// anteriores sob demanda.</summary>
    Task<PaginaCursor<MensagemDto>> MensagensAsync(
        long conversaId, long? antesDeId, int tamanho, CancellationToken ct);

    /// <summary>Marca a conversa como lida (zera o contador). Nao mexe em aguardando_desde: ler
    /// nao e responder.</summary>
    Task MarcarLidaAsync(long conversaId, CancellationToken ct);
}
