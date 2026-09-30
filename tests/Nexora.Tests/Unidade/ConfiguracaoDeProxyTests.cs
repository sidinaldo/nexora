using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Api.Seguranca;

namespace Nexora.Tests.Unidade;

/// <summary>DE ONDE SAI O IP DO CLIENTE atrás de um proxy.
///
/// ===================== POR QUE ISTO PRECISA DE TESTE DURO =====================
/// Errar aqui não dá erro. A aplicação sobe, responde, e duas coisas quebram em silêncio:
///
///   • **o rate limit colapsa** — todo cliente vira o IP da borda, os baldes por IP viram um só, e
///     o primeiro que errar a senha cinco vezes tranca o login do sistema inteiro;
///   • **a atribuição de anúncio piora** — o `client_ip_address` que vai para a Meta (INT-4) passa
///     a ser o do datacenter, ela responde 200, e o casamento simplesmente não acontece.
///
/// Os dois só aparecem depois, e o segundo aparece como "o produto não funciona".
///
/// ⚠️ O TESTE USA O MIDDLEWARE DE VERDADE do ASP.NET, montado com `UseForwardedHeaders` — e não uma
/// releitura da regra. É a mesma disciplina de `PermissoesTests`, que avalia a política pelo
/// `IAuthorizationService` real em vez de reimplementar a tabela.
/// ============================================================================</summary>
public class ConfiguracaoDeProxyTests
{
    private const string DaBorda = "198.51.100.7";     // o IP do proxy, que chega no socket
    private const string DoCliente = "203.0.113.42";   // o IP de quem realmente pediu

    /// <summary>Monta o pipeline com o `UseForwardedHeaders` real e devolve o IP que a aplicação
    /// enxerga depois dele.</summary>
    private static async Task<string?> IpVistoAsync(
        OpcoesRateLimit rate, Action<HttpContext> prepararRequisicao)
    {
        var servicos = new ServiceCollection().AddLogging().BuildServiceProvider();

        var opcoes = new ForwardedHeadersOptions();
        ConfiguracaoDeProxy.Aplicar(opcoes, rate);

        var pipeline = new ApplicationBuilder(servicos)
            .UseMiddleware<ForwardedHeadersMiddleware>(Options.Create(opcoes))
            .Use(_ => ctx => Task.CompletedTask)
            .Build();

        var contexto = new DefaultHttpContext { RequestServices = servicos };
        contexto.Connection.RemoteIpAddress = IPAddress.Parse(DaBorda);
        prepararRequisicao(contexto);

        await pipeline(contexto);
        return contexto.Connection.RemoteIpAddress?.ToString();
    }

    [Fact]
    public async Task ATRAS_DO_CLOUDFLARE_O_IP_SAI_DO_CF_CONNECTING_IP()
    {
        // ⚠️ E O `X-Forwarded-For` MENTE DE PROPÓSITO NESTE TESTE. É o cenário real: o cliente pode
        // mandar o cabeçalho que quiser, e a borda do Cloudflare ACRESCENTA o IP real ao fim em vez
        // de reescrever. Quem confia no `X-Forwarded-For` ali está contando posições numa lista que
        // o atacante controla; o `CF-Connecting-IP` a borda sempre reescreve.
        var ip = await IpVistoAsync(
            new OpcoesRateLimit
            {
                ConfiarProxyReverso = true,
                CabecalhoIpReal = ConfiguracaoDeProxy.CloudflareIpReal
            },
            ctx =>
            {
                ctx.Request.Headers["CF-Connecting-IP"] = DoCliente;
                ctx.Request.Headers["X-Forwarded-For"] = "1.2.3.4";   // forjado pelo cliente
            });

        Assert.Equal(DoCliente, ip);
    }

