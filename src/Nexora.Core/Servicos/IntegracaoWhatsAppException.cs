namespace Nexora.Core.Servicos;

/// <summary>Falha ao falar com o gateway de WhatsApp (Evolution API): fora do ar, ou
/// respondeu erro. O FiltroRegraDeNegocio traduz para 502 Bad Gateway — o upstream falhou,
/// nao nos. Sem isso a excecao vazaria como 500 com stack trace.
///
/// Declarada ja no bloco 1 porque quem traduz excecao para HTTP e o filtro global, e ele
/// precisa conhecer o tipo. O cliente que a lanca chega no bloco 3 (Evolution API).</summary>
/// <param name="incerto">===== NAO DA PARA SABER SE SAIU (BUG-XX) =====
/// O pedido de envio pode ter chegado ao WhatsApp: a resposta nao voltou a tempo, ou a conexao
/// caiu no meio. O envio NAO e tentado de novo sozinho — reenviar o que talvez ja tenha chegado e
/// exatamente a mensagem duplicada que o protocolo existe para impedir. Falso quando e certo que
/// nao saiu: o WhatsApp respondeu erro, ou nem deu para conectar.</param>
public class IntegracaoWhatsAppException(string mensagem, Exception? interna = null, bool incerto = false)
    : Exception(mensagem, interna)
{
    public bool Incerto { get; } = incerto;
}
