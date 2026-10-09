using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>O template de cada automacao (INT-XX): follow-up, lembrete e pesquisa. E o que sai
    /// pela API oficial quando a janela de 24h fechou; nulo = a automatica nao sai, com o motivo.
    ///
    /// FK simples com SET NULL: apagar o template so desfaz a escolha.</summary>
    public partial class ModelosDasAutomacoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "modelo_follow_up_id",
                table: "empresas",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "modelo_lembrete_id",
                table: "empresas",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "modelo_nps_id",
                table: "empresas",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_empresas_modelo_follow_up",
                table: "empresas",
                column: "modelo_follow_up_id",
                principalTable: "modelos_mensagem",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_empresas_modelo_lembrete",
                table: "empresas",
                column: "modelo_lembrete_id",
                principalTable: "modelos_mensagem",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_empresas_modelo_nps",
                table: "empresas",
                column: "modelo_nps_id",
                principalTable: "modelos_mensagem",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_empresas_modelo_follow_up",
                table: "empresas");

            migrationBuilder.DropForeignKey(
                name: "fk_empresas_modelo_lembrete",
                table: "empresas");

            migrationBuilder.DropForeignKey(
                name: "fk_empresas_modelo_nps",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "modelo_follow_up_id",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "modelo_lembrete_id",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "modelo_nps_id",
                table: "empresas");
        }
    }
}
