using System.Net;
using System.Net.Sockets;

namespace Nexora.Core.Webhooks;

/// <summary>Resolve um nome para os endereços dele. Interface no Core para o validador ser
/// testável sem DNS de verdade — e é o que permite provar que a checagem da ENTREGA acontece,
/// simulando um domínio que passou no cadastro e depois passou a apontar para dentro.</summary>
public interface IResolvedorDns
{
    /// <summary>Nunca lança: nome que não resolve devolve vazio, e vazio é recusa.</summary>
    Task<IPAddress[]> ResolverAsync(string host, CancellationToken ct);
}

public record ResultadoUrl(bool Ok, string? Motivo)
{
    public static readonly ResultadoUrl Valida = new(true, null);
    public static ResultadoUrl Recusada(string motivo) => new(false, motivo);
}

/// <summary>O GUARDA DE SSRF.
///
/// ===================== O QUE ESTÁ EM JOGO =====================
/// A URL é escolhida pelo CLIENTE e chamada pelo NOSSO servidor. Sem guarda, ele aponta para
/// `http://169.254.169.254/latest/meta-data/` e o Nexora busca as credenciais de nuvem da própria
/// infraestrutura e as entrega — de dentro da rede, autenticado pelo simples fato de a requisição
/// sair de lá. O mesmo vale para `http://localhost:5432`, para o painel de um contêiner vizinho e
/// para qualquer coisa em `10.`.
///
/// Isso é SSRF, e não é hipótese: é a forma mais comum de transformar "webhook configurável" em
/// leitura da rede interna.
/// ==============================================================
///
/// ===================== VALIDAR DUAS VEZES NÃO É REDUNDÂNCIA =====================
/// No cadastro E antes de cada entrega. A razão é DNS: `webhook.cliente.com` pode resolver para um
/// IP público no dia do cadastro e para `127.0.0.1` amanhã — o atacante controla a zona. Validar
/// só na entrada é validar um valor que ele pode mudar depois, sem tocar no Nexora.
/// ================================================================================</summary>
public static class ValidadorUrlWebhook
{
    /// <summary>Só `https`. `http` sairia com o payload e a assinatura em claro, e assinatura em
    /// claro numa rede hostil é assinatura que se copia. Não há exceção para desenvolvimento: uma
    /// exceção configurável vira a configuração de produção de alguém.</summary>
    private const string EsquemaExigido = "https";

    /// <summary>Formato + esquema + host literal. NÃO resolve DNS — é a parte pura, e é o que
    /// permite recusar `http://` ou `https://10.0.0.5` sem tocar na rede.</summary>
    public static ResultadoUrl ValidarFormato(string? url)
    {
        var limpo = (url ?? "").Trim();
        if (limpo.Length == 0) return ResultadoUrl.Recusada("Informe a URL do webhook.");
        if (limpo.Length > 500) return ResultadoUrl.Recusada("A URL é longa demais.");

        if (!Uri.TryCreate(limpo, UriKind.Absolute, out var uri))
            return ResultadoUrl.Recusada($"URL inválida: \"{limpo}\".");

        if (!string.Equals(uri.Scheme, EsquemaExigido, StringComparison.OrdinalIgnoreCase))
            return ResultadoUrl.Recusada(
                "A URL precisa começar com https. Em http o conteúdo e a assinatura viajam em "
              + "claro, e qualquer um no caminho consegue ler e copiar.");

        if (uri.HostNameType is UriHostNameType.Unknown || uri.Host.Length == 0)
            return ResultadoUrl.Recusada("A URL precisa ter um endereço válido.");

        // `localhost`, `algo.localhost` e `.local`: nomes que NUNCA saem da máquina ou da rede
        // local, e que muitas vezes nem chegam ao DNS para a checagem de IP pegar.
        var host = uri.Host.ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local"))
            return ResultadoUrl.Recusada(RecusaDeRedeInterna);

        // IP literal: dá para decidir aqui, sem DNS.
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !EhPublico(ip))
            return ResultadoUrl.Recusada(RecusaDeRedeInterna);

        return ResultadoUrl.Valida;
    }

    /// <summary>Formato + DNS. TODOS os endereços do nome precisam ser públicos: um nome que
    /// resolve para dois IPs, um público e um interno, é exatamente o truque — o cliente mostra o
    /// público na demonstração e o servidor pode escolher o outro na hora do POST.</summary>
    public static async Task<ResultadoUrl> ValidarAsync(
        string? url, IResolvedorDns dns, CancellationToken ct)
    {
        var formato = ValidarFormato(url);
        if (!formato.Ok) return formato;

        var host = new Uri(url!.Trim()).Host.Trim('[', ']');

        // IP literal já foi decidido no formato — e resolver um IP pelo DNS não acrescenta nada.
        if (IPAddress.TryParse(host, out _)) return ResultadoUrl.Valida;

        var enderecos = await dns.ResolverAsync(host, ct);

        if (enderecos.Length == 0)
            return ResultadoUrl.Recusada(
                $"Não foi possível resolver \"{host}\". Confira o endereço.");

        return enderecos.All(EhPublico)
            ? ResultadoUrl.Valida
            : ResultadoUrl.Recusada(RecusaDeRedeInterna);
    }

    public const string RecusaDeRedeInterna =
        "Esta URL aponta para um endereço interno (localhost, IP privado ou faixa reservada). "
      + "O webhook precisa de um endereço público na internet.";

    /// <summary>O endereço é roteável na internet pública?
    ///
    /// A lista é por EXCLUSÃO, não por inclusão: recusar o que se sabe interno e aceitar o resto.
    /// O contrário — tentar enumerar o que é público — erra para o lado perigoso a cada faixa nova
    /// que a IANA reservar.</summary>
    public static bool EhPublico(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;                    // 127.0.0.0/8, ::1
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;

            // fc00::/7 — "unique local", o equivalente IPv6 de 10./192.168.
            var b0 = ip.GetAddressBytes()[0];
            if ((b0 & 0xFE) == 0xFC) return false;

            // ::ffff:10.0.0.1 — o mesmo IP privado vestido de IPv6. Sem desembrulhar, é o
            // caminho mais curto para furar todas as regras acima.
            if (ip.IsIPv4MappedToIPv6) return EhPublico(ip.MapToIPv4());

            return true;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;   // nem IPv4 nem IPv6

        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 => false,                                   // 0.0.0.0/8
            10 => false,                                  // 10.0.0.0/8
            127 => false,                                 // loopback (já coberto; explícito)
            169 when b[1] == 254 => false,                // 169.254.0.0/16 — link-local e METADADOS
            172 when b[1] >= 16 && b[1] <= 31 => false,   // 172.16.0.0/12
            192 when b[1] == 168 => false,                // 192.168.0.0/16
            192 when b[1] == 0 && b[2] == 0 => false,     // 192.0.0.0/24 — atribuições especiais
            100 when b[1] >= 64 && b[1] <= 127 => false,  // 100.64.0.0/10 — CGNAT
            >= 224 => false,                              // multicast e reservado
            _ => true
        };
    }
}
