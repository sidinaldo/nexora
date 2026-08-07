using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Auditoria;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>Contexto de tenant MUTAVEL, so para teste. Serve para trocar de empresa sem
/// trocar de DbContext — o que importa porque o teste roda tudo numa transacao unica: um
/// segundo DbContext usaria outra conexao e nao enxergaria as linhas ainda nao commitadas.
///
/// Funciona porque o EF avalia `_contexto.EmpresaId` na TRADUCAO da consulta (vira
/// parametro), nao na construcao do modelo.</summary>
public sealed class ContextoMutavel : IContextoEmpresa
{
    public long EmpresaId { get; set; }
    public long UsuarioId { get; set; }
    public string? Papel { get; set; }
    public bool EstaAutenticado => EmpresaId != 0;
}

/// <summary>Fixture compartilhada dos testes de INTEGRACAO. Roda contra um Postgres REAL
/// (nao provider in-memory): query filter global, indice parcial, indice funcional em
/// lower(email) e check constraint simplesmente nao existem em memoria — testar la daria
/// verde sem provar nada.
///
/// Diferente do Recupera, esta fixture se provisiona sozinha: cria o banco de teste se nao
/// existir e aplica as MIGRATIONS. Nao ha passo manual de "aplique o .sql antes de rodar".
///
/// Cada teste roda numa transacao SEMPRE revertida — o banco nao e sujado e os testes nao
/// interferem entre si. Sobrescreva a string com NEXORA_TESTE_CONN.</summary>
public sealed class BancoTeste : IDisposable
{
    public NpgsqlDataSource Fonte { get; }

    public BancoTeste()
    {
        var conexao = Environment.GetEnvironmentVariable("NEXORA_TESTE_CONN")
            ?? "Host=localhost;Port=5432;Database=nexora_teste;Username=postgres;Password=admin";

        GarantirBanco(conexao);
        AplicarMigrations(conexao);

        // So DEPOIS das migrations o data source pode mapear os enums: o Npgsql resolve o OID
        // de cada enum ao abrir a conexao, e num banco vazio o tipo ainda nao existe.
        //
        // A lista vem do ServicosInfra, a MESMA da producao. Duas listas divergiriam no dia em
        // que alguem adicionasse um enum, e o sintoma seria um teste verde contra um banco
        // configurado errado.
        var fonte = new NpgsqlDataSourceBuilder(conexao);
        Nexora.Infra.ServicosInfra.MapearEnums(fonte);
        Fonte = fonte.Build();
    }

    /// <summary>Cria o banco de teste se ele nao existir (conectando na base `postgres`).</summary>
    private static void GarantirBanco(string conexao)
    {
        var alvo = new NpgsqlConnectionStringBuilder(conexao);
        var nome = alvo.Database
            ?? throw new InvalidOperationException("A connection string de teste precisa de Database.");

        var admin = new NpgsqlConnectionStringBuilder(conexao) { Database = "postgres" };
        using var conn = new NpgsqlConnection(admin.ConnectionString);
        conn.Open();

        using var existe = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn);
        existe.Parameters.AddWithValue("n", nome);
        if (existe.ExecuteScalar() is not null) return;

        // Nome de banco nao e parametrizavel em CREATE DATABASE; vem da propria connection
        // string do desenvolvedor, e as aspas duplas evitam surpresa com maiuscula.
        using var criar = new NpgsqlCommand($"CREATE DATABASE \"{nome}\"", conn);
        criar.ExecuteNonQuery();
    }

    /// <summary>Aplica as migrations com um contexto SEM mapeamento de enum — o mesmo motivo
    /// da FabricaDbContextDesignTime: mapear enum exige que o tipo ja exista, e e a migration
    /// que o cria.</summary>
    private static void AplicarMigrations(string conexao)
    {
        var opcoes = new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(conexao).Options;
        using var db = new NexoraDbContext(opcoes, new ContextoMutavel());
        db.Database.Migrate();
    }

    /// <summary>Um DbContext novo, com o MESMO wiring da producao (incluindo o interceptor de
    /// auditoria). Passe o relogio quando o teste precisar controlar o tempo.</summary>
    /// <summary>O contexto de teste com os MESMOS interceptores da producao.
    ///
    /// O `coletor` (AUD-1) tem que ser a MESMA instancia que os servicos recebem — e o elo entre
    /// "o servico declarou a acao" e "o interceptor gravou a linha". Contextos diferentes com
    /// coletores diferentes fariam a trilha nascer vazia, e o teste passaria a medir nada.</summary>
    public NexoraDbContext NovoContexto(
        IContextoEmpresa contexto, TimeProvider? relogio = null, ColetorAuditoria? coletor = null)
    {
        var tempo = relogio ?? TimeProvider.System;

        var opcoes = new DbContextOptionsBuilder<NexoraDbContext>()
            .UseNpgsql(Fonte)
            .AddInterceptors(
                new InterceptorAuditoria(tempo),
                new InterceptorTrilha(coletor ?? new ColetorAuditoria(), contexto, tempo))
            .Options;

        return new NexoraDbContext(opcoes, contexto);
    }

    public void Dispose() => Fonte.Dispose();
}

/// <summary>Compartilha uma unica BancoTeste (e um unico data source) entre as classes de
/// teste de integracao — evita reconstruir o pool de conexoes por classe.</summary>
[CollectionDefinition("banco")]
public sealed class BancoCollection : ICollectionFixture<BancoTeste>;
