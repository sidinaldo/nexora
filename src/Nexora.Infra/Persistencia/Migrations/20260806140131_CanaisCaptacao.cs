using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class CanaisCaptacao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "canais_captacao",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    nome = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    codigo = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    conexao_id = table.Column<long>(type: "bigint", nullable: false),
                    origem = table.Column<int>(type: "origem_lead_enum", nullable: false),
                    ativo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    leads_recebidos = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canais_captacao", x => x.id);
                    table.ForeignKey(
                        name: "FK_canais_captacao_conexoes_conexao_id_empresa_id",
                        columns: x => new { x.conexao_id, x.empresa_id },
                        principalTable: "conexoes",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_canais_captacao_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "uq_canais_empresa_codigo",
                table: "canais_captacao",
                columns: new[] { "empresa_id", "codigo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_canais_empresa_nome",
                table: "canais_captacao",
                columns: new[] { "empresa_id", "nome" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "canais_captacao");
        }
    }
}
