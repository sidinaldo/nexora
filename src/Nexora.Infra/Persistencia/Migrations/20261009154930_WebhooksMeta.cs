using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>A fila de entrada do webhook da Cloud API (INT-XX): cada mudanca aceita fica aqui ate a
    /// rodada em segundo plano processar. Ver `WebhookMetaRecebido`.</summary>
    public partial class WebhooksMeta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhooks_meta_recebidos",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    conexao_id = table.Column<long>(type: "bigint", nullable: false),
                    campo = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    recebido_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processando_desde = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    processado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tentativas = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    erro = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhooks_meta_recebidos", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhooks_meta_recebidos_conexoes_conexao_id",
                        column: x => x.conexao_id,
                        principalTable: "conexoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_webhooks_meta_recebidos_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_webhooks_meta_pendentes",
                table: "webhooks_meta_recebidos",
                column: "id",
                filter: "processado_em IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhooks_meta_recebidos");
        }
    }
}
