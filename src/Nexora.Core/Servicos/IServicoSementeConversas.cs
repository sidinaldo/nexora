namespace Nexora.Core.Servicos;

/// <summary>O que a semeadura de conversas fez.</summary>
public record ResumoSementeConversas(
    int Conversas, int MensagensCriadas, int MensagensApagadas, int NaoEntregues, int Expiradas);

/// <summary>DIÁLOGOS DE MENTIRA nas conversas que já existem.
///
/// ===================== POR QUE É UM SEMEADOR SEPARADO =====================
/// `IServicoSemente` APAGA e recria o tenant inteiro. Este aqui não cria contato, não cria
/// conversa e não toca em lembrete: ele reescreve a THREAD de conversas que já estão lá.
///
/// A distinção importa porque a semeadura original distribuiu as conversas com cuidado pelas
/// faixas do semáforo, pelo funil e pelo dashboard. Recriar tudo para ter diálogo melhor jogaria
/// fora essa distribuição; reescrever só as mensagens a preserva.
/// ==========================================================================
///
/// ===================== O QUE ELE PRESERVA, E POR QUÊ =====================
/// `ultima_mensagem_em`, `aguardando_desde` e o status da conversa ficam INTOCADOS. São eles que
/// definem a cor do semáforo, o que entra no Meu Dia e o número de "aguardando resposta" do
/// dashboard — mexer neles trocaria o cenário inteiro por outro.
///
/// O roteiro é escolhido para TERMINAR na direção que a conversa já tem: quem estava esperando
/// resposta continua esperando, quem não estava continua sem esperar.
/// =========================================================================
///
/// ⚠️ Exposto SÓ em Development (ver DevController), e SÓ para o tenant logado.</summary>
public interface IServicoSementeConversas
{
    /// <summary>Reescreve a thread das `quantas` conversas mais recentes do tenant logado.
    ///
    /// IDEMPOTENTE: apaga as mensagens das conversas escolhidas antes de escrever. Rodar duas
    /// vezes dá o mesmo resultado — sem isso, a segunda execução empilharia dois diálogos na
    /// mesma thread e o histórico deixaria de fazer sentido.</summary>
    Task<ResumoSementeConversas> SemearAsync(int quantas, CancellationToken ct);
}
