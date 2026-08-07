using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class HistoricoVendas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vendas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    contato_id = table.Column<long>(type: "bigint", nullable: false),
                    valor = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    fechada_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    responsavel_id = table.Column<long>(type: "bigint", nullable: true),
                    observacao = table.Column<string>(type: "text", nullable: true),
                    etapa_id = table.Column<long>(type: "bigint", nullable: false),
                    cancelada_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelada_por = table.Column<long>(type: "bigint", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendas", x => x.id);
                    table.CheckConstraint("ck_vendas_valor", "valor > 0");
                    table.ForeignKey(
                        name: "FK_vendas_contatos_contato_id",
                        column: x => x.contato_id,
                        principalTable: "contatos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vendas_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vendas_usuarios_responsavel_id",
                        column: x => x.responsavel_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vendas_contato",
                table: "vendas",
                columns: new[] { "empresa_id", "contato_id", "fechada_em" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_vendas_periodo",
                table: "vendas",
                columns: new[] { "empresa_id", "fechada_em" },
                descending: new[] { false, true },
                filter: "cancelada_em IS NULL");

            // ===================== O BACKFILL, E POR QUE ELE NÃO É OPCIONAL =====================
            // Sem isto o histórico começa vazio e **o dashboard de todo cliente zera no dia do
            // deploy**: as vendas passam a ser contadas por `vendas`, e a tabela estaria vazia.
            // Toda venda já fechada viraria invisível de uma vez.
            //
            // Uma linha por contato com `ganho_em` preenchido, com o valor e o responsável atuais.
            //
            // `COALESCE(valor, 0.01)`: existe ganho antigo sem valor — o campo era opcional antes.
            // O CHECK `ck_vendas_valor` exige > 0, e as duas alternativas seriam afrouxar o CHECK
            // (aceitando zero para sempre, em toda venda nova) ou descartar essas linhas (perdendo
            // a contagem de vendas do cliente). Um centavo preserva a CONTAGEM, que é o que aquele
            // registro sempre teve, sem inventar faturamento que ninguém digitou.
            // O relatório do bloco traz quantos casos apareceram.
            //
            // `etapa_id`: a etapa de ganho da empresa, ou a de maior ordem se ela não tiver uma
            // marcada — a coluna é NOT NULL e um contato ganho sempre veio de algum lugar.
            //
            // `WHERE NOT EXISTS`: a migração é IDEMPOTENTE. Se alguém a rodar duas vezes numa base
            // já migrada — o que acontece em restauração de backup —, o faturamento não dobra.
            // ====================================================================================
            migrationBuilder.Sql("""
                INSERT INTO vendas (empresa_id, contato_id, valor, fechada_em, responsavel_id, etapa_id, criado_em)
                SELECT c.empresa_id,
                       c.id,
                       COALESCE(c.valor, 0.01),
                       c.ganho_em,
                       c.responsavel_id,
                       COALESCE(
                           (SELECT e.id FROM etapas_funil e
                             WHERE e.empresa_id = c.empresa_id AND e.e_ganho
                             ORDER BY e.ordem LIMIT 1),
                           (SELECT e.id FROM etapas_funil e
                             WHERE e.empresa_id = c.empresa_id
                             ORDER BY e.ordem DESC LIMIT 1)),
                       c.ganho_em
                  FROM contatos c
                 WHERE c.ganho_em IS NOT NULL
                   AND EXISTS (SELECT 1 FROM etapas_funil e WHERE e.empresa_id = c.empresa_id)
                   AND NOT EXISTS (
                           SELECT 1 FROM vendas v
                            WHERE v.contato_id = c.id AND v.fechada_em = c.ganho_em);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vendas");
        }
    }
}
