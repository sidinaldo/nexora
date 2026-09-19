using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class EntregaEmMassa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_entregas_fila",
                table: "entregas_webhook");

            migrationBuilder.AddColumn<bool>(
                name: "em_massa",
                table: "entregas_webhook",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_entregas_fila",
                table: "entregas_webhook",
                columns: new[] { "em_massa", "proxima_tentativa_em" },
                filter: "status = 'pendente'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_entregas_fila",
                table: "entregas_webhook");

            migrationBuilder.DropColumn(
                name: "em_massa",
                table: "entregas_webhook");

            migrationBuilder.CreateIndex(
                name: "ix_entregas_fila",
                table: "entregas_webhook",
                column: "proxima_tentativa_em",
                filter: "status = 'pendente'");
        }
    }
}
