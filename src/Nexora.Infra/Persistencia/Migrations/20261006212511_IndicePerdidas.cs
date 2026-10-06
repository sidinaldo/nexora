using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class IndicePerdidas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_negociacoes_perdidas",
                table: "negociacoes",
                columns: new[] { "empresa_id", "perdida_em" },
                filter: "status = 'perdida'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_negociacoes_perdidas",
                table: "negociacoes");
        }
    }
}
