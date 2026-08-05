using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Nexora.Api.Seguranca;

/// <summary>Limites por politica. Vem da secao "RateLimit" do appsettings — tunavel sem
/// recompilar. 100/min por usuario e folgadissimo para uso humano (um clique gera ~1
/// requisicao); so morde script e abuso.</summary>
public class OpcoesRateLimit
{
    /// <summary>true em PRODUCAO atras de proxy/Cloudflare: confia no X-Forwarded-For para achar o
    /// IP real. Deixe FALSE em dev/local — senao um cliente forjando o header vira qualquer IP.
    /// So ligue quando o UNICO caminho de entrada for o proxy.</summary>
    public bool ConfiarProxyReverso { get; set; }

    public int GeralPorMinuto { get; set; } = 100;    // demais /api/* autenticados, por usuario
    public int LoginPorMinuto { get; set; } = 5;      // POST /api/auth/login, por IP
    public int WebhookPorMinuto { get; set; } = 300;  // POST /api/webhook/evolution, por IP
    public int SenhaPor15Min { get; set; } = 5;       // aceite de convite e reset, por IP+token

    /// <summary>"Esqueci minha senha", por IP. APERTADO de propósito (3): cada tentativa dispara
    /// um e-mail para um endereço que quem pede escolhe — sem limite curto, o endpoint vira
    /// ferramenta de flood contra terceiros, e o domínio remetente é quem paga a reputação.
    ///
    /// O limite baixo também torna impraticável usar o TEMPO DE RESPOSTA para enumerar contas:
    /// 3 medições a cada 15 minutos por IP não sustentam ataque de timing.</summary>
    public int RecuperacaoPor15Min { get; set; } = 3;

    /// <summary>Criacao de empresa, por IP. Baixissimo (3 por hora) porque o fluxo e MANUAL:
    /// alguem da equipe cadastra um cliente por reuniao, nao em rajada. Um numero folgado aqui
    /// nao serve a ninguem e transforma vazamento da chave em criacao de tenants em massa.</summary>
    public int CadastroPorHora { get; set; } = 3;

    /// <summary>Captacao por formulario do site, por IP. 10/min e folgado para pessoa (ninguem
    /// preenche formulario dez vezes por minuto) e aperta script.
    ///
    /// Nao pode ser MAIS baixo: o formulario fica no site do cliente, e visitantes atras do mesmo
    /// NAT corporativo compartilham o IP. Um teto apertado recusaria lead legitimo — que e o
    /// oposto do que o endpoint existe para fazer.</summary>
    public int CapturaPorMinuto { get; set; } = 10;
}

/// <summary>Configura o rate limiter NATIVO do .NET 8 (System.Threading.RateLimiting). Limiter em
/// MEMORIA — a aplicacao e instancia unica. Escalar horizontal exige um backplane distribuido;
/// enquanto nao houver, nao suba duas instancias achando que o limite continua valendo.
///
/// Chaves de particao (o "balde" onde a contagem acontece):
///   • geral = por USUARIO logado (sub). So requisicoes autenticadas; anonimas caem nas
///             politicas nomeadas.
///   • login = por IP. Cobre tentativa rapida da MESMA conta E credential-stuffing (varias
///             contas do mesmo IP) — as duas caem no mesmo balde de IP. O ataque distribuido
///             contra UMA conta (varios IPs) e coberto pelo bloqueio persistente por conta
///             (ServicoAutenticacao), que e cross-IP.
///
/// As politicas de convite, redefinicao de senha e webhook entram junto com os endpoints
/// delas, nos blocos seguintes — nao ha o que limitar antes da rota existir.</summary>
public static class RateLimitingConfig
{
    public const string PolLogin = "login";
    public const string PolWebhook = "webhook";
    public const string PolSenha = "senha";
    public const string PolRecuperacao = "recuperacao";
    public const string PolCadastro = "cadastro";
    public const string PolCaptura = "captura";

    public static IServiceCollection AdicionarRateLimit(this IServiceCollection services, OpcoesRateLimit op)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // --- GERAL (global): so autenticadas, por usuario. Anonimas -> sem limite aqui
            //     (as sensiveis tem politica nomeada). ---
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                if (ctx.User?.Identity?.IsAuthenticated != true)
                    return RateLimitPartition.GetNoLimiter("anon");

