using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>OPE-1 — o catálogo de planos e o teto de pessoas por empresa.
    ///
    /// ===================== A ORDEM DOS PASSOS É A PARTE PERIGOSA =====================
    /// O `AddCheckConstraint` do limite de usuários vem DEPOIS do backfill, e não antes. Invertido,
    /// uma empresa com mais de 50 pessoas faria a migration falhar no `UPDATE` — mesmo desfecho,
    /// mensagem muito pior: o erro apontaria para uma linha de dados em vez de para a regra violada.
    ///
    /// E o backfill vem depois do `AddColumn` por obrigação: ele lê a coluna que acabou de nascer.
    /// =================================================================================</summary>
    public partial class PlanosELimiteDeUsuarios : Migration
    {
        /// <summary>⚠️ O `DEFAULT 3` SOZINHO MACHUCARIA CLIENTE EXISTENTE. Qualquer empresa com 4 ou
        /// mais pessoas nasceria acima da cota no instante da migration, e o sintoma seria o convite
        /// seguinte do dono falhando com uma mensagem que ninguém esperava.
        ///
        /// `GREATEST(3, vagas)` dá a cada empresa pelo menos o que ela já usa. Contam `ativo` e
        /// `convidado`, que é a regra de vaga do sistema — ver `Empresa.LimiteUsuarios`.
        ///
        /// ⚠️ E NÃO HÁ `LEAST(50, …)`, DE PROPÓSITO. Se alguma empresa tiver 51 pessoas, esta
        /// migration FALHA ALTO no CHECK logo abaixo. É o desfecho correto: limitar o time real de
        /// um cliente a 50 em silêncio é perda de dado que ninguém encontra por meses, e a resposta
        /// certa a um tenant de 51 pessoas é subir o teto, não aparar a linha.</summary>
        /// <remarks>⚠️ `public`, e não `private`, de propósito: `PlanosDbTests` executa ESTA
        /// constante, não uma cópia dela. Backfill testado por cópia é backfill NÃO testado — a
        /// cópia pode estar certa enquanto a migration está errada, e o teste fica verde contando
        /// uma verdade sobre o arquivo errado.
        ///
        /// `internal` seria mais contido, mas exigiria `InternalsVisibleTo` no projeto inteiro, e
        /// não há precedente disso aqui. Abrir um projeto por causa de uma constante é caro demais
        /// para o que se ganha.</remarks>
        public const string Backfill = @"
            UPDATE empresas e
               SET limite_usuarios = GREATEST(
                       3,
                       (SELECT COUNT(*) FROM usuarios u
                         WHERE u.empresa_id = e.id
                           AND u.status IN ('ativo', 'convidado'))
                   );";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "limite_usuarios",
                table: "empresas",
                type: "smallint",
                nullable: false,
                defaultValue: (short)3);

            migrationBuilder.AddColumn<long>(
                name: "plano_id",
                table: "empresas",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "planos",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    nome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    preco = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    limite_conexoes = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)1),
                    limite_usuarios = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)3),
                    ativo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    ordem = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)1),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planos", x => x.id);
                    table.CheckConstraint("ck_planos_limite_conexoes", "limite_conexoes BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_planos_limite_usuarios", "limite_usuarios BETWEEN 1 AND 50");
                    table.CheckConstraint("ck_planos_preco", "preco >= 0");
                });

            migrationBuilder.AddForeignKey(
                name: "fk_empresas_plano",
                table: "empresas",
                column: "plano_id",
                principalTable: "planos",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // ⚠️ ÍNDICE FUNCIONAL, e por isso escrito à mão: o EF não sabe expressar `lower(nome)`,
            // e sem ele "Pro" e "pro" seriam dois planos. Mesmo arranjo de duas pontas do
            // `uq_usuarios_email` — o `HasIndex` no DbContext existe só para o snapshot não brigar.
            migrationBuilder.Sql("CREATE UNIQUE INDEX uq_planos_nome ON planos (lower(nome))");

            // O backfill ANTES do CHECK. Ver o comentário da constante.
            migrationBuilder.Sql(Backfill);

            migrationBuilder.AddCheckConstraint(
                name: "ck_empresas_limite_usuarios",
                table: "empresas",
                sql: "limite_usuarios BETWEEN 1 AND 50");
        }

        /// <summary>⚠️ ESTE É O ÚNICO `Down` DESTE SISTEMA QUE PERDE ESTADO COMERCIAL, e não só
        /// estrutura. Ele apaga o catálogo inteiro, qual plano cada empresa tinha, e o teto de
        /// pessoas de cada uma — inclusive os que o backfill calculou a partir do uso real e os que
        /// alguém ajustou à mão depois.
        ///
        /// Reverter e reaplicar não devolve: o backfill recalcula pelo uso do momento, que já pode
        /// ser outro, e `plano_id` volta como NULL para todo mundo.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS uq_planos_nome");

            migrationBuilder.DropForeignKey(
                name: "fk_empresas_plano",
                table: "empresas");

            migrationBuilder.DropTable(
                name: "planos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_empresas_limite_usuarios",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "limite_usuarios",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "plano_id",
                table: "empresas");
        }
    }
}
