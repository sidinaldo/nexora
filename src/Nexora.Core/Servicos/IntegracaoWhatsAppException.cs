namespace Nexora.Core.Servicos;

/// <summary>Falha ao falar com o gateway de WhatsApp (Evolution API): fora do ar, ou
/// respondeu erro. O FiltroRegraDeNegocio traduz para 502 Bad Gateway — o upstream falhou,
/// nao nos. Sem isso a excecao vazaria como 500 com stack trace.
///
/// Declarada ja no bloco 1 porque quem traduz excecao para HTTP e o filtro global, e ele
/// precisa conhecer o tipo. O cliente que a lanca chega no bloco 3 (Evolution API).</summary>
public class IntegracaoWhatsAppException(string mensagem, Exception? interna = null)
    : Exception(mensagem, interna);
