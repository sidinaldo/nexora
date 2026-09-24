using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ConversoesDeAnuncio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
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
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
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

            migrationBuilder.AddUniqueConstraint(
                name: "uq_formularios_id_empresa",
                table: "formularios_captura",
                columns: new[] { "id", "empresa_id" });

            migrationBuilder.CreateTable(
                name: "credenciais_conversao",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    plataforma = table.Column<int>(type: "plataforma_conversao_enum", nullable: false),
                    identificador = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    token = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    codigo_teste = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ativo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    em_lead = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    em_compra = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    consentimento_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consentimento_por = table.Column<long>(type: "bigint", nullable: true),
                    desativada_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    desativada_motivo = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credenciais_conversao", x => x.id);
                    table.ForeignKey(
                        name: "FK_credenciais_conversao_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_credenciais_consentimento",
                        columns: x => new { x.consentimento_por, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "eventos_conversao",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    plataforma = table.Column<int>(type: "plataforma_conversao_enum", nullable: false),
                    tipo = table.Column<int>(type: "tipo_conversao_enum", nullable: false),
                    evento_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contato_id = table.Column<long>(type: "bigint", nullable: false),
                    negociacao_id = table.Column<long>(type: "bigint", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    ocorrido_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expira_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<int>(type: "status_conversao_enum", nullable: false),
                    tentativas = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    proxima_tentativa_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    codigo_resposta = table.Column<int>(type: "integer", nullable: true),
                    codigo_meta = table.Column<int>(type: "integer", nullable: true),
                    fbtrace_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    erro = table.Column<string>(type: "text", nullable: true),
                    entregue_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eventos_conversao", x => x.id);
                    table.CheckConstraint("ck_conversoes_expira", "expira_em > ocorrido_em");
                    table.CheckConstraint("ck_conversoes_negociacao", "(tipo = 'compra') = (negociacao_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_eventos_conversao_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conversoes_contato",
                        columns: x => new { x.contato_id, x.empresa_id },
                        principalTable: "contatos",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conversoes_negociacao",
                        columns: x => new { x.negociacao_id, x.empresa_id },
                        principalTable: "negociacoes",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rastreios_lead",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    contato_id = table.Column<long>(type: "bigint", nullable: false),
                    fonte = table.Column<int>(type: "fonte_rastreio_enum", nullable: false),
                    utm_source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    utm_medium = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    utm_campaign = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    utm_content = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    utm_term = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    pagina = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    referencia = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    formulario_id = table.Column<long>(type: "bigint", nullable: true),
                    identificadores = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    evento_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ocorrido_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rastreios_lead", x => x.id);
                    table.ForeignKey(
                        name: "FK_rastreios_lead_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_rastreios_contato",
                        columns: x => new { x.contato_id, x.empresa_id },
                        principalTable: "contatos",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_credenciais_empresa_plataforma",
                table: "credenciais_conversao",
                columns: new[] { "empresa_id", "plataforma" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_conversoes_contato",
                table: "eventos_conversao",
                column: "contato_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversoes_criado",
                table: "eventos_conversao",
                column: "criado_em");

            migrationBuilder.CreateIndex(
                name: "ix_conversoes_empresa",
                table: "eventos_conversao",
                columns: new[] { "empresa_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_conversoes_fila",
                table: "eventos_conversao",
                column: "proxima_tentativa_em",
                filter: "status = 'pendente'");

            migrationBuilder.CreateIndex(
                name: "uq_conversoes_compra",
                table: "eventos_conversao",
                column: "negociacao_id",
                unique: true,
                filter: "tipo = 'compra'");

            migrationBuilder.CreateIndex(
                name: "uq_conversoes_lead",
                table: "eventos_conversao",
                column: "contato_id",
                unique: true,
                filter: "tipo = 'lead'");

            migrationBuilder.CreateIndex(
                name: "ix_rastreios_campanha",
                table: "rastreios_lead",
                columns: new[] { "empresa_id", "utm_campaign" });

            migrationBuilder.CreateIndex(
                name: "uq_rastreios_contato",
                table: "rastreios_lead",
                column: "contato_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credenciais_conversao");

            migrationBuilder.DropTable(
                name: "eventos_conversao");

            migrationBuilder.DropTable(
                name: "rastreios_lead");

            migrationBuilder.DropUniqueConstraint(
                name: "uq_formularios_id_empresa",
                table: "formularios_captura");

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
                .OldAnnotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
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
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");
        }
    }
}
