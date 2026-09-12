using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Etiquetas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "etiquetas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    nome = table.Column<string>(type: "text", nullable: false),
                    cor = table.Column<string>(type: "text", nullable: false, defaultValue: "#2F5D3A"),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_etiquetas", x => x.id);
                    table.UniqueConstraint("uq_etiquetas_id_empresa", x => new { x.id, x.empresa_id });
                    table.ForeignKey(
                        name: "FK_etiquetas_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // ===================== O UNICO DE NOME E FUNCIONAL, E POR ISSO E SQL CRU =====
            // (empresa_id, lower(nome)): "VIP" e "vip" na mesma empresa sao a MESMA etiqueta.
            // Sem isso a lista vira duas entradas que parecem uma, e o filtro por etiqueta que
            // vem na issue #3 passa a achar so metade de cada busca.
            //
            // ⚠️ Vai em `Sql` porque o fluent API do EF NAO expressa indice sobre expressao.
            // Mesmo padrao do uq_usuarios_email, criado assim no bloco inicial — e a mesma
            // consequencia: indice funcional NAO entra no ModelSnapshot, entao nenhuma migration
            // futura o recria sozinha.
            //
            // ⚠️ O `;` NO FIM NAO E ENFEITE. `dotnet ef migrations script --idempotent` embrulha
            // cada `Sql` num `IF NOT EXISTS(...) THEN <sql> END IF;` — sem o ponto e virgula o
            // bloco fica sintaticamente invalido e o script morre no meio, SO em producao. Ja
            // aconteceu neste repositorio.
            // ============================================================================
            migrationBuilder.Sql(
                @"CREATE UNIQUE INDEX uq_etiquetas_nome ON etiquetas (empresa_id, lower(nome));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Ordem inversa do Up. O indice cai junto com a tabela, mas o DROP explicito
            // mantem o Down correto se alguem reordenar as operacoes um dia.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS uq_etiquetas_nome;");

            migrationBuilder.DropTable(
                name: "etiquetas");
        }
    }
}
