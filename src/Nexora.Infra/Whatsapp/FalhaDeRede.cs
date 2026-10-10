namespace Nexora.Infra.Whatsapp;

/// <summary>===================== O PEDIDO DE ENVIO PODE TER CHEGADO? (BUG-XX) =====================
///
/// Duas falhas de rede parecem iguais e sao opostas:
///   · NAO CONECTOU (servidor fora, nome nao resolve, TLS recusado) — o pedido nem saiu daqui, e
///     tentar de novo e seguro;
///   · CONECTOU E A RESPOSTA NAO VOLTOU (timeout, conexao caiu no meio) — o WhatsApp pode ter
///     mandado a mensagem. Tentar de novo e o jeito de o cliente receber duas vezes.
///
/// Na duvida, "pode ter chegado": so o que e CERTO que nao saiu volta para a fila.
/// ==========================================================================================</summary>
public static class FalhaDeRede
{
    public static bool PodeTerChegado(Exception ex)
    {
        if (ex is HttpRequestException http)
        {
            if (http.HttpRequestError == HttpRequestError.ConnectionError) return false;
            if (http.HttpRequestError == HttpRequestError.NameResolutionError) return false;
            if (http.HttpRequestError == HttpRequestError.SecureConnectionError) return false;
        }

        return true;
    }
}
