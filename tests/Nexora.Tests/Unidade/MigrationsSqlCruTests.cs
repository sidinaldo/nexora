using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Unidade;

/// <summary>===================== SQL CRU DE MIGRATION PRECISA DE PONTO E VÍRGULA =====================
///
/// Há DOIS caminhos para aplicar uma migration neste projeto, e eles tratam o SQL cru de formas
/// diferentes:
///
///   • `dotnet ef database update` / `Migrate()` — o caminho de desenvolvimento, e o que a suíte de
///     testes usa. Cada `migrationBuilder.Sql(...)` vai ao banco como um comando próprio;
///
///   • o SCRIPT IDEMPOTENTE — o que o `deploy/migrate.sh` gera e roda em PRODUÇÃO. Aqui o EF cola o
///     texto dentro de um bloco `DO $EF$ BEGIN IF NOT EXISTS(...) THEN ... END IF; END $EF$;`.
///
/// No segundo, SQL sem `;` no fim não compila: o `END IF` cola na última instrução e o PL/pgSQL
/// recusa o bloco inteiro.
///
/// ⚠️ E O MODO DE FALHA É O PIOR POSSÍVEL: a suíte inteira fica verde — ela usa o primeiro caminho —
/// e o DEPLOY quebra. Aconteceu no OPE-1, com um `CREATE UNIQUE INDEX` sem ponto e vírgula, e só
/// apareceu num ensaio manual da migration contra uma cópia restaurada do backup.
///
/// Este teste lê as operações que cada migration declara, sem tocar no banco.
/// ==========================================================================================</summary>
public class MigrationsSqlCruTests
{
    [Fact]
    public void TODO_SQL_CRU_DE_MIGRATION_TERMINA_COM_PONTO_E_VIRGULA()
    {
        var faltando = new List<string>();

        foreach (var tipo in typeof(NexoraDbContext).Assembly.GetTypes()
                     .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var migration = (Migration)Activator.CreateInstance(tipo)!;

            foreach (var operacao in migration.UpOperations.Concat(migration.DownOperations))
            {
                if (operacao is not SqlOperation sql) continue;

                var texto = sql.Sql.TrimEnd();
                if (texto.EndsWith(';')) continue;

                faltando.Add($"{tipo.Name}: {Resumo(texto)}");
            }
        }

        Assert.True(faltando.Count == 0,
            "SQL cru de migration SEM `;` no fim. Funciona no `Migrate()` (que é o que os testes "
            + "usam) e QUEBRA o script idempotente do deploy:\n  " + string.Join("\n  ", faltando));
    }

    /// <summary>A última linha do comando, que é onde o `;` faltaria — o SQL inteiro tornaria a
    /// mensagem de falha ilegível.</summary>
    private static string Resumo(string sql)
    {
        var linhas = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var ultima = linhas[^1].Trim();
        return ultima.Length <= 90 ? ultima : ultima[..90] + "…";
    }
}
