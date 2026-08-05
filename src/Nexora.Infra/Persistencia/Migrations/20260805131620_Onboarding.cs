using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Onboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "equipe_dispensada_em",
                table: "empresas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "onboarding_dispensado_em",
                table: "empresas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "primeira_mensagem_em",
                table: "empresas",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "equipe_dispensada_em",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "onboarding_dispensado_em",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "primeira_mensagem_em",
                table: "empresas");
        }
    }
}
