using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class MultiConexao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_conexoes_empresa",
                table: "conexoes");

            migrationBuilder.AddColumn<short>(
                name: "limite_conexoes",
                table: "empresas",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.AddCheckConstraint(
                name: "ck_empresas_limite_conexoes",
                table: "empresas",
                sql: "limite_conexoes BETWEEN 1 AND 20");

            migrationBuilder.CreateIndex(
                name: "ix_conexoes_empresa",
                table: "conexoes",
                column: "empresa_id");

            migrationBuilder.CreateIndex(
                name: "uq_conexoes_empresa_nome",
                table: "conexoes",
                columns: new[] { "empresa_id", "nome" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_empresas_limite_conexoes",
                table: "empresas");

            migrationBuilder.DropIndex(
                name: "ix_conexoes_empresa",
                table: "conexoes");

            migrationBuilder.DropIndex(
                name: "uq_conexoes_empresa_nome",
                table: "conexoes");

            migrationBuilder.DropColumn(
                name: "limite_conexoes",
                table: "empresas");

            migrationBuilder.CreateIndex(
                name: "uq_conexoes_empresa",
                table: "conexoes",
                column: "empresa_id",
                unique: true);
        }
    }
}
