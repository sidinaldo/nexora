using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Nexora.Api;
using Nexora.Api.Controllers;
using Nexora.Api.Realtime;
using Nexora.Api.Seguranca;
using Nexora.Api.Servicos;
using Nexora.Core;
using Nexora.Core.Whatsapp;
using Nexora.Infra;
using Nexora.Infra.Armazenamento;
using Nexora.Infra.Email;
using Nexora.Infra.Evolution;
using Nexora.Infra.Persistencia;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// Segredos vem do user-secrets em dev (nao vao para o git):
//   dotnet user-secrets set "ConnectionStrings:Nexora" "..."   --project src/Nexora.Api
//   dotnet user-secrets set "Jwt:Chave" "<>=32 caracteres>"    --project src/Nexora.Api
var conexao = cfg.GetConnectionString("Nexora")
    ?? throw new InvalidOperationException(
        "Falta a connection string 'Nexora'. Em dev: " +
        "dotnet user-secrets set \"ConnectionStrings:Nexora\" \"...\" --project src/Nexora.Api");

var jwt = cfg.GetSection("Jwt").Get<OpcoesJwt>() ?? new OpcoesJwt();
// Validacao no BOOT: chave curta derruba a aplicacao com mensagem acionavel, em vez de
// aceitar um token fraco e so descobrir em producao.
if (jwt.Chave.Length < 32)
    throw new InvalidOperationException(
        "Jwt:Chave precisa de ao menos 32 caracteres. Em dev: " +
        "dotnet user-secrets set \"Jwt:Chave\" \"...\" --project src/Nexora.Api");

builder.Services.AddControllers(o => o.Filters.Add<FiltroRegraDeNegocio>()).AddJsonOptions(o =>
{
    // Enum como TEXTO, nao como numero. Sem isto o painel recebe `papel: 2` e teria que
    // manter a ordem do enum C# duplicada no TypeScript — e qualquer valor novo inserido
    // no meio do enum quebraria o front em silencio.
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // `<input type="time">` manda "14:30", sem segundos — e o conversor padrao de TimeOnly
    // exige "14:30:00" e devolve 400. Aceitar as duas formas AQUI, e nao no cliente: o
    // servidor e quem define o contrato, e todo navegador manda o formato curto.
    o.JsonSerializerOptions.Converters.Add(new ConversorHoraFlexivel());
    o.JsonSerializerOptions.Converters.Add(new ConversorHoraFlexivelNulavel());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "nexora", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Cole aqui o token devolvido por POST /api/auth/login"
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference
            { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
    });
});

builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<GeradorToken>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Emissor,
            ValidAudience = jwt.Audiencia,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Chave)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // O SignalR (WebSocket) NAO manda header Authorization — o browser nao deixa.
        // O token vem na query string, e e aqui que ele e resgatado. Sem isto, o hub
        // conecta anonimo e o cliente nunca entra no grupo da empresa: o realtime
        // simplesmente nao chega, sem erro nenhum.
        //
        // O hub em si (AddSignalR/MapHub) chega junto com a caixa de entrada; este resgate
        // ja fica pronto porque e justamente a parte que se esquece.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hub"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddSignalR();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IContextoEmpresa, ContextoEmpresaHttp>();

builder.Services.AdicionarInfra(conexao);
builder.Services.AdicionarWhatsApp(
    cfg.GetSection("Evolution").Get<OpcoesEvolution>() ?? new OpcoesEvolution(),
    cfg.GetSection("Envio").Get<OpcoesEnvio>() ?? new OpcoesEnvio());
builder.Services.AdicionarArmazenamento(
    cfg.GetSection("Midia").Get<OpcoesMidia>() ?? new OpcoesMidia());

// E-mail transacional. As CREDENCIAIS vem de user-secrets/variavel de ambiente, nunca do
// appsettings versionado:
//   dotnet user-secrets set "Email:Senha" "..." --project src/Nexora.Api
// O padrao (Email:Provedor ausente) e "arquivo": um clone limpo sobe e funciona sem SMTP, e
// errar para o lado de NAO enviar e sempre mais barato que errar para o lado de enviar.
builder.Services.AdicionarEmail(cfg.GetSection("Email").Get<OpcoesEmail>() ?? new OpcoesEmail());

// O segredo do webhook vem de user-secrets. Sem ele o WebhookController recusa TUDO — e o
// comportamento certo: a Evolution nao assina o payload, entao um segredo vazio seria um
// endpoint aberto para qualquer um forjar "o contato respondeu".
builder.Services.AddSingleton(cfg.GetSection("Webhook").Get<OpcoesWebhook>() ?? new OpcoesWebhook());

// Chave que autoriza CRIAR EMPRESA. Vazia = cadastro desligado, e esse e o padrao — um clone
// que alguem suba sem configurar nada nao pode ficar com criacao de conta aberta:
//   dotnet user-secrets set "Cadastro:ChaveAdministracao" "..." --project src/Nexora.Api
builder.Services.AddSingleton(cfg.GetSection("Cadastro").Get<OpcoesCadastro>() ?? new OpcoesCadastro());

