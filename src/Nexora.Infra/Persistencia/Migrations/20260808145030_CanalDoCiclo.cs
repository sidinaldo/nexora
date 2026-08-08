using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class CanalDoCiclo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "canal_id",
                table: "vendas",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "canal_ciclo_id",
                table: "conversas",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_vendas_canal",
                table: "vendas",
                columns: new[] { "empresa_id", "canal_id", "fechada_em" },
                filter: "canal_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_conversas_canais_captacao_canal_ciclo_id",
                table: "conversas",
                column: "canal_ciclo_id",
                principalTable: "canais_captacao",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_vendas_canais_captacao_canal_id",
                table: "vendas",
                column: "canal_id",
                principalTable: "canais_captacao",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conversas_canais_captacao_canal_ciclo_id",
                table: "conversas");

            migrationBuilder.DropForeignKey(
                name: "FK_vendas_canais_captacao_canal_id",
                table: "vendas");

            migrationBuilder.DropIndex(
                name: "ix_vendas_canal",
                table: "vendas");

            migrationBuilder.DropColumn(
                name: "canal_id",
                table: "vendas");

            migrationBuilder.DropColumn(
                name: "canal_ciclo_id",
                table: "conversas");
        }
    }
}
