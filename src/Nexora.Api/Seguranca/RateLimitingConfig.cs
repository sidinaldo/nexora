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
    /// <summary>true em PRODUCAO atras de proxy: confia no cabecalho de IP encaminhado para achar o
    /// IP real. Deixe FALSE em dev/local — senao um cliente forjando o header vira qualquer IP.
    /// So ligue quando o UNICO caminho de entrada for o proxy.</summary>
    public bool ConfiarProxyReverso { get; set; }

    /// <summary>===================== QUAL CABECALHO TRAZ O IP DO CLIENTE =====================
    /// Vazio = `X-Forwarded-For`, o padrao do ASP.NET e o que um proxy comum manda.
    ///
    /// ⚠️ ATRAS DO CLOUDFLARE, PONHA `CF-Connecting-IP`. O motivo e que o `X-Forwarded-For` que
    /// chega ali NAO e confiavel do jeito que parece: o cliente pode mandar o dele, e o que a borda
    /// faz e ACRESCENTAR o real ao fim da lista. Ja o `CF-Connecting-IP` a borda SEMPRE reescreve
    /// com o IP real, descartando o que o cliente tiver mandado — e por isso ele e o unico valor
    /// que nao depende de contar posicoes numa lista que o atacante controla.
    ///
    /// O que se perde ao errar isto nao e pouco, e sao duas coisas distintas:
    ///
    ///   • O RATE LIMIT COLAPSA. Todo cliente vira o IP da borda, entao os baldes por IP viram UM
    ///     — e o primeiro que errar a senha cinco vezes tranca o login do sistema inteiro;
    ///   • A ATRIBUICAO DE ANUNCIO PIORA. O `client_ip_address` mandado para a Meta (INT-4) passa a
    ///     ser o do datacenter do Cloudflare, e ela casa o lead com a pessoa errada, ou com
    ///     ninguem. Sem erro nenhum: ela responde 200 e o casamento so nao acontece.
    ///
    /// ⚠️ SO TEM EFEITO COM `ConfiarProxyReverso = true`. Confiar num cabecalho sem saber que o
    /// unico caminho de entrada e o proxy e deixar qualquer um escolher o proprio IP.
    /// ================================================================================</summary>
    public string? CabecalhoIpReal { get; set; }

    public int GeralPorMinuto { get; set; } = 100;    // demais /api/* autenticados, por usuario
    public int LoginPorMinuto { get; set; } = 5;      // POST /api/auth/login, por IP
    public int WebhookPorMinuto { get; set; } = 300;  // POST /api/webhook/evolution, por IP
    public int SenhaPor15Min { get; set; } = 5;       // aceite de convite e reset, por IP+token

    /// <summary>CONSULTA de convite e de redefinicao (os dois GET por token), por IP.
    ///
    /// Folgado (30/min): a pessoa abre o link, recarrega, volta -- tres ou quatro visitas sao
    /// normais, e apertar isso impediria alguem de aceitar o proprio convite.
    ///
    /// ⚠️ POR IP, E NAO POR IP+TOKEN como o `PolSenha`. Quem sonda token VARIA o token, entao uma
    /// particao que inclui o token da um balde novo a cada tentativa e nao limita nada. O ponto
    /// aqui nao e forca bruta -- o token tem 32 bytes de CSPRNG -- e sim nao deixar rota anonima
    /// sem teto nenhum.</summary>
    public int ConsultaTokenPorMinuto { get; set; } = 30;

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

    /// <summary>Leitura da area do operador, por IP (OPE-1). Folgado (120/min) porque uma tela de
    /// lista pagina, filtra e recarrega, e apertar isso so atrapalha quem opera.
    ///
    /// ⚠️ MAS NAO PODE SER AUSENTE. Rota anonima NAO tem limite neste sistema -- o limitador global
    /// devolve `NoLimiter("anon")` para quem nao esta autenticado. Sem politica nomeada, esta rota
    /// seria uma leitura de TODAS as empresas, destravada, atras de um segredo estatico.</summary>
    public int OperadorPorMinuto { get; set; } = 120;

    /// <summary>ESCRITA da area do operador, por IP (OPE-1). Apertado (20/min) pelo mesmo raciocinio
    /// do cadastro de empresa: o fluxo e manual -- alguem ajusta o plano de um cliente de cada vez,
    /// nao em rajada. Se a chave vazar, o teto limita o estrago a 20 alteracoes por minuto por
    /// origem, e cada uma delas deixa linha na trilha com ator `Operador`.</summary>
    public int OperadorEscritaPorMinuto { get; set; } = 20;

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
    public const string PolOperador = "operador";
    public const string PolOperadorEscrita = "operador-escrita";
    public const string PolConsultaToken = "consulta-token";

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

            // --- CONSULTA de convite/redefinicao por token: por IP, janela de 1 minuto.
            //
            //     Estes dois GET ficaram sem politica nenhuma ate o OPE-1, e rota anonima sem
            //     politica nomeada NAO TEM TETO neste sistema. Foram achados pela varredura que o
            //     `RotasAnonimasTests` passou a fazer -- que e exatamente para o que ela existe. ---
            options.AddPolicy(PolConsultaToken, ctx =>
                Fixa($"consulta-token:{Ip(ctx)}", op.ConsultaTokenPorMinuto, TimeSpan.FromMinutes(1)));

            // --- AREA DO OPERADOR (OPE-1): por IP, janela de 1 minuto, leitura e escrita
            //     separadas.
            //
            //     ⚠️ ESTAS POLITICAS SAO OBRIGATORIAS, nao defensivas. As rotas do operador sao
            //     `[AllowAnonymous]` -- a credencial e a chave no cabecalho, nao um JWT --, e o
            //     GlobalLimiter acima devolve `NoLimiter("anon")` para quem nao esta autenticado.
            //     Esquecer o `[EnableRateLimiting]` numa acao deixa uma leitura de TODAS as
            //     empresas sem teto nenhum atras de um segredo estatico. Ha teste varrendo o
            //     controller atras de acao sem politica. ---
            options.AddPolicy(PolOperador, ctx =>
                Fixa($"operador:{Ip(ctx)}", op.OperadorPorMinuto, TimeSpan.FromMinutes(1)));

            options.AddPolicy(PolOperadorEscrita, ctx =>
                Fixa($"operador-escrita:{Ip(ctx)}", op.OperadorEscritaPorMinuto, TimeSpan.FromMinutes(1)));

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
