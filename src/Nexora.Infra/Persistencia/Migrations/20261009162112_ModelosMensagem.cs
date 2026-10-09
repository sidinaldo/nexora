using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>Os templates da API oficial (INT-XX): `modelos_mensagem`, com a revisao da Meta, e
    /// `mensagens.modelo_id` — o que faz o reenvio de um template sair como template.
    ///
    /// `fk_msg_modelo` e composta e RESTRICT de proposito: o `SET NULL` do EF zeraria tambem o
    /// `empresa_id` (ver `FkMensagemNegociacaoSetNullColuna`).</summary>
    public partial class ModelosMensagem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:canal_whatsapp_enum", "evolution,cloud_api")
                .Annotation("Npgsql:Enum:categoria_modelo_enum", "utility,marketing,authentication")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .Annotation("Npgsql:Enum:origem_mensagem_enum", "humana,automatica")
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:plataforma_conversao_enum", "meta")
                .Annotation("Npgsql:Enum:resultado_linha_enum", "importado,duplicado,invalido")
                .Annotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .Annotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .Annotation("Npgsql:Enum:status_conversao_enum", "pendente,entregue,falhou,expirado,cancelado")
                .Annotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .Annotation("Npgsql:Enum:status_importacao_enum", "aguardando_mapeamento,processando,concluida,erro")
                .Annotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .Annotation("Npgsql:Enum:status_modelo_enum", "rascunho,enviado,aprovado,rejeitado")
                .Annotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .Annotation("Npgsql:Enum:status_pesquisa_nps_enum", "agendada,enviada,respondida,possivel_nota,expirada,cancelada")
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .Annotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .Annotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video")
                .OldAnnotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .OldAnnotation("Npgsql:Enum:canal_whatsapp_enum", "evolution,cloud_api")
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .OldAnnotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:origem_mensagem_enum", "humana,automatica")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:plataforma_conversao_enum", "meta")
                .OldAnnotation("Npgsql:Enum:resultado_linha_enum", "importado,duplicado,invalido")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_conversao_enum", "pendente,entregue,falhou,expirado,cancelado")
                .OldAnnotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .OldAnnotation("Npgsql:Enum:status_importacao_enum", "aguardando_mapeamento,processando,concluida,erro")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .OldAnnotation("Npgsql:Enum:status_pesquisa_nps_enum", "agendada,enviada,respondida,possivel_nota,expirada,cancelada")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .OldAnnotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.AddColumn<long>(
                name: "modelo_id",
                table: "mensagens",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "modelos_mensagem",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    conexao_id = table.Column<long>(type: "bigint", nullable: false),
                    waba_id = table.Column<string>(type: "text", nullable: false),
                    nome = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    categoria = table.Column<int>(type: "categoria_modelo_enum", nullable: false),
                    idioma = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    corpo = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    variaveis = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<int>(type: "status_modelo_enum", nullable: false),
                    motivo_rejeicao = table.Column<string>(type: "text", nullable: true),
                    id_meta = table.Column<string>(type: "text", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_modelos_mensagem", x => x.id);
                    table.UniqueConstraint("uq_modelos_mensagem_id_empresa", x => new { x.id, x.empresa_id });
                    table.ForeignKey(
                        name: "FK_modelos_mensagem_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_modelos_conexao",
                        columns: x => new { x.conexao_id, x.empresa_id },
                        principalTable: "conexoes",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_msg_modelo",
                table: "mensagens",
                column: "modelo_id",
                filter: "modelo_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_modelos_id_meta",
                table: "modelos_mensagem",
                column: "id_meta",
                filter: "id_meta IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_modelos_conta_nome_idioma",
                table: "modelos_mensagem",
                columns: new[] { "empresa_id", "waba_id", "nome", "idioma" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_msg_modelo",
                table: "mensagens",
                columns: new[] { "modelo_id", "empresa_id" },
                principalTable: "modelos_mensagem",
                principalColumns: new[] { "id", "empresa_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_msg_modelo",
                table: "mensagens");

            migrationBuilder.DropTable(
                name: "modelos_mensagem");

            migrationBuilder.DropIndex(
                name: "ix_msg_modelo",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "modelo_id",
                table: "mensagens");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:canal_whatsapp_enum", "evolution,cloud_api")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .Annotation("Npgsql:Enum:origem_mensagem_enum", "humana,automatica")
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:plataforma_conversao_enum", "meta")
                .Annotation("Npgsql:Enum:resultado_linha_enum", "importado,duplicado,invalido")
                .Annotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .Annotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .Annotation("Npgsql:Enum:status_conversao_enum", "pendente,entregue,falhou,expirado,cancelado")
                .Annotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .Annotation("Npgsql:Enum:status_importacao_enum", "aguardando_mapeamento,processando,concluida,erro")
                .Annotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .Annotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .Annotation("Npgsql:Enum:status_pesquisa_nps_enum", "agendada,enviada,respondida,possivel_nota,expirada,cancelada")
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .Annotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .Annotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video")
                .OldAnnotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .OldAnnotation("Npgsql:Enum:canal_whatsapp_enum", "evolution,cloud_api")
                .OldAnnotation("Npgsql:Enum:categoria_modelo_enum", "utility,marketing,authentication")
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .OldAnnotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:origem_mensagem_enum", "humana,automatica")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:plataforma_conversao_enum", "meta")
                .OldAnnotation("Npgsql:Enum:resultado_linha_enum", "importado,duplicado,invalido")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_conversao_enum", "pendente,entregue,falhou,expirado,cancelado")
                .OldAnnotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .OldAnnotation("Npgsql:Enum:status_importacao_enum", "aguardando_mapeamento,processando,concluida,erro")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_modelo_enum", "rascunho,enviado,aprovado,rejeitado")
                .OldAnnotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .OldAnnotation("Npgsql:Enum:status_pesquisa_nps_enum", "agendada,enviada,respondida,possivel_nota,expirada,cancelada")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .OldAnnotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");
        }
    }
}
