using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class TrilhaAuditoria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auditoria",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    entidade = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    entidade_id = table.Column<long>(type: "bigint", nullable: false),
                    acao = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    alteracoes = table.Column<string>(type: "jsonb", nullable: false),
                    usuario_id = table.Column<long>(type: "bigint", nullable: true),
                    ator = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    quando = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auditoria", x => x.id);
                    table.ForeignKey(
                        name: "FK_auditoria_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_auditoria_usuarios_usuario_id",
                        column: x => x.usuario_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_empresa",
                table: "auditoria",
                columns: new[] { "empresa_id", "quando" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_registro",
                table: "auditoria",
                columns: new[] { "empresa_id", "entidade", "entidade_id", "quando" },
                descending: new[] { false, false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auditoria");
        }
    }
}
