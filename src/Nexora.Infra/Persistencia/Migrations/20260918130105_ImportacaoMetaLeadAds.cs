using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ImportacaoMetaLeadAds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:resultado_linha_enum", "importado,duplicado,invalido")
                .Annotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .Annotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .Annotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .Annotation("Npgsql:Enum:status_importacao_enum", "aguardando_mapeamento,processando,concluida,erro")
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
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.AddColumn<string>(
                name: "meta_ad_id",
                table: "contatos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "meta_campaign_id",
                table: "contatos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "meta_form_id",
                table: "contatos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "meta_lead_id",
                table: "contatos",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "importacoes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    usuario_id = table.Column<long>(type: "bigint", nullable: false),
                    nome_arquivo = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    total_linhas = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    importados = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    duplicados = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    invalidos = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<int>(type: "status_importacao_enum", nullable: false),
                    mapeamento = table.Column<string>(type: "jsonb", nullable: true),
                    erro = table.Column<string>(type: "text", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_importacoes", x => x.id);
                    table.ForeignKey(
                        name: "FK_importacoes_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_importacoes_usuario",
                        columns: x => new { x.usuario_id, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "importacao_linhas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    importacao_id = table.Column<long>(type: "bigint", nullable: false),
                    numero_linha = table.Column<int>(type: "integer", nullable: false),
                    dados_brutos = table.Column<string>(type: "jsonb", nullable: false),
                    resultado = table.Column<int>(type: "resultado_linha_enum", nullable: false),
                    motivo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    contato_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_importacao_linhas", x => x.id);
                    table.ForeignKey(
                        name: "FK_importacao_linhas_importacoes_importacao_id",
                        column: x => x.importacao_id,
                        principalTable: "importacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_contatos_meta_lead_id",
                table: "contatos",
                columns: new[] { "empresa_id", "meta_lead_id" },
                unique: true,
                filter: "meta_lead_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_importacao_linhas_resultado",
                table: "importacao_linhas",
                columns: new[] { "importacao_id", "resultado" });

            migrationBuilder.CreateIndex(
                name: "ix_importacoes_empresa",
                table: "importacoes",
                columns: new[] { "empresa_id", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_importacoes_fila",
                table: "importacoes",
                column: "id",
                filter: "status = 'processando'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "importacao_linhas");

            migrationBuilder.DropTable(
                name: "importacoes");

            migrationBuilder.DropIndex(
                name: "ux_contatos_meta_lead_id",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "meta_ad_id",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "meta_campaign_id",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "meta_form_id",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "meta_lead_id",
                table: "contatos");

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
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:resultado_linha_enum", "importado,duplicado,invalido")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .OldAnnotation("Npgsql:Enum:status_importacao_enum", "aguardando_mapeamento,processando,concluida,erro")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");
        }
    }
}
