using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class VendasMorre : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // =================== A CONFERENCIA ANTES DE DERRUBAR FATURAMENTO ===================
            // `vendas` guardava DINHEIRO. O backfill do E4b criou uma negociacao para cada venda e
            // ligou as duas por `venda_id`; se alguma venda ficou de fora, derrubar a tabela agora
            // apaga receita que nenhum relatorio consegue reconstruir depois.
            //
            // Cancelada nao conta: ela ja estava fora de todo relatorio por definicao, e exigir
            // espelho para ela faria a migracao recusar por causa de linha que ninguem soma.
            //
            // ⚠️ ESTA CHECAGEM TEM DE VIR ANTES DE `DropColumn("venda_id")` — depois dele nao ha
            // mais como saber qual negociacao correspondia a qual venda. E o mesmo motivo pelo
            // qual a coluna existiu: casar por (contato, valor, data) ja derrubou um teste neste
            // projeto, porque duas vendas no mesmo instante casavam as duas.
            // ==================================================================================
            migrationBuilder.Sql("""
                DO $$
                DECLARE orfas bigint; dinheiro numeric;
                BEGIN
                    SELECT count(*), COALESCE(sum(v.valor), 0) INTO orfas, dinheiro
                      FROM vendas v
                     WHERE v.status <> 'cancelada'
                       AND NOT EXISTS (SELECT 1 FROM negociacoes n WHERE n.venda_id = v.id);

                    IF orfas > 0 THEN
                        RAISE EXCEPTION
                            'E4e/5: % venda(s) somando % nao tem negociacao espelho. Derrubar '
                            'a tabela agora apagaria esse faturamento para sempre.',
                            orfas, dinheiro;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_negociacoes_venda",
                table: "negociacoes");

            migrationBuilder.DropTable(
                name: "vendas");

            migrationBuilder.DropIndex(
                name: "uq_negociacoes_venda",
                table: "negociacoes");

            migrationBuilder.DropColumn(
                name: "venda_id",
                table: "negociacoes");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .Annotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .Annotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .Annotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .Annotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video")
                .OldAnnotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:status_venda_enum", "fechada,concluida,cancelada")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");
        }

        /// <inheritdoc />
        /// <summary>⚠️ O `Down` RECRIA A TABELA VAZIA. Mesmo caso do E4e/4: EF sabe recriar a
        /// forma, nao o conteudo. A volta atras de verdade e o backup de antes.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .Annotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .Annotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .Annotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .Annotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:status_venda_enum", "fechada,concluida,cancelada")
                .Annotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video")
                .OldAnnotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.AddColumn<long>(
                name: "venda_id",
                table: "negociacoes",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "vendas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    canal_id = table.Column<long>(type: "bigint", nullable: true),
                    contato_id = table.Column<long>(type: "bigint", nullable: false),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    responsavel_id = table.Column<long>(type: "bigint", nullable: true),
                    cancelada_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelada_por = table.Column<long>(type: "bigint", nullable: true),
                    concluida_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    concluida_por = table.Column<long>(type: "bigint", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    etapa_id = table.Column<long>(type: "bigint", nullable: false),
                    fechada_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    observacao = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "status_venda_enum", nullable: false),
                    valor = table.Column<decimal>(type: "numeric(14,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendas", x => x.id);
                    table.CheckConstraint("ck_vendas_valor", "valor > 0");
                    table.ForeignKey(
                        name: "FK_vendas_canais_captacao_canal_id",
                        column: x => x.canal_id,
                        principalTable: "canais_captacao",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
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
                name: "uq_negociacoes_venda",
                table: "negociacoes",
                column: "venda_id",
                unique: true,
                filter: "venda_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_vendas_canal",
                table: "vendas",
                columns: new[] { "empresa_id", "canal_id", "fechada_em" },
                filter: "canal_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_vendas_contato",
                table: "vendas",
                columns: new[] { "empresa_id", "contato_id", "fechada_em" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_vendas_contato_status",
                table: "vendas",
                columns: new[] { "empresa_id", "contato_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_vendas_periodo",
                table: "vendas",
                columns: new[] { "empresa_id", "fechada_em" },
                descending: new[] { false, true },
                filter: "status <> 'cancelada'");

            migrationBuilder.AddForeignKey(
                name: "fk_negociacoes_venda",
                table: "negociacoes",
                column: "venda_id",
                principalTable: "vendas",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