                var id = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value   // sub mapeado
                      ?? ctx.User.FindFirst("sub")?.Value
                      ?? Ip(ctx);
                return Janela($"user:{id}", op.GeralPorMinuto, TimeSpan.FromMinutes(1));
            });

            // --- LOGIN: por IP, janela deslizante de 1 min. ---
            options.AddPolicy(PolLogin, ctx =>
                Janela($"login:{Ip(ctx)}", op.LoginPorMinuto, TimeSpan.FromMinutes(1)));

            // --- WEBHOOK: por IP (a Evolution e uma origem so) — teto alto, so anti-flood.
            //     O limite nao pode ser apertado: uma conversa movimentada gera varios eventos
            //     por segundo (upsert + update de ACK por mensagem), e barrar aqui faria a
            //     Evolution reentregar em loop. ---
            options.AddPolicy(PolWebhook, ctx =>
                Fixa($"webhook:{Ip(ctx)}", op.WebhookPorMinuto, TimeSpan.FromMinutes(1)));

            // --- SENHA (aceite de convite / redefinicao): por IP + token da URL. O corpo nao
            //     tem e-mail; o alvo do ataque e o token, entao ele entra na chave. ---
            options.AddPolicy(PolSenha, ctx =>
                Fixa($"senha:{Ip(ctx)}:{ctx.Request.RouteValues["token"]}",
                    op.SenhaPor15Min, TimeSpan.FromMinutes(15)));

            // --- RECUPERACAO ("esqueci minha senha"): por IP apenas. Aqui NAO ha token na rota
            //     — e justamente o endpoint que o gera —, entao a chave e so o IP.
            //
            //     A chave NAO inclui o e-mail do corpo, de proposito: com o e-mail na chave,
            //     cada endereco teria seu proprio balde e um script pediria reset para milhares
            //     de enderecos sem nunca estourar o limite. Por IP, o flood para no terceiro. ---
            options.AddPolicy(PolRecuperacao, ctx =>
                Fixa($"recuperacao:{Ip(ctx)}", op.RecuperacaoPor15Min, TimeSpan.FromMinutes(15)));

            // --- CADASTRO de empresa: por IP, janela de UMA HORA. O fluxo e manual (um cliente
            //     por reuniao), entao o teto e baixo de proposito: se a chave de administracao
            //     vazar, o estrago fica limitado a 3 tenants por hora por origem, em vez de
            //     milhares — e cada tenant falso arrasta usuario, conexao e 5 etapas de funil. ---
            options.AddPolicy(PolCadastro, ctx =>
                Fixa($"cadastro:{Ip(ctx)}", op.CadastroPorHora, TimeSpan.FromHours(1)));

            // --- CAPTACAO por formulario do site: por IP, janela FIXA de 1 minuto.
            //
            //     Fixa e nao deslizante: o teto e por minuto corrido, e o comportamento previsivel
            //     ("espere o minuto virar") e o que a mensagem de 429 consegue prometer. Deslizante
            //     liberaria vagas aos poucos, e o visitante do site nao tem como saber quando. ---
            options.AddPolicy(PolCaptura, ctx =>
                Fixa($"captura:{Ip(ctx)}", op.CapturaPorMinuto, TimeSpan.FromMinutes(1)));

            // --- Resposta 429 no padrao de erro da API + Retry-After + log do bloqueio. ---
            options.OnRejected = async (ctx, ct) =>
            {
                var segundos = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                    ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds))
                    : 60;

                var resp = ctx.HttpContext.Response;
                resp.StatusCode = StatusCodes.Status429TooManyRequests;
                resp.Headers.RetryAfter = segundos.ToString();
                resp.ContentType = "application/json; charset=utf-8";

                var politica = ctx.HttpContext.GetEndpoint()?.Metadata
                    .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "geral";
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RateLimit")
                    .LogWarning("Rate limit atingido: politica={Politica} ip={Ip} path={Path}",
                        politica, Ip(ctx.HttpContext), ctx.HttpContext.Request.Path);

                // Mensagem IDENTICA em qualquer caso — nunca revela se o e-mail existe ou a senha.
                await resp.WriteAsync(JsonSerializer.Serialize(new
                {
                    erro = $"Muitas tentativas. Aguarde {segundos} segundos e tente novamente."
                }), ct);
            };
        });
        return services;
    }

    private static RateLimitPartition<string> Fixa(string chave, int limite, TimeSpan janela) =>
        RateLimitPartition.GetFixedWindowLimiter(chave, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limite, Window = janela, QueueLimit = 0
        });

    private static RateLimitPartition<string> Janela(string chave, int limite, TimeSpan janela) =>
        RateLimitPartition.GetSlidingWindowLimiter(chave, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = limite, Window = janela, SegmentsPerWindow = 6, QueueLimit = 0
        });

    /// <summary>IP real da requisicao. Atras de proxy, ja vem corrigido pelo ForwardedHeaders
    /// (quando ConfiarProxyReverso=true); em dev e o IP do socket.</summary>
    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "sem-ip";
}
