using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ContatosEtiquetas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contatos_etiquetas",
                columns: table => new
                {
                    contato_id = table.Column<long>(type: "bigint", nullable: false),
                    etiqueta_id = table.Column<long>(type: "bigint", nullable: false),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    criado_por = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contatos_etiquetas", x => new { x.contato_id, x.etiqueta_id });
                    table.ForeignKey(
                        name: "FK_contatos_etiquetas_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contatos_etiquetas_contato",
                        columns: x => new { x.contato_id, x.empresa_id },
                        principalTable: "contatos",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_contatos_etiquetas_criado_por",
                        columns: x => new { x.criado_por, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contatos_etiquetas_etiqueta",
                        columns: x => new { x.etiqueta_id, x.empresa_id },
                        principalTable: "etiquetas",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contatos_etiquetas_etiqueta",
                table: "contatos_etiquetas",
                columns: new[] { "empresa_id", "etiqueta_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contatos_etiquetas");

        }
    }
}
