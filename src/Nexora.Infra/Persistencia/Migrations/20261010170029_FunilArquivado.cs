using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class FunilArquivado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_pipelines_empresa_nome",
                table: "pipelines");

            migrationBuilder.AddColumn<DateTime>(
                name: "arquivado_em",
                table: "pipelines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "uq_pipelines_empresa_nome",
                table: "pipelines",
                columns: new[] { "empresa_id", "nome" },
                unique: true,
                filter: "arquivado_em IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_pipelines_empresa_nome",
                table: "pipelines");

            migrationBuilder.DropColumn(
                name: "arquivado_em",
                table: "pipelines");

            migrationBuilder.CreateIndex(
                name: "uq_pipelines_empresa_nome",
                table: "pipelines",
                columns: new[] { "empresa_id", "nome" },
                unique: true);
        }
    }
}
