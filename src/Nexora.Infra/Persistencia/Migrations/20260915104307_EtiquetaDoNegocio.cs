using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class EtiquetaDoNegocio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "negociacoes_etiquetas",
                columns: table => new
                {
                    negociacao_id = table.Column<long>(type: "bigint", nullable: false),
                    etiqueta_id = table.Column<long>(type: "bigint", nullable: false),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    criado_por = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_negociacoes_etiquetas", x => new { x.negociacao_id, x.etiqueta_id });
                    table.ForeignKey(
                        name: "FK_negociacoes_etiquetas_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_negociacoes_etiquetas_criado_por",
                        columns: x => new { x.criado_por, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_negociacoes_etiquetas_etiqueta",
                        columns: x => new { x.etiqueta_id, x.empresa_id },
                        principalTable: "etiquetas",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_negociacoes_etiquetas_negociacao",
                        columns: x => new { x.negociacao_id, x.empresa_id },
                        principalTable: "negociacoes",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_negociacoes_etiquetas_etiqueta",
                table: "negociacoes_etiquetas",
                columns: new[] { "empresa_id", "etiqueta_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "negociacoes_etiquetas");
        }
    }
}
