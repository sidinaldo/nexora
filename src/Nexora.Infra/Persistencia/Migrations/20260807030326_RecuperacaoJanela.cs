using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class RecuperacaoJanela : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "recuperada_em",
                table: "mensagens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_msg_recuperada",
                table: "mensagens",
                columns: new[] { "empresa_id", "recuperada_em" },
                filter: "recuperada_em IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_msg_recuperada",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "recuperada_em",
                table: "mensagens");
        }
    }
}
