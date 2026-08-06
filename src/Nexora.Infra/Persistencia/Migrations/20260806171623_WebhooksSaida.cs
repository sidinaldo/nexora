using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class WebhooksSaida : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video")
                .OldAnnotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.CreateTable(
                name: "entregas_webhook",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    evento_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evento = table.Column<int>(type: "evento_webhook_enum", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<int>(type: "status_entrega_webhook_enum", nullable: false),
                    tentativas = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    codigo_resposta = table.Column<int>(type: "integer", nullable: true),
                    erro = table.Column<string>(type: "text", nullable: true),
                    proxima_tentativa_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    entregue_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entregas_webhook", x => x.id);
                    table.ForeignKey(
                        name: "FK_entregas_webhook_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhooks_saida",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    segredo = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ativo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    somente_ids = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    em_lead_criado = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    em_lead_movido = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    em_venda_fechada = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    em_venda_perdida = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    em_mensagem_recebida = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhooks_saida", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhooks_saida_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_entregas_criado",
                table: "entregas_webhook",
                column: "criado_em");

            migrationBuilder.CreateIndex(
                name: "ix_entregas_empresa",
                table: "entregas_webhook",
                columns: new[] { "empresa_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_entregas_fila",
                table: "entregas_webhook",
                column: "proxima_tentativa_em",
                filter: "status = 'pendente'");

            migrationBuilder.CreateIndex(
                name: "uq_webhooks_empresa",
                table: "webhooks_saida",
                column: "empresa_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entregas_webhook");

            migrationBuilder.DropTable(
                name: "webhooks_saida");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .Annotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .Annotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
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
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");
        }
    }
}
