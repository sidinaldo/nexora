using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Nexora.Core;

namespace Nexora.Infra.Persistencia;

/// <summary>Usada SO pelas ferramentas de linha de comando (dotnet ef migrations/database).
/// Nunca participa do runtime.
///
/// Existe para que `dotnet ef migrations add` funcione sem exigir que a Program da Api suba —
/// ela, de proposito, derruba a aplicacao quando falta connection string ou chave JWT.
///
/// ===================== POR QUE NAO HA CONNECTION STRING PADRAO =====================
/// Havia. Apontava para um banco chamado `nexora`, enquanto o de desenvolvimento e outro
/// (definido em user-secrets, normalmente `nexora_dev`). O resultado: quem rodasse
/// `dotnet ef database update` sem definir NEXORA_CONN criava um banco VAZIO com o schema
/// aplicado, sem erro nenhum, e ficava se perguntando por que a aplicacao nao via as tabelas
/// novas. Aconteceu neste projeto, e sobrou um banco orfao no ambiente.
///
/// Default silencioso que aponta para o banco errado e PIOR que erro: o erro voce conserta em
/// dez segundos; o banco errado voce descobre depois de meia hora de depuracao. Agora falha
/// alto, com o comando exato na mensagem.
/// ==================================================================================</summary>
public class FabricaDbContextDesignTime : IDesignTimeDbContextFactory<NexoraDbContext>
{
    public NexoraDbContext CreateDbContext(string[] args)
    {
        var conexao = Environment.GetEnvironmentVariable("NEXORA_CONN");

        if (string.IsNullOrWhiteSpace(conexao))
            throw new InvalidOperationException(
                """
                NEXORA_CONN nao esta definida.

                As ferramentas do EF (migrations / database update) precisam saber CONTRA QUAL
                BANCO trabalhar, e nao ha padrao de proposito: um padrao errado cria banco vazio
                em silencio.

                Use a MESMA string do user-secrets do projeto. Para consultar:
                    dotnet user-secrets list --project src/Nexora.Api

                PowerShell:
                    $env:NEXORA_CONN = "Host=localhost;Port=5432;Database=nexora_dev;Username=postgres;Password=..."

                bash:
                    export NEXORA_CONN='Host=localhost;Port=5432;Database=nexora_dev;Username=postgres;Password=...'

                Depois:
                    dotnet dotnet-ef database update --project src/Nexora.Infra --startup-project src/Nexora.Api
                """);

        // ARMADILHA (custou uma rodada de depuracao): aqui NAO se chama MapEnum.
        //
        // O NpgsqlDataSource resolve o OID de cada enum mapeado ao abrir a conexao. Num banco
        // vazio o tipo ainda nao existe — e quem o cria e justamente a migration que estamos
        // tentando aplicar. Com MapEnum, `dotnet ef database update` morre com
        // 42704: tipo "papel_usuario_enum" nao existe, antes de rodar qualquer DDL.
        //
        // Migrations nao leem dados, so emitem DDL: nao precisam do mapeamento. Quem precisa
        // e o RUNTIME, e la o mapeamento existe (ver ServicosInfra.AdicionarInfra).
        var opcoes = new DbContextOptionsBuilder<NexoraDbContext>()
            .UseNpgsql(conexao)
            .Options;

        return new NexoraDbContext(opcoes, new ContextoSemTenant());
    }

    /// <summary>Contexto sem tenant (EmpresaId = 0), como um job de fundo. As migrations nao
    /// leem dados, entao o query filter nunca chega a ser avaliado aqui.</summary>
    private sealed class ContextoSemTenant : IContextoEmpresa
    {
        public long EmpresaId => 0;
        public long UsuarioId => 0;
        public string? Papel => null;
        public bool EstaAutenticado => false;
    }
}
