using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Nexora.Core.Email;
using Nexora.Core.Entidades;
using Nexora.Core.FollowUp;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Core.Captacao;
using Nexora.Core.Webhooks;
using Nexora.Infra.Webhooks;
using Nexora.Infra.Armazenamento;
using Nexora.Infra.Captacao;
using Nexora.Infra.Email;
using Nexora.Infra.Evolution;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Infra;

public static class ServicosInfra
{
    /// <summary>Monta o NpgsqlDataSource com os enums nativos e registra o DbContext.
    ///
    /// Os enums precisam ser mapeados AQUI alem do HasPostgresEnum do OnModelCreating:
    /// o primeiro ensina a MIGRATION a criar o tipo, este ensina o DRIVER a ler e escrever
    /// o valor. Falta o segundo e a aplicacao compila, sobe, e estoura na primeira consulta.</summary>
    public static IServiceCollection AdicionarInfra(this IServiceCollection servicos, string connectionString)
    {
        var fonte = new NpgsqlDataSourceBuilder(connectionString);
        MapearEnums(fonte);

        servicos.AddSingleton(fonte.Build());
        servicos.TryAddTimeProviderPadrao();

        // O coletor da trilha (AUD-1) é POR ESCOPO: ele acumula as declarações dos serviços
        // durante uma requisição e é esvaziado a cada SaveChanges. Singleton misturaria eventos
        // de requisições concorrentes; transient perderia a declaração entre o serviço e o
        // interceptor.
        servicos.AddScoped<ColetorAuditoria>();

        servicos.AddDbContext<NexoraDbContext>((sp, opcoes) => opcoes
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
            .AddInterceptors(
                new InterceptorAuditoria(sp.GetRequiredService<TimeProvider>()),
                // DEPOIS do carimbo de tempo, e de propósito: `atualizado_em` já está escrito
                // quando o diff é montado — e é por isso que ele está na lista de ignoradas.
                new InterceptorTrilha(
                    sp.GetRequiredService<ColetorAuditoria>(),
                    sp.GetRequiredService<IContextoEmpresa>(),
                    sp.GetRequiredService<TimeProvider>())));

        // Servicos que os CONTROLLERS enxergam. A Api depende so das interfaces do Core —
        // nenhum controller injeta NexoraDbContext, e nenhum importa Nexora.Infra.
        servicos.AddScoped<IServicoAutenticacao, ServicoAutenticacao>();
        servicos.AddScoped<IServicoCadastroEmpresa, ServicoCadastroEmpresa>();
        servicos.AddScoped<IServicoConexoes, ServicoConexoes>();
        servicos.AddScoped<IServicoConversas, ServicoConversas>();
        servicos.AddScoped<IServicoCaixa, ServicoCaixa>();
        servicos.AddScoped<IServicoPainel, ServicoPainel>();
        servicos.AddScoped<IServicoEquipe, ServicoEquipe>();
        servicos.AddScoped<IServicoContatos, ServicoContatos>();
        servicos.AddScoped<IServicoFunil, ServicoFunil>();
        // O funil configuravel. Leitura do quadro e ServicoFunil (operacao diaria); a forma
        // do funil e configuracao, e so o dono mexe.
        servicos.AddScoped<IServicoEtapas, ServicoEtapas>();
        servicos.AddScoped<IServicoMeuDia, ServicoMeuDia>();
        servicos.AddScoped<IServicoDashboard, ServicoDashboard>();
        servicos.AddScoped<IServicoVendas, ServicoVendas>();
        servicos.AddScoped<IServicoTrilha, ServicoTrilha>();
        servicos.AddScoped<IServicoSerie, ServicoSerie>();
        servicos.AddScoped<IServicoAtividades, ServicoAtividades>();
        // A demonstração agora é um TENANT com dados de verdade, não um gerador de números —
        // ver docs/PI-4b.md. O `ServicoDashboardDemo` foi removido junto com a rota dele.
        servicos.AddScoped<IServicoSeedDemonstracao, ServicoSeedDemonstracao>();
        // Captação por formulário do site: o público resolve o tenant pela chave, o de
        // configuração vive na área logada e usa o query filter normal.
        servicos.AddScoped<IServicoCaptura, ServicoCaptura>();
        servicos.AddScoped<IServicoFormularios, ServicoFormularios>();
        // Captação por QR Code e link rastreável. O desenho do QR é SINGLETON e sem estado — a
        // instância do QRCodeGenerator é criada por chamada dentro dele, ver GeradorQrCoder.
        servicos.AddSingleton<IGeradorQrCode, GeradorQrCoder>();
        servicos.AddScoped<IServicoCanais, ServicoCanais>();
        servicos.AddScoped<IServicoLembretes, ServicoLembretes>();
        servicos.AddScoped<IServicoFeriados, ServicoFeriados>();
        servicos.AddScoped<IServicoConfiguracao, ServicoConfiguracao>();
        servicos.AddScoped<IServicoOnboarding, ServicoOnboarding>();
        // Dados falsos de desenvolvimento. O controller que o expoe checa o ambiente em tempo
        // de execucao — ver DevController.
        servicos.AddScoped<IServicoSemente, ServicoSemente>();
        // Dialogos de verdade nas conversas que ja existem. Separado do semeador geral de
        // proposito: aquele apaga e recria o tenant, este so reescreve a thread.
        servicos.AddScoped<IServicoSementeConversas, ServicoSementeConversas>();

        // Interfaces de dados do Core -> implementacao SQL/EF. E o que permite o protocolo de
        // envio viver no Core sem enxergar o DbContext.
        servicos.AddScoped<IDadosMensagem, DadosMensagem>();

        // Camada de tempo. O motor mora no Core (regra pura); o SQL da elegibilidade, na Infra.
        servicos.AddScoped<IDadosFollowUp, DadosFollowUp>();
        servicos.AddScoped<MotorFollowUp>();

        return servicos;
    }

