using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Nexora.Core.Entidades;

namespace Nexora.Tests.Integracao;

/// <summary>AS DUAS LISTAS DE ENUM NATIVO TÊM DE BATER.
///
/// Todo enum do Postgres aparece em DOIS lugares: `HasPostgresEnum` no `NexoraDbContext` ensina a
/// MIGRAÇÃO a criar o tipo, e `ServicosInfra.MapearEnums` ensina o DRIVER a ler e escrever o
/// valor. O comentário de `MapearEnums` já avisava que as duas listas precisam bater.
///
/// ⚠️ E ELAS DIVERGIRAM, achado em revisão. O schema do INT-XX declarou `StatusImportacao` e
/// `ResultadoLinha` no DbContext e esqueceu o `MapearEnums`. A migração criou os tipos, o build
/// passou, os 964 testes passaram — porque nenhum escrevia em `importacoes` ainda. A primeira
/// escrita de verdade teria estourado com "column status is of type status_importacao_enum but
/// expression is of type integer".
///
/// Este teste percorre TODOS os enums que o modelo declara e faz cada um atravessar o driver de
/// verdade. Não é sobre os dois de agora: é sobre o próximo que alguém acrescentar.</summary>
[Collection("banco")]
public class EnumsNativosDbTests(BancoTeste banco)
{
    [Fact]
    public async Task TODO_ENUM_DO_MODELO_ATRAVESSA_O_DRIVER()
    {
        using var db = banco.NovoContexto(new ContextoMutavel());

        // ⚠️ A LISTA DO `HasPostgresEnum`, e não "todo enum do modelo". A primeira versão deste
        // teste varria toda propriedade enum e tropeçou em `AcaoAuditoria` — que é enum no C#, mas
        // a trilha grava o NOME em texto (ver `AcaoAuditoria`), não como tipo nativo. O que precisa
        // bater com `MapearEnums` é exatamente o que o modelo DECLARA ao Postgres.
        //
        // ⚠️ DO MODELO DE DESIGN, e não de `db.Model`. A anotação do enum nativo é de MIGRAÇÃO, e o
        // EF a remove do modelo de execução — `db.Model.GetPostgresEnums()` volta vazio. A guarda
        // logo abaixo é o que mostrou isso: sem ela o laço rodaria zero vezes e o teste passaria
        // sem conferir nada, que é pior que não ter teste.
        var nativos = db.GetService<IDesignTimeModel>().Model
            .GetPostgresEnums().Select(e => e.Name).ToList();

        var enumsDoCore = typeof(OrigemLead).Assembly.GetTypes().Where(t => t.IsEnum).ToList();

        // Sem isto o teste passaria vazio no dia em que a consulta acima parasse de achar enum.
        Assert.Contains("status_importacao_enum", nativos);
        Assert.Contains("resultado_linha_enum", nativos);
        Assert.Contains("origem_lead_enum", nativos);

        foreach (var nomeNoBanco in nativos)
        {
            // A convenção do projeto inteiro: `StatusImportacao` ↔ `status_importacao_enum`.
            var tipo = enumsDoCore.SingleOrDefault(t =>
                JsonNamingPolicy.SnakeCaseLower.ConvertName(t.Name) + "_enum" == nomeNoBanco);

            Assert.True(tipo is not null,
                $"{nomeNoBanco} é declarado no modelo mas não há enum no C# com o nome correspondente");

            foreach (var valor in Enum.GetValues(tipo!))
            {
                // ⚠️ PELO DRIVER, com o valor TIPADO como o enum do C#. Sem o `MapEnum`, o Npgsql
                // não sabe mandá-lo como o tipo nativo — e o erro é exatamente o que a primeira
                // escrita em produção teria dado.
                await using var cmd = banco.Fonte.CreateCommand($"SELECT CAST($1 AS text)");
                cmd.Parameters.Add(new Npgsql.NpgsqlParameter { Value = valor, DataTypeName = nomeNoBanco });

                string? rotulo = null;
                var erro = await Record.ExceptionAsync(async () =>
                    rotulo = (string?)await cmd.ExecuteScalarAsync());

                Assert.True(erro is null,
                    $"{tipo!.Name}.{valor} não atravessa o driver como {nomeNoBanco} — falta em " +
                    $"`ServicosInfra.MapearEnums`? ({erro?.GetType().Name}: {erro?.Message})");

                // E o rótulo é o que o resto do sistema espera ler: snake_case do nome.
                Assert.Equal(JsonNamingPolicy.SnakeCaseLower.ConvertName(valor.ToString()!), rotulo);
            }
        }
    }
}
