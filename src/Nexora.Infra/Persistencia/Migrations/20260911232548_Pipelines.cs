using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Pipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_etapas_ganho",
                table: "etapas_funil");

            migrationBuilder.DropIndex(
                name: "uq_etapas_ordem",
                table: "etapas_funil");

            migrationBuilder.AddColumn<long>(
                name: "pipeline_id",
                table: "etapas_funil",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "pipelines",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    nome = table.Column<string>(type: "text", nullable: false),
                    cor = table.Column<string>(type: "text", nullable: false, defaultValue: "#2F5D3A"),
                    ordem = table.Column<short>(type: "smallint", nullable: false),
                    padrao = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pipelines", x => x.id);
                    table.UniqueConstraint("uq_pipelines_id_empresa", x => new { x.id, x.empresa_id });
                    table.ForeignKey(
                        name: "FK_pipelines_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "uq_etapas_ganho",
                table: "etapas_funil",
                columns: new[] { "empresa_id", "pipeline_id" },
                unique: true,
                filter: "e_ganho");

            migrationBuilder.CreateIndex(
                name: "uq_etapas_ordem",
                table: "etapas_funil",
                columns: new[] { "empresa_id", "pipeline_id", "ordem" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_pipelines_empresa_nome",
                table: "pipelines",
                columns: new[] { "empresa_id", "nome" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_pipelines_padrao",
                table: "pipelines",
                column: "empresa_id",
                unique: true,
                filter: "padrao");

            // ===================== O BACKFILL, E POR QUE ELE NAO E OPCIONAL =====================
            // `pipeline_id` nasceu NOT NULL com default 0, e 0 nao existe em `pipelines`. Sem as
            // duas instrucoes abaixo, o `AddForeignKey` logo em seguida estoura em QUALQUER banco
            // que ja tenha uma etapa — ou seja, em todos, menos num vazio.
            //
            // Toda empresa que tem etapa ganha UMA pipeline chamada "Vendas", marcada como padrao,
            // e todas as etapas dela passam a apontar para essa. O funil de quem ja usa o produto
            // continua exatamente como estava: mesmas etapas, mesma ordem, mesma etapa de ganho.
            // O que mudou e de quem elas penduram.
            //
            // `WHERE NOT EXISTS`: as duas sao IDEMPOTENTES. Rodar de novo numa base ja migrada —
            // o que acontece em restauracao de backup — nao cria uma segunda pipeline nem repoe
            // ponteiro nenhum.
            //
            // ⚠️ O `;` NO FIM NAO E ENFEITE. `dotnet ef migrations script --idempotent` embrulha
            // cada `Sql` num `IF NOT EXISTS(...) THEN <sql> END IF;` — sem o ponto e virgula o
            // bloco fica sintaticamente invalido e o script morre no meio, SO em producao. Ja
            // aconteceu neste repositorio (commit 245886f).
            // ====================================================================================
            migrationBuilder.Sql("""
                INSERT INTO pipelines (empresa_id, nome, cor, ordem, padrao, criado_em, atualizado_em)
                SELECT DISTINCT e.empresa_id, 'Vendas', '#2F5D3A', 1, true, now(), now()
                  FROM etapas_funil e
                 WHERE NOT EXISTS (
                           SELECT 1 FROM pipelines p WHERE p.empresa_id = e.empresa_id);
                """);

            migrationBuilder.Sql("""
                UPDATE etapas_funil e
                   SET pipeline_id = p.id
                  FROM pipelines p
                 WHERE p.empresa_id = e.empresa_id
                   AND p.padrao
                   AND e.pipeline_id = 0;
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_etapas_pipeline",
                table: "etapas_funil",
                columns: new[] { "pipeline_id", "empresa_id" },
                principalTable: "pipelines",
                principalColumns: new[] { "id", "empresa_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_etapas_pipeline",
                table: "etapas_funil");

            migrationBuilder.DropTable(
                name: "pipelines");

            migrationBuilder.DropIndex(
                name: "uq_etapas_ganho",
                table: "etapas_funil");

            migrationBuilder.DropIndex(
                name: "uq_etapas_ordem",
                table: "etapas_funil");

            migrationBuilder.DropColumn(
                name: "pipeline_id",
                table: "etapas_funil");

            migrationBuilder.CreateIndex(
                name: "uq_etapas_ganho",
                table: "etapas_funil",
                column: "empresa_id",
                unique: true,
                filter: "e_ganho");

            migrationBuilder.CreateIndex(
                name: "uq_etapas_ordem",
                table: "etapas_funil",
                columns: new[] { "empresa_id", "ordem" },
                unique: true);
        }
    }
}
