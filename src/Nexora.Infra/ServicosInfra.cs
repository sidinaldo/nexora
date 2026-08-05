using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Nexora.Core.Email;
using Nexora.Core.Entidades;
using Nexora.Core.FollowUp;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Armazenamento;
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

        servicos.AddDbContext<NexoraDbContext>((sp, opcoes) => opcoes
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
            .AddInterceptors(new InterceptorAuditoria(sp.GetRequiredService<TimeProvider>())));

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
        servicos.AddScoped<IServicoMeuDia, ServicoMeuDia>();
        servicos.AddScoped<IServicoDashboard, ServicoDashboard>();
        servicos.AddScoped<IServicoSerie, ServicoSerie>();
        servicos.AddScoped<IServicoAtividades, ServicoAtividades>();
        // A demonstração agora é um TENANT com dados de verdade, não um gerador de números —
        // ver docs/PI-4b.md. O `ServicoDashboardDemo` foi removido junto com a rota dele.
        servicos.AddScoped<IServicoSeedDemonstracao, ServicoSeedDemonstracao>();
        servicos.AddScoped<IServicoLembretes, ServicoLembretes>();
        servicos.AddScoped<IServicoFeriados, ServicoFeriados>();
        servicos.AddScoped<IServicoConfiguracao, ServicoConfiguracao>();
        servicos.AddScoped<IServicoOnboarding, ServicoOnboarding>();
        // Dados falsos de desenvolvimento. O controller que o expoe checa o ambiente em tempo
        // de execucao — ver DevController.
        servicos.AddScoped<IServicoSemente, ServicoSemente>();

        // Interfaces de dados do Core -> implementacao SQL/EF. E o que permite o protocolo de
        // envio viver no Core sem enxergar o DbContext.
        servicos.AddScoped<IDadosMensagem, DadosMensagem>();

        // Camada de tempo. O motor mora no Core (regra pura); o SQL da elegibilidade, na Infra.
        servicos.AddScoped<IDadosFollowUp, DadosFollowUp>();
        servicos.AddScoped<MotorFollowUp>();

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
        fonte.MapEnum<OrigemLead>("origem_lead_enum");
        fonte.MapEnum<DirecaoMensagem>("direcao_mensagem_enum");
        fonte.MapEnum<TipoMidia>("tipo_midia_enum");
        fonte.MapEnum<StatusConversa>("status_conversa_enum");
        fonte.MapEnum<StatusLembrete>("status_lembrete_enum");
        fonte.MapEnum<OrigemLembrete>("origem_lembrete_enum");
        fonte.MapEnum<AbrangenciaFeriado>("abrangencia_feriado_enum");
    }

    /// <summary>TimeProvider.System como padrao; os testes registram um relogio falso antes.</summary>
    private static void TryAddTimeProviderPadrao(this IServiceCollection servicos)
    {
        if (servicos.Any(d => d.ServiceType == typeof(TimeProvider))) return;
        servicos.AddSingleton(TimeProvider.System);
    }
}
