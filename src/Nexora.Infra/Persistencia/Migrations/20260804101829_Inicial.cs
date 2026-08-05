using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo");

            migrationBuilder.CreateTable(
                name: "empresas",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    nome = table.Column<string>(type: "text", nullable: false),
                    documento = table.Column<string>(type: "text", nullable: true),
                    ativo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    janela_hora_inicio = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)8),
                    janela_hora_fim = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)20),
                    janela_dias_semana = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)126),
                    fuso_horario = table.Column<string>(type: "text", nullable: false, defaultValue: "America/Sao_Paulo"),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_empresas", x => x.id);
                    table.CheckConstraint("ck_empresas_dias", "janela_dias_semana BETWEEN 1 AND 127");
                    table.CheckConstraint("ck_empresas_hora_faixa", "janela_hora_inicio BETWEEN 0 AND 23 AND janela_hora_fim BETWEEN 1 AND 24");
                    table.CheckConstraint("ck_empresas_janela", "janela_hora_inicio < janela_hora_fim");
                });

            migrationBuilder.CreateTable(
                name: "usuarios",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    nome = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    senha_hash = table.Column<string>(type: "text", nullable: true),
                    papel = table.Column<int>(type: "papel_usuario_enum", nullable: false),
                    status = table.Column<int>(type: "status_usuario_enum", nullable: false),
                    falhas_login = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    bloqueado_ate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    token_convite = table.Column<string>(type: "text", nullable: true),
                    convite_expira = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    token_reset = table.Column<string>(type: "text", nullable: true),
                    reset_expira = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ultimo_acesso_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usuarios", x => x.id);
                    table.CheckConstraint("ck_usuarios_senha", "status = 'convidado' OR senha_hash IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_usuarios_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_usuarios_empresa",
                table: "usuarios",
                columns: new[] { "empresa_id", "status" });

            migrationBuilder.CreateIndex(
                name: "uq_usuarios_id_empresa",
                table: "usuarios",
                columns: new[] { "id", "empresa_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_usuarios_token_convite",
                table: "usuarios",
                column: "token_convite",
                unique: true,
                filter: "token_convite IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_usuarios_token_reset",
                table: "usuarios",
                column: "token_reset",
                unique: true,
                filter: "token_reset IS NOT NULL");

            // ---------------------------------------------------------------------------
            // O que o modelo do EF nao expressa. Continua sendo migration (versionada, com
            // Down, aplicada por `dotnet dotnet-ef database update`) — o que a regra do
            // projeto proibe e .sql solto aplicado a mao, nao SQL dentro de migration.
            // ---------------------------------------------------------------------------

            // 1) Default do enum. Nao pode vir do modelo: em tempo de design o provider nao
            //    carrega o mapeamento do enum (ver FabricaDbContextDesignTime) e renderizaria
            //    o default como inteiro (DEFAULT 0), que o Postgres recusa na coluna
            //    status_usuario_enum.
            migrationBuilder.Sql(
                @"ALTER TABLE usuarios ALTER COLUMN status SET DEFAULT 'ativo';");

            // 2) Indice unico FUNCIONAL: o e-mail e unico GLOBALMENTE e sem diferenciar caixa.
            //    O login busca por lower(email) sem tenant no contexto; um indice sobre a
            //    coluna crua deixaria "Joao@x.com" e "joao@x.com" coexistirem, e o login
            //    autenticaria num deles arbitrariamente. O EF Core nao modela indice por
            //    expressao, entao ele NAO aparece no ModelSnapshot — uma migration futura nao
            //    vai recria-lo sozinha.
            migrationBuilder.Sql(
                @"CREATE UNIQUE INDEX uq_usuarios_email ON usuarios (lower(email));");

            // 3) Trigger de atualizado_em. O InterceptorAuditoria ja cobre tudo que passa pelo
            //    EF; o trigger cobre o que NAO passa — SQL cru, correcao manual em producao, e
            //    o INSERT ... ON CONFLICT do outbox que chega no bloco 4. now() e o horario de
            //    INICIO DA TRANSACAO (nao do statement): linhas alteradas na mesma transacao
            //    recebem o mesmo carimbo, que e o comportamento desejado.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION fn_atualizado_em() RETURNS trigger AS $$
                BEGIN
                    NEW.atualizado_em := now();
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tg_empresas_atualizado BEFORE UPDATE ON empresas
                    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tg_usuarios_atualizado BEFORE UPDATE ON usuarios
                    FOR EACH ROW EXECUTE FUNCTION fn_atualizado_em();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Ordem inversa do Up. Os triggers caem junto com as tabelas, mas o indice
            // funcional e a funcao sao objetos independentes e precisam de DROP explicito.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS uq_usuarios_email;");

            migrationBuilder.DropTable(
                name: "usuarios");

            migrationBuilder.DropTable(
                name: "empresas");

            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS fn_atualizado_em();");
        }
    }
}