// Fila de segundo plano: hoje so o e-mail do "esqueci minha senha" passa por aqui. Ela existe
// para o tempo de resposta daquele endpoint nao depender da velocidade do relay SMTP — senao da
// para enumerar contas pelo cronometro. UMA tentativa, sem retry: ver IFilaSegundoPlano.
builder.Services.AddSingleton<FilaSegundoPlano>();
builder.Services.AddSingleton<IFilaSegundoPlano>(sp => sp.GetRequiredService<FilaSegundoPlano>());
builder.Services.AddHostedService<ProcessadorSegundoPlano>();

// Liga o comando de seed do tenant de DEMONSTRACAO. Desligado por padrao — em producao ninguem
// liga, e sem isto a rota devolve 404:
//   dotnet user-secrets set "Demonstracao:Habilitado" "true" --project src/Nexora.Api
builder.Services.AddSingleton(
    cfg.GetSection("Demonstracao").Get<OpcoesDemonstracao>() ?? new OpcoesDemonstracao());

// O Core so conhece INotificadorPainel; quem sabe o que e SignalR e a Api.
builder.Services.AddSingleton<INotificadorPainel, NotificadorSignalR>();

// A rodada diaria de follow-up. Nao roda no boot por padrao — em producao todo deploy
// dispararia mensagem para cliente. Ver a secao Agendador do appsettings.
builder.Services.AddSingleton(
    cfg.GetSection("Agendador").Get<OpcoesAgendador>() ?? new OpcoesAgendador());
builder.Services.AddHostedService<AgendadorFollowUp>();

// Rate limiting (nativo do .NET 8, em memoria — instancia unica). Ver RateLimitingConfig.
var opRate = cfg.GetSection("RateLimit").Get<OpcoesRateLimit>() ?? new OpcoesRateLimit();
builder.Services.AddSingleton(opRate);
builder.Services.AdicionarRateLimit(opRate);

// Atras de proxy/Cloudflare (ConfiarProxyReverso=true), o IP real vem no X-Forwarded-For — sem
// isto todo mundo vira o IP do proxy e um usuario bloqueia todos. Em dev fica desligado.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (opRate.ConfiarProxyReverso) { o.KnownNetworks.Clear(); o.KnownProxies.Clear(); }
});

// Health check: aplicacao viva + banco respondendo. Sem isto nenhum orquestrador consegue
// distinguir "ainda subindo" de "de pe, mas sem banco".
builder.Services.AddHealthChecks().AddDbContextCheck<NexoraDbContext>("banco");

// O painel Angular roda em outra origem. SignalR exige AllowCredentials, e AllowCredentials
// e incompativel com AllowAnyOrigin — por isso a origem e explicita.
const string PainelCors = "painel";
builder.Services.AddCors(o => o.AddPolicy(PainelCors, p =>
{
    if (builder.Environment.IsDevelopment())
    {
        // DEV: qualquer origem de LOOPBACK. Em desenvolvimento o painel nem sempre sai do
        // `ng serve` na 4200 — Live Preview do editor, `http-server` sobre o dist e afins
        // sobem numa porta efemera que muda a cada execucao, e a lista fixa barra tudo com
        // "No 'Access-Control-Allow-Origin' header is present", que nao diz o que fazer.
        //
        // SetIsOriginAllowed e a unica forma de combinar origem dinamica com
        // AllowCredentials: AllowAnyOrigin() e ILEGAL junto de credenciais, e o SignalR
        // exige credenciais. O predicado restringe a loopback — nao e "libera geral".
        p.SetIsOriginAllowed(origem =>
            Uri.TryCreate(origem, UriKind.Absolute, out var u) && u.IsLoopback);
    }
    else
    {
        // PRODUCAO: lista explicita, sempre. Ver a secao Cors:Origens do appsettings.
        p.WithOrigins(cfg.GetSection("Cors:Origens").Get<string[]>() ?? []);
    }

    p.AllowAnyHeader().AllowAnyMethod().AllowCredentials()
     // Retry-After nao e header CORS-safelisted: sem expor, o Angular nao consegue ler os
     // segundos do 429 para a contagem regressiva do botao de login.
     .WithExposedHeaders("Retry-After");
}));

var app = builder.Build();

// Primeiro middleware: corrige o IP real a partir do X-Forwarded-For (quando atras de proxy),
// para o rate limiter e os logs enxergarem o cliente, nao o proxy.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(PainelCors);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();   // depois do Authentication: o limite "geral" particiona por usuario logado
app.MapControllers();
app.MapHub<HubPainel>("/hub/painel");
app.MapHealthChecks("/health");

app.Run();

// Para o teste de integracao alcancar a Program (WebApplicationFactory).
public partial class Program;
