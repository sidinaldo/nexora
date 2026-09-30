using Microsoft.AspNetCore.HttpOverrides;

namespace Nexora.Api.Seguranca;

/// <summary>DE ONDE SAI O IP DO CLIENTE quando há um proxy na frente.
///
/// ===================== POR QUE ISTO NÃO MORA NO `Program.cs` =====================
/// Porque é regra, e regra no `Program.cs` não tem teste. O que está em jogo aqui não é
/// encanamento: é a diferença entre cada cliente ter o próprio balde de rate limit e todos
/// dividirem um, e entre a Meta reconhecer quem clicou no anúncio e não reconhecer ninguém.
///
/// Com a regra numa função, o teste exercita ESTA configuração contra o middleware DE VERDADE do
/// ASP.NET — e não uma releitura dela.
/// ==============================================================================</summary>
public static class ConfiguracaoDeProxy
{
    /// <summary>O cabeçalho que o Cloudflare escreve com o IP real, sempre, descartando o que o
    /// cliente tiver mandado. É o valor de `RateLimit:CabecalhoIpReal` atrás de Tunnel ou proxy
    /// laranja.</summary>
    public const string CloudflareIpReal = "CF-Connecting-IP";

    /// <summary>Configura o `UseForwardedHeaders` a partir das opções.
    ///
    /// ⚠️ LIMPAR `KnownProxies`/`KnownNetworks` SÓ COM `ConfiarProxyReverso`. Limpar as duas listas
    /// é dizer "aceito o cabeçalho de qualquer origem" — o que só é seguro quando a aplicação não
    /// publica porta e o proxy é o único caminho de entrada. Ligado por engano em dev, qualquer um
    /// escolhe o próprio IP e escapa de todos os limites.
    ///
    /// ⚠️ E `ForwardedForHeaderName` SÓ QUANDO PEDIDO. Vazio mantém o `X-Forwarded-For`, que é o
    /// padrão e o que um proxy comum manda — trocar isso por engano faria o IP sumir em vez de
    /// melhorar.</summary>
    public static void Aplicar(ForwardedHeadersOptions opcoes, OpcoesRateLimit rate)
    {
        opcoes.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        if (!string.IsNullOrWhiteSpace(rate.CabecalhoIpReal))
            opcoes.ForwardedForHeaderName = rate.CabecalhoIpReal.Trim();

        if (rate.ConfiarProxyReverso)
        {
            opcoes.KnownNetworks.Clear();
            opcoes.KnownProxies.Clear();
        }
    }
}
