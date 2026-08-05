using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class CamadaDeTempo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.AddColumn<short>(
                name: "dias_sem_resposta_followup",
                table: "empresas",
                type: "smallint",
                nullable: false,
                defaultValue: (short)2);

            migrationBuilder.AddColumn<short>(
                name: "semaforo_amarelo_minutos",
                table: "empresas",
                type: "smallint",
                nullable: false,
                defaultValue: (short)60);

            migrationBuilder.AddColumn<short>(
                name: "semaforo_vermelho_minutos",
                table: "empresas",
                type: "smallint",
                nullable: false,
                defaultValue: (short)240);

            migrationBuilder.CreateTable(
                name: "feriados",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: true),
                    data = table.Column<DateOnly>(type: "date", nullable: false),
                    nome = table.Column<string>(type: "text", nullable: false),
                    abrangencia = table.Column<int>(type: "abrangencia_feriado_enum", nullable: false),
                    uf = table.Column<string>(type: "text", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feriados", x => x.id);
                    table.ForeignKey(
                        name: "FK_feriados_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_feriados_data",
                table: "feriados",
                column: "data");

            // ---------------------------------------------------------------------------
            // O que o modelo do EF nao expressa.
            // ---------------------------------------------------------------------------

            // Indice unico FUNCIONAL (COALESCE nas colunas anulaveis) — o EF Core nao modela
            // indice por expressao, entao ele NAO aparece no ModelSnapshot.
            //
            // E o que sustenta a IDEMPOTENCIA do seed anual: o job roda no boot e de novo na
            // rodada diaria, e as duas execucoes usam INSERT ... ON CONFLICT DO NOTHING. Sem o
            // indice, o ON CONFLICT nao tem contra o que colidir e o feriado entra duplicado a
            // cada execucao — inclusive com duas instancias correndo em paralelo.
            //
            // COALESCE porque `empresa_id` (global = NULL) e `uf` (nao-estadual = NULL) sao
            // anulaveis, e em indice unico do Postgres NULL nunca colide com NULL.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX uq_feriados
                    ON feriados (data, abrangencia, COALESCE(uf, ''), COALESCE(empresa_id, 0));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feriados");

            migrationBuilder.DropColumn(
                name: "dias_sem_resposta_followup",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "semaforo_amarelo_minutos",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "semaforo_vermelho_minutos",
                table: "empresas");

            migrationBuilder.AlterDatabase()
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
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");
        }
    }
}
