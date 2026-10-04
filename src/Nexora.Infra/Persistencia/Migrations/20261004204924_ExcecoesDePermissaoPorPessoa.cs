using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ExcecoesDePermissaoPorPessoa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usuarios_permissoes",
                columns: table => new
                {
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    usuario_id = table.Column<long>(type: "bigint", nullable: false),
                    permissao = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    concedida = table.Column<bool>(type: "boolean", nullable: false),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    criado_por = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usuarios_permissoes", x => new { x.empresa_id, x.usuario_id, x.permissao });
                    table.ForeignKey(
                        name: "FK_usuarios_permissoes_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_usuarios_permissoes_usuario",
                        columns: x => new { x.usuario_id, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usuarios_permissoes");
        }
    }
}
