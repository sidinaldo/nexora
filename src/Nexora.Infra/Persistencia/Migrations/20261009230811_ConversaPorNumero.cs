using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>CONV-XX: uma conversa por número. Os dados atuais já cabem no índice novo (cada
    /// contato tem uma conversa só), então não há transformação de dados.
    ///
    /// ⚠️ O `Down` FALHA se já existir contato com duas conversas: voltar exige antes juntar ou
    /// apagar as conversas a mais.</summary>
    public partial class ConversaPorNumero : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_conversas_contato",
                table: "conversas");

            migrationBuilder.CreateIndex(
                name: "uq_conversas_contato_conexao",
                table: "conversas",
                columns: new[] { "contato_id", "conexao_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_conversas_contato_conexao",
                table: "conversas");

            migrationBuilder.CreateIndex(
                name: "uq_conversas_contato",
                table: "conversas",
                column: "contato_id",
                unique: true);
        }
    }
}
