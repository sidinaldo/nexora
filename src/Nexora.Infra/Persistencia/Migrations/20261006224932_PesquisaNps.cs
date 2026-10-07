using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class PesquisaNps : Migration
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
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .OldAnnotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            // ===================== UMA DERIVA DA ETAPA 1, ENCERRADA AQUI =====================
            //
            // ⚠️ ESTE `AlterColumn` NAO FOI PEDIDO POR MIM — o EF o gerou, e ele conserta um
            // desencontro real. A etapa 1 trocou `defaultValue: 0` por `defaultValueSql: "'humana'"`
            // DENTRO da migration (o Postgres recusa inteiro como default de enum), mas o SNAPSHOT
            // continuou gravando `0`. Resultado: toda migration nova regenerava este alter.
            //
            // Aplica-lo e o que sincroniza o snapshot e encerra a recorrencia. Contra o banco e
            // inofensivo: o default ja E `'humana'` desde a etapa 1.
            //
            // ⚠️ `oldDefaultValueSql` E NAO `oldDefaultValue: 0`, e isto nao e cosmetico: com o
            // inteiro, o `Down()` tentava devolver `DEFAULT 0` a uma coluna enum e o Postgres
            // recusava com 42804 — "coluna e do tipo origem_mensagem_enum mas expressao padrao e
            // do tipo integer". O rollback desta migration ficava IMPOSSIVEL, e eu descobri isso
            // tentando voltar uma versao para reaplicar o conserto das FKs.
            // =================================================================================
            migrationBuilder.AlterColumn<int>(
                name: "origem",
                table: "mensagens",
                type: "origem_mensagem_enum",
                nullable: false,
                defaultValueSql: "'humana'",
                oldClrType: typeof(int),
                oldType: "origem_mensagem_enum",
                oldDefaultValueSql: "'humana'");

            migrationBuilder.AddColumn<bool>(
                name: "nps_ativo",
                table: "empresas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<short>(
                name: "nps_dias_apos_conclusao",
                table: "empresas",
                type: "smallint",
                nullable: false,
                defaultValue: (short)3);

            migrationBuilder.AddColumn<short>(
                name: "nps_dias_expiracao",
                table: "empresas",
                type: "smallint",
                nullable: false,
                defaultValue: (short)3);

            migrationBuilder.AddColumn<string>(
                name: "nps_mensagem_detrator",
                table: "empresas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nps_mensagem_promotor",
                table: "empresas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nps_texto",
                table: "empresas",
                type: "text",
                nullable: false,
                defaultValue: "Oi, {{nome}}! Aqui é da {{empresa}}. De 0 a 10, quanto você recomendaria a gente para um amigo? É só responder com o número.");

            migrationBuilder.AddUniqueConstraint(
                name: "uq_mensagens_id_empresa",
                table: "mensagens",
                columns: new[] { "id", "empresa_id" });

            migrationBuilder.CreateTable(
                name: "pesquisas_nps",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    negociacao_id = table.Column<long>(type: "bigint", nullable: false),
                    contato_id = table.Column<long>(type: "bigint", nullable: false),
                    mensagem_envio_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<int>(type: "status_pesquisa_nps_enum", nullable: false),
                    data_agendada = table.Column<DateOnly>(type: "date", nullable: false),
                    data_limite = table.Column<DateOnly>(type: "date", nullable: false),
                    data_envio = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    data_resposta = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    nota = table.Column<short>(type: "smallint", nullable: true),
                    comentario = table.Column<string>(type: "text", nullable: true),
                    mensagem_resposta_id = table.Column<long>(type: "bigint", nullable: true),
                    confirmada_por_usuario_id = table.Column<long>(type: "bigint", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pesquisas_nps", x => x.id);
                    table.CheckConstraint("ck_pesquisas_nps_limite", "data_limite >= data_agendada");
                    table.CheckConstraint("ck_pesquisas_nps_nota", "nota IS NULL OR nota BETWEEN 0 AND 10");
                    table.CheckConstraint("ck_pesquisas_nps_respondida", "status <> 'respondida' OR nota IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_pesquisas_nps_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pesquisas_nps_confirmada_por",
                        columns: x => new { x.confirmada_por_usuario_id, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pesquisas_nps_contato",
                        columns: x => new { x.contato_id, x.empresa_id },
                        principalTable: "contatos",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pesquisas_nps_msg_envio",
                        columns: x => new { x.mensagem_envio_id, x.empresa_id },
                        principalTable: "mensagens",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_pesquisas_nps_msg_resposta",
                        columns: x => new { x.mensagem_resposta_id, x.empresa_id },
                        principalTable: "mensagens",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_pesquisas_nps_negociacao",
                        columns: x => new { x.negociacao_id, x.empresa_id },
                        principalTable: "negociacoes",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            // ===================== `SET NULL` COMPOSTO ZERA AS DUAS COLUNAS =====================
            //
            // ⚠️ DEFEITO MEDIDO, NAO SUPOSTO. O `OnDelete(SetNull)` do EF sobre uma FK COMPOSTA
            // gera `ON DELETE SET NULL` sem lista de colunas, e o Postgres entao zera TODAS as
            // colunas da chave — inclusive `empresa_id`, que e NOT NULL. Apagar uma mensagem
            // referenciada por uma pesquisa estourava assim:
            //
            //   UPDATE pesquisas_nps SET "mensagem_envio_id" = NULL, "empresa_id" = NULL ...
            //   ERRO: o valor nulo na coluna "empresa_id" viola a restricao de nao-nulo
            //
            // E o estrago nao seria no NPS: seria no EXPURGO DE MENSAGENS da rodada diaria, que
            // deixaria de rodar para qualquer empresa com uma pesquisa. Um defeito no job de
            // limpeza, causado por uma tabela que ele nem conhece.
            //
            // `SET NULL (coluna)` existe desde o Postgres 15 e resolve: zera SO o ponteiro da
            // mensagem e preserva o tenant. O EF nao sabe gerar isso, entao a FK nasce pelo
            // `CreateTable` acima e e trocada aqui.
            //
            // ⚠️ `Cascade` NAO SERVE DE ALTERNATIVA: levaria a pesquisa junto, e a NOTA e o dado
            // que o relatorio precisa — ela tem de sobreviver ao texto que a trouxe.
            // ⚠️ `Restrict` TAMBEM NAO: travaria o expurgo em vez de quebra-lo, que e o mesmo
            // problema com outra cara.
            //
            // O snapshot do EF continua dizendo `SetNull`, e NAO ha diff recorrente por isso: ele
            // compara o modelo com o snapshot, nao com o banco. Quem recriar esta tabela um dia
            // tem de reaplicar estas duas linhas — e e por isso que o comentario do
            // `NexoraDbContext` tambem aponta para ca.
            // ====================================================================================
            migrationBuilder.Sql("""
                ALTER TABLE pesquisas_nps DROP CONSTRAINT fk_pesquisas_nps_msg_envio;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE pesquisas_nps ADD CONSTRAINT fk_pesquisas_nps_msg_envio
                    FOREIGN KEY (mensagem_envio_id, empresa_id)
                    REFERENCES mensagens (id, empresa_id)
                    ON DELETE SET NULL (mensagem_envio_id);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE pesquisas_nps DROP CONSTRAINT fk_pesquisas_nps_msg_resposta;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE pesquisas_nps ADD CONSTRAINT fk_pesquisas_nps_msg_resposta
                    FOREIGN KEY (mensagem_resposta_id, empresa_id)
                    REFERENCES mensagens (id, empresa_id)
                    ON DELETE SET NULL (mensagem_resposta_id);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_pesquisas_nps_aberta",
                table: "pesquisas_nps",
                columns: new[] { "empresa_id", "contato_id" },
                filter: "status IN ('enviada', 'possivel_nota')");

            migrationBuilder.CreateIndex(
                name: "ix_pesquisas_nps_agenda",
                table: "pesquisas_nps",
                columns: new[] { "empresa_id", "data_agendada" },
                filter: "status = 'agendada'");

            migrationBuilder.CreateIndex(
                name: "uq_pesquisas_nps_negociacao",
                table: "pesquisas_nps",
                column: "negociacao_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pesquisas_nps");

            migrationBuilder.DropUniqueConstraint(
                name: "uq_mensagens_id_empresa",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "nps_ativo",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "nps_dias_apos_conclusao",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "nps_dias_expiracao",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "nps_mensagem_detrator",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "nps_mensagem_promotor",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "nps_texto",
                table: "empresas");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
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
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .Annotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .Annotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video")
                .OldAnnotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
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

            // O default fica como esta: voltar atras seria devolver `DEFAULT 0` a uma coluna
            // enum, que o Postgres recusa (42804) — ver o comentario longo no `Up`. O estado
            // anterior REAL do banco ja era `'humana'`; so o snapshot discordava.
            migrationBuilder.AlterColumn<int>(
                name: "origem",
                table: "mensagens",
                type: "origem_mensagem_enum",
                nullable: false,
                defaultValueSql: "'humana'",
                oldClrType: typeof(int),
                oldType: "origem_mensagem_enum",
                oldDefaultValueSql: "'humana'");
        }
    }
}
