using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class EnvioConfiavel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_msg_pendentes",
                table: "mensagens");

            migrationBuilder.AddColumn<DateTime>(
                name: "expirada_em",
                table: "mensagens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "tentativas",
                table: "mensagens",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.CreateIndex(
                name: "ix_msg_pendentes",
                table: "mensagens",
                columns: new[] { "empresa_id", "data_disparo" },
                filter: "enviada_em IS NULL AND expirada_em IS NULL AND direcao = 'saida'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_msg_pendentes",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "expirada_em",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "tentativas",
                table: "mensagens");

            migrationBuilder.CreateIndex(
                name: "ix_msg_pendentes",
                table: "mensagens",
                columns: new[] { "empresa_id", "data_disparo" },
                filter: "enviada_em IS NULL AND direcao = 'saida'");
        }
    }
}
