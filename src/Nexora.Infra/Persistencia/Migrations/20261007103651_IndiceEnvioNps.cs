using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class IndiceEnvioNps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_pesquisas_nps_envio",
                table: "pesquisas_nps",
                columns: new[] { "empresa_id", "data_envio" },
                filter: "data_envio IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pesquisas_nps_envio",
                table: "pesquisas_nps");
        }
    }
}