    /// <summary>Webhook de SAÍDA (INT-3): o Nexora avisando um sistema do cliente.
    ///
    /// Separado do `AdicionarInfra` porque precisa de `HttpClient` — a mesma razão de
    /// `AdicionarWhatsApp` ser um método próprio.
    ///
    /// ===================== O HttpClient AQUI É DIFERENTE =====================
    /// Ele chama um endereço ESCOLHIDO PELO CLIENTE. Duas travas que o cliente da Evolution não
    /// precisa ter:
    ///
    ///   • `AllowAutoRedirect = false`. Redirecionamento é a forma clássica de furar a checagem de
    ///     SSRF: a URL cadastrada é pública, responde 302, e o destino é `127.0.0.1`. Nós validamos
    ///     a URL, não o que ela mandar seguir;
    ///   • `MaxConnectionsPerServer`. Um receptor lento não pode consumir o pool de conexões da
    ///     aplicação inteira.
    /// ========================================================================</summary>
    public static IServiceCollection AdicionarWebhooksSaida(this IServiceCollection servicos)
    {
        servicos.AddSingleton<IResolvedorDns, ResolvedorDns>();

        servicos.AddHttpClient<ClienteWebhook>(http =>
            {
                // O timeout REAL é o da PoliticaEntrega, aplicado por tentativa com um
                // CancellationToken. Este é um teto de segurança bem acima dele: se o do
                // HttpClient fosse o menor, ele venceria antes e o erro viria sem a mensagem em
                // português que a tela do dono precisa.
                http.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                MaxConnectionsPerServer = 4
            });

        servicos.AddScoped<IClienteWebhook>(sp => sp.GetRequiredService<ClienteWebhook>());
        servicos.AddScoped<IPublicadorEventos, PublicadorEventos>();
        servicos.AddScoped<IServicoWebhooks, ServicoWebhooks>();
        servicos.AddScoped<MotorWebhooks>();

        return servicos;
    }

