using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ImportacaoEmSegundoPlano : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "avisar_integracoes",
                table: "importacoes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "pipeline_id",
                table: "importacoes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "processando_desde",
                table: "importacoes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "responsavel_id",
                table: "importacoes",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "avisar_integracoes",
                table: "importacoes");

            migrationBuilder.DropColumn(
                name: "pipeline_id",
                table: "importacoes");

            migrationBuilder.DropColumn(
                name: "processando_desde",
                table: "importacoes");

            migrationBuilder.DropColumn(
                name: "responsavel_id",
                table: "importacoes");
        }
    }
}
