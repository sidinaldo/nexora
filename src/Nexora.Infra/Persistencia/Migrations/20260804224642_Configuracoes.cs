using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Configuracoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feriados_ignorados",
                columns: table => new
                {
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    feriado_id = table.Column<long>(type: "bigint", nullable: false),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feriados_ignorados", x => new { x.empresa_id, x.feriado_id });
                    table.ForeignKey(
                        name: "FK_feriados_ignorados_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_feriados_ignorados_feriados_feriado_id",
                        column: x => x.feriado_id,
                        principalTable: "feriados",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feriados_ignorados");
        }
    }
}