    /// <summary>Cliente HTTP da Evolution API + o processador do webhook.</summary>
    public static IServiceCollection AdicionarWhatsApp(
        this IServiceCollection servicos, OpcoesEvolution opcoes, OpcoesEnvio? opcoesEnvio = null)
    {
        servicos.AddSingleton(opcoes);
        servicos.AddSingleton(opcoesEnvio ?? new OpcoesEnvio());

        // O dono unico do protocolo de envio. Lembrete e resposta manual passam por ele.
        servicos.AddScoped<EnviadorMensagem>();

        // Typed client: o ServicoConexoes injeta IClienteWhatsApp, e o mesmo objeto atende as
        // operacoes de instancia (QR, status) e as de mensagem.
        servicos.AddHttpClient<ClienteEvolution>(http =>
        {
            http.BaseAddress = new Uri(opcoes.BaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Add("apikey", opcoes.ApiKey);
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        servicos.AddScoped<IClienteWhatsApp>(sp => sp.GetRequiredService<ClienteEvolution>());

        servicos.AddScoped<IProcessadorWebhookWhatsApp, ProcessadorEventoEvolution>();

        return servicos;
    }

    /// <summary>E-mail transacional. O REMETENTE e escolhido aqui, por configuracao — e o unico
    /// lugar do sistema que sabe se o e-mail sai por SMTP ou vai para disco.
    ///
    /// Duas IMPLEMENTACOES da mesma interface, nao um `if` no meio do notificador: com `if`, o
    /// caminho de producao nunca roda em dev e o de dev vira codigo morto em producao — os dois
    /// divergem e ninguem percebe ate o deploy.</summary>
    public static IServiceCollection AdicionarEmail(
        this IServiceCollection servicos, OpcoesEmail opcoes)
    {
        servicos.AddSingleton(opcoes);

        if (string.Equals(opcoes.Provedor, "smtp", StringComparison.OrdinalIgnoreCase))
            servicos.AddScoped<IRemetenteEmail, RemetenteSmtp>();
        else
            servicos.AddScoped<IRemetenteEmail, RemetenteArquivo>();

        servicos.AddScoped<INotificadorEmail, NotificadorEmail>();
        return servicos;
    }

    /// <summary>Armazenamento da midia recebida. FASE 1: disco. Trocar por S3/R2 na fase 2 e
    /// substituir esta linha.</summary>
    public static IServiceCollection AdicionarArmazenamento(
        this IServiceCollection servicos, OpcoesMidia opcoes)
    {
        servicos.AddSingleton(opcoes);
        servicos.AddSingleton<IArmazenamentoMidia>(_ => new ArmazenamentoDisco(opcoes));
        return servicos;
    }

    /// <summary>Os enums nativos, num lugar so. Publico porque os testes montam o proprio
    /// data source e precisam da MESMA lista — duas listas divergem no dia em que alguem
    /// adiciona um enum, e o sintoma e um teste que passa com o banco errado.
    ///
    /// Esta lista tem que bater com os HasPostgresEnum do NexoraDbContext. La ela ensina a
    /// MIGRATION a criar o tipo; aqui ensina o DRIVER a ler e escrever o valor.</summary>
    public static void MapearEnums(NpgsqlDataSourceBuilder fonte)
    {
        fonte.MapEnum<PapelUsuario>("papel_usuario_enum");
        fonte.MapEnum<StatusUsuario>("status_usuario_enum");
        fonte.MapEnum<StatusConexao>("status_conexao_enum");
        fonte.MapEnum<StatusVenda>("status_venda_enum");
        fonte.MapEnum<OrigemLead>("origem_lead_enum");
        fonte.MapEnum<DirecaoMensagem>("direcao_mensagem_enum");
        fonte.MapEnum<TipoMidia>("tipo_midia_enum");
        fonte.MapEnum<StatusConversa>("status_conversa_enum");
        fonte.MapEnum<StatusLembrete>("status_lembrete_enum");
        fonte.MapEnum<OrigemLembrete>("origem_lembrete_enum");
        fonte.MapEnum<AbrangenciaFeriado>("abrangencia_feriado_enum");
        fonte.MapEnum<EventoWebhook>("evento_webhook_enum");
        fonte.MapEnum<StatusEntregaWebhook>("status_entrega_webhook_enum");
    }

    /// <summary>TimeProvider.System como padrao; os testes registram um relogio falso antes.</summary>
    private static void TryAddTimeProviderPadrao(this IServiceCollection servicos)
    {
        if (servicos.Any(d => d.ServiceType == typeof(TimeProvider))) return;
        servicos.AddSingleton(TimeProvider.System);
    }
}