    [Fact]
    public async Task SEM_CONFIGURAR_NADA_CONTINUA_VALENDO_O_X_FORWARDED_FOR()
    {
        // O padrão do ASP.NET e o que um proxy comum manda. Trocar isso por engano faria o IP sumir
        // em vez de melhorar, então vazio tem de significar "não mexi".
        var ip = await IpVistoAsync(
            new OpcoesRateLimit { ConfiarProxyReverso = true },
            ctx => ctx.Request.Headers["X-Forwarded-For"] = DoCliente);

        Assert.Equal(DoCliente, ip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CABECALHO_VAZIO_NAO_E_CABECALHO(string? configurado)
    {
        // ⚠️ ESTE TESTE NASCEU DE UMA SABOTAGEM QUE PASSOU. A versão anterior só cobria `null`, e
        // trocar `IsNullOrWhiteSpace` por `is not null` não derrubava nada — mas quebraria em
        // produção, porque a variável VAZIA é o caso mais provável de todos: o compose escreve
        // `${VAR:-}` em toda parte, e um `.env` com a chave sem valor entrega string vazia, não
        // nulo.
        //
        // Com um nome de cabeçalho vazio, o middleware procuraria um cabeçalho que não existe — e o
        // sintoma seria o IP da borda em todo mundo, idêntico a não ter configurado nada.
        var ip = await IpVistoAsync(
            new OpcoesRateLimit { ConfiarProxyReverso = true, CabecalhoIpReal = configurado },
            ctx => ctx.Request.Headers["X-Forwarded-For"] = DoCliente);

        Assert.Equal(DoCliente, ip);
    }

    [Fact]
    public async Task SEM_CONFIAR_NO_PROXY_O_CABECALHO_E_IGNORADO()
    {
        // ⚠️ A GUARDA QUE IMPEDE QUALQUER UM DE ESCOLHER O PRÓPRIO IP. Sem `ConfiarProxyReverso`,
        // `KnownProxies`/`KnownNetworks` seguem nos defaults de loopback — e um cabeçalho vindo de
        // um IP público não é honrado. Ligar a confiança sem que o proxy seja o ÚNICO caminho de
        // entrada é entregar o rate limit de bandeja.
        var ip = await IpVistoAsync(
            new OpcoesRateLimit
            {
                ConfiarProxyReverso = false,
                CabecalhoIpReal = ConfiguracaoDeProxy.CloudflareIpReal
            },
            ctx => ctx.Request.Headers["CF-Connecting-IP"] = DoCliente);

        Assert.Equal(DaBorda, ip);
    }

    [Fact]
    public async Task ATRAS_DE_PROXY_LOCAL_SEM_CONFIANCA_O_CABECALHO_NAO_VALE()
    {
        // ⚠️ O BURACO QUE A REVISÃO ACHOU, e o teste anterior dava falsa garantia: ele usava um
        // par PÚBLICO (198.51.100.7), que os defaults de `KnownProxies` já recusam de qualquer
        // jeito. O caso perigoso é o par em LOOPBACK — um nginx ou Caddy no mesmo host.
        //
        // Ali o par imediato É confiável por padrão, e um proxy comum repassa o
        // `CF-Connecting-IP` do cliente sem tocar nele. Com o nome do cabeçalho configurado
        // fora do portão de `ConfiarProxyReverso`, o middleware passava a lê-lo — e qualquer um
        // mandava `CF-Connecting-IP: <aleatório>` a cada requisição para nunca dividir balde de
        // rate limit, derrotando o teto de 5 logins por minuto.
        var servicos = new ServiceCollection().AddLogging().BuildServiceProvider();
        var opcoes = new ForwardedHeadersOptions();
        ConfiguracaoDeProxy.Aplicar(opcoes, new OpcoesRateLimit
        {
            ConfiarProxyReverso = false,
            CabecalhoIpReal = ConfiguracaoDeProxy.CloudflareIpReal
        });

        var pipeline = new ApplicationBuilder(servicos)
            .UseMiddleware<ForwardedHeadersMiddleware>(Options.Create(opcoes))
            .Use(_ => _ => Task.CompletedTask)
            .Build();

        var contexto = new DefaultHttpContext { RequestServices = servicos };
        contexto.Connection.RemoteIpAddress = IPAddress.Loopback;   // o proxy local
        contexto.Request.Headers["CF-Connecting-IP"] = "9.9.9.9";   // forjado pelo cliente

        await pipeline(contexto);

        Assert.Equal(IPAddress.Loopback.ToString(), contexto.Connection.RemoteIpAddress?.ToString());
    }

    [Fact]
    public async Task ESPACO_EM_BRANCO_NO_NOME_NAO_QUEBRA_O_CABECALHO()
    {
        // O valor vem de variável de ambiente, e variável de ambiente pega espaço com facilidade.
        // Sem o `Trim`, `"CF-Connecting-IP "` viraria um cabeçalho que nunca existe — e o sintoma
        // seria o IP da borda em todo mundo, exatamente como se a opção não tivesse sido posta.
        var ip = await IpVistoAsync(
            new OpcoesRateLimit
            {
                ConfiarProxyReverso = true,
                CabecalhoIpReal = "  CF-Connecting-IP  "
            },
            ctx => ctx.Request.Headers["CF-Connecting-IP"] = DoCliente);

        Assert.Equal(DoCliente, ip);
    }
}
