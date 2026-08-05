using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Dominio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_usuarios_id_empresa",
                table: "usuarios");

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
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo");

            migrationBuilder.AddUniqueConstraint(
                name: "uq_usuarios_id_empresa",
                table: "usuarios",
                columns: new[] { "id", "empresa_id" });

            migrationBuilder.CreateTable(
                name: "conexoes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    nome = table.Column<string>(type: "text", nullable: false),
                    instance_name = table.Column<string>(type: "text", nullable: false),
                    numero = table.Column<string>(type: "text", nullable: true),
                    numero_anterior = table.Column<string>(type: "text", nullable: true),
                    perfil_nome = table.Column<string>(type: "text", nullable: true),
                    perfil_foto_url = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "status_conexao_enum", nullable: false),
                    status_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    conectado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    desconectado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conexoes", x => x.id);
                    table.UniqueConstraint("uq_conexoes_id_empresa", x => new { x.id, x.empresa_id });
                    table.ForeignKey(
                        name: "FK_conexoes_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "etapas_funil",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    nome = table.Column<string>(type: "text", nullable: false),
                    ordem = table.Column<short>(type: "smallint", nullable: false),
                    cor = table.Column<string>(type: "text", nullable: false, defaultValue: "#2F5D3A"),
                    e_ganho = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_etapas_funil", x => x.id);
                    table.UniqueConstraint("uq_etapas_id_empresa", x => new { x.id, x.empresa_id });
                    table.ForeignKey(
                        name: "FK_etapas_funil_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "contatos",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    nome = table.Column<string>(type: "text", nullable: false),
                    telefone = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    origem = table.Column<int>(type: "origem_lead_enum", nullable: false),
                    origem_detalhe = table.Column<string>(type: "text", nullable: true),
                    etapa_id = table.Column<long>(type: "bigint", nullable: false),
                    ordem_kanban = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 0m),
                    responsavel_id = table.Column<long>(type: "bigint", nullable: true),
                    valor = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    ganho_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    perdido_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    motivo_perda = table.Column<string>(type: "text", nullable: true),
                    observacoes = table.Column<string>(type: "text", nullable: true),
                    anonimizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contatos", x => x.id);
                    table.UniqueConstraint("uq_contatos_id_empresa", x => new { x.id, x.empresa_id });
                    table.CheckConstraint("ck_contatos_terminal", "ganho_em IS NULL OR perdido_em IS NULL");
                    table.ForeignKey(
                        name: "FK_contatos_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contatos_etapa",
                        columns: x => new { x.etapa_id, x.empresa_id },
                        principalTable: "etapas_funil",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contatos_responsavel",
                        columns: x => new { x.responsavel_id, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conversas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    contato_id = table.Column<long>(type: "bigint", nullable: false),
                    conexao_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<int>(type: "status_conversa_enum", nullable: false),
                    responsavel_id = table.Column<long>(type: "bigint", nullable: true),
                    atribuido_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    aguardando_desde = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ultima_mensagem_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ultima_mensagem_direcao = table.Column<int>(type: "direcao_mensagem_enum", nullable: true),
                    ultima_mensagem_previa = table.Column<string>(type: "text", nullable: true),
                    nao_lidas = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    resolvido_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolvido_por = table.Column<long>(type: "bigint", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversas", x => x.id);
                    table.UniqueConstraint("uq_conversas_id_empresa", x => new { x.id, x.empresa_id });
                    table.CheckConstraint("ck_conversas_nao_lidas", "nao_lidas >= 0");
                    table.ForeignKey(
                        name: "FK_conversas_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conversas_conexao",
                        columns: x => new { x.conexao_id, x.empresa_id },
                        principalTable: "conexoes",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conversas_contato",
                        columns: x => new { x.contato_id, x.empresa_id },
                        principalTable: "contatos",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conversas_resolvido_por",
                        columns: x => new { x.resolvido_por, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conversas_responsavel",
                        columns: x => new { x.responsavel_id, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lembretes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    contato_id = table.Column<long>(type: "bigint", nullable: false),
                    conversa_id = table.Column<long>(type: "bigint", nullable: true),
                    origem = table.Column<int>(type: "origem_lembrete_enum", nullable: false),
                    status = table.Column<int>(type: "status_lembrete_enum", nullable: false),
                    data_alvo = table.Column<DateOnly>(type: "date", nullable: false),
                    hora_alvo = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    titulo = table.Column<string>(type: "text", nullable: false),
                    observacao = table.Column<string>(type: "text", nullable: true),
                    envia_mensagem = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    texto_mensagem = table.Column<string>(type: "text", nullable: true),
                    responsavel_id = table.Column<long>(type: "bigint", nullable: true),
                    criado_por = table.Column<long>(type: "bigint", nullable: true),
                    concluido_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    concluido_por = table.Column<long>(type: "bigint", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lembretes", x => x.id);
                    table.UniqueConstraint("uq_lembretes_id_empresa", x => new { x.id, x.empresa_id });
                    table.CheckConstraint("ck_lembretes_texto", "NOT envia_mensagem OR texto_mensagem IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_lembretes_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lembretes_concluido_por",
                        columns: x => new { x.concluido_por, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lembretes_contato",
                        columns: x => new { x.contato_id, x.empresa_id },
                        principalTable: "contatos",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lembretes_conversa",
                        columns: x => new { x.conversa_id, x.empresa_id },
                        principalTable: "conversas",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lembretes_criado_por",
                        columns: x => new { x.criado_por, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lembretes_responsavel",
                        columns: x => new { x.responsavel_id, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mensagens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    conversa_id = table.Column<long>(type: "bigint", nullable: false),
                    contato_id = table.Column<long>(type: "bigint", nullable: false),
                    conexao_id = table.Column<long>(type: "bigint", nullable: false),
                    instance_name = table.Column<string>(type: "text", nullable: false),
                    direcao = table.Column<int>(type: "direcao_mensagem_enum", nullable: false),
                    wa_message_id = table.Column<string>(type: "text", nullable: true),
                    texto = table.Column<string>(type: "text", nullable: true),
                    tipo_midia = table.Column<int>(type: "tipo_midia_enum", nullable: false),
                    midia_chave = table.Column<string>(type: "text", nullable: true),
                    midia_mime = table.Column<string>(type: "text", nullable: true),
                    midia_nome = table.Column<string>(type: "text", nullable: true),
                    midia_bytes = table.Column<int>(type: "integer", nullable: true),
                    ack = table.Column<short>(type: "smallint", nullable: true),
                    ack_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    enviado_por = table.Column<long>(type: "bigint", nullable: true),
                    lembrete_id = table.Column<long>(type: "bigint", nullable: true),
                    data_disparo = table.Column<DateOnly>(type: "date", nullable: true),
                    reservado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    enviada_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    recebida_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    erro = table.Column<string>(type: "text", nullable: true),
                    payload_raw = table.Column<string>(type: "jsonb", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mensagens", x => x.id);
                    table.CheckConstraint("ck_msg_ack", "ack IS NULL OR ack BETWEEN 0 AND 4");
                    table.CheckConstraint("ck_msg_data_disparo", "direcao = 'entrada' OR data_disparo IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_mensagens_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mensagens_lembrete",
                        columns: x => new { x.lembrete_id, x.empresa_id },
                        principalTable: "lembretes",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_msg_conexao",
                        columns: x => new { x.conexao_id, x.empresa_id },
                        principalTable: "conexoes",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_msg_contato",
                        columns: x => new { x.contato_id, x.empresa_id },
                        principalTable: "contatos",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_msg_conversa",
                        columns: x => new { x.conversa_id, x.empresa_id },
                        principalTable: "conversas",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_msg_enviado_por",
                        columns: x => new { x.enviado_por, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "uq_conexoes_empresa",
                table: "conexoes",
                column: "empresa_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_conexoes_instance",
                table: "conexoes",
                column: "instance_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_contatos_criado",
                table: "contatos",
                columns: new[] { "empresa_id", "criado_em" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_contatos_ganho",
                table: "contatos",
                columns: new[] { "empresa_id", "ganho_em" },
                descending: new[] { false, true },
                filter: "ganho_em IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_contatos_kanban",
                table: "contatos",
                columns: new[] { "empresa_id", "etapa_id", "ordem_kanban" },
                filter: "perdido_em IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_contatos_responsavel",
                table: "contatos",
                columns: new[] { "empresa_id", "responsavel_id" },
                filter: "responsavel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_contatos_telefone",
                table: "contatos",
                columns: new[] { "empresa_id", "telefone" },
                unique: true,
                filter: "anonimizado_em IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_conversas_aguardando",
                table: "conversas",
                columns: new[] { "empresa_id", "aguardando_desde" },
                filter: "aguardando_desde IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_conversas_lista",
                table: "conversas",
                columns: new[] { "empresa_id", "status", "ultima_mensagem_em", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_conversas_responsavel",
                table: "conversas",
                columns: new[] { "empresa_id", "responsavel_id", "ultima_mensagem_em" },
                descending: new[] { false, false, true },
                filter: "responsavel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_conversas_contato",
                table: "conversas",
                column: "contato_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_etapas_ganho",
                table: "etapas_funil",
                column: "empresa_id",
                unique: true,
                filter: "e_ganho");

            migrationBuilder.CreateIndex(
                name: "uq_etapas_ordem",
                table: "etapas_funil",
                columns: new[] { "empresa_id", "ordem" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lembretes_dia",
                table: "lembretes",
                columns: new[] { "empresa_id", "data_alvo", "responsavel_id" },
                filter: "status = 'pendente'");

            migrationBuilder.CreateIndex(
                name: "ix_lembretes_disparo",
                table: "lembretes",
                columns: new[] { "empresa_id", "data_alvo" },
                filter: "status = 'pendente' AND envia_mensagem");

            migrationBuilder.CreateIndex(
                name: "uq_lembrete_teto_diario",
                table: "lembretes",
                columns: new[] { "contato_id", "data_alvo" },
                unique: true,
                filter: "origem = 'automatico' AND envia_mensagem AND status <> 'cancelado'");

            migrationBuilder.CreateIndex(
                name: "ix_msg_pendentes",
                table: "mensagens",
                columns: new[] { "empresa_id", "data_disparo" },
                filter: "enviada_em IS NULL AND direcao = 'saida'");

            migrationBuilder.CreateIndex(
                name: "ix_msg_timeline",
                table: "mensagens",
                columns: new[] { "empresa_id", "conversa_id", "id" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "uq_msg_lembrete",
                table: "mensagens",
                column: "lembrete_id",
                unique: true,
                filter: "lembrete_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_msg_wa_id",
                table: "mensagens",
                columns: new[] { "instance_name", "wa_message_id" },
                unique: true,
                filter: "wa_message_id IS NOT NULL AND wa_message_id <> ''");

            // ---------------------------------------------------------------------------
            // O que o modelo do EF nao expressa (mesmo caso da migration Inicial).
            // ---------------------------------------------------------------------------

            // 1) DEFAULT das colunas de enum. Nao pode vir do modelo: em tempo de design o
            //    provider nao carrega o mapeamento do enum (ver FabricaDbContextDesignTime) e
            //    renderiza o default como inteiro (DEFAULT 0), que o Postgres recusa.
            migrationBuilder.Sql(@"
                ALTER TABLE conexoes  ALTER COLUMN status     SET DEFAULT 'nao_criada';
                ALTER TABLE contatos  ALTER COLUMN origem     SET DEFAULT 'whatsapp';
                ALTER TABLE conversas ALTER COLUMN status     SET DEFAULT 'aberta';
                ALTER TABLE mensagens ALTER COLUMN tipo_midia SET DEFAULT 'nenhum';
                ALTER TABLE lembretes ALTER COLUMN status     SET DEFAULT 'pendente';");

            // 2) Triggers de atualizado_em nas tabelas novas. A funcao fn_atualizado_em() ja
            //    foi criada na migration Inicial. `mensagens` NAO entra: e log append-only,
            //    sem coluna atualizado_em.
            //
            //    O InterceptorAuditoria cobre o que passa pelo EF; o trigger cobre o que NAO
            //    passa — e o bloco de envio vai gravar a outbox com INSERT ... ON CONFLICT em
            //    SQL cru, que nao dispara SaveChanges.
            migrationBuilder.Sql(@"
                CREATE TRIGGER tg_conexoes_atualizado  BEFORE UPDATE ON conexoes
                    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();
                CREATE TRIGGER tg_etapas_atualizado    BEFORE UPDATE ON etapas_funil
                    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();
                CREATE TRIGGER tg_contatos_atualizado  BEFORE UPDATE ON contatos
                    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();
                CREATE TRIGGER tg_conversas_atualizado BEFORE UPDATE ON conversas
                    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();
                CREATE TRIGGER tg_lembretes_atualizado BEFORE UPDATE ON lembretes
                    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mensagens");

            migrationBuilder.DropTable(
                name: "lembretes");

            migrationBuilder.DropTable(
                name: "conversas");

            migrationBuilder.DropTable(
                name: "conexoes");

            migrationBuilder.DropTable(
                name: "contatos");

            migrationBuilder.DropTable(
                name: "etapas_funil");

            migrationBuilder.DropUniqueConstraint(
                name: "uq_usuarios_id_empresa",
                table: "usuarios");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.CreateIndex(
                name: "uq_usuarios_id_empresa",
                table: "usuarios",
                columns: new[] { "id", "empresa_id" },
                unique: true);
        }
    }
}
