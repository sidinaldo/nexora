using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>POS-1 — o liga/desliga da conclusao automatica.
    ///
    /// ===================== SEM BACKFILL E SEM SQL CRU, DE PROPOSITO =====================
    /// `defaultValue: true` vira DEFAULT no DDL, e o Postgres preenche TODA linha existente ao
    /// adicionar a coluna. Nao ha `UPDATE empresas SET ...` a escrever.
    ///
    /// E isso tambem e a resposta para a armadilha do OPE-1: `migrationBuilder.Sql` sem `;` no fim
    /// passa em toda a suite (que usa `Migrate()`) e QUEBRA o script idempotente do deploy, que cola
    /// o texto dentro de um `DO $EF$ ... END IF; END $EF$;`. Aqui nao existe `SqlOperation` nenhuma,
    /// entao a armadilha nao tem onde disparar — e nao e sorte, e o motivo de preferir o DEFAULT a
    /// um UPDATE escrito a mao.
    ///
    /// LIGADO para todo mundo: e o comportamento que todos os clientes de hoje ja tem. Nascer
    /// `false` pararia a rodada diaria de todas as empresas, e ninguem notaria por semanas.
    /// ===================================================================================</summary>
    public partial class ConclusaoAutomaticaLigaDesliga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "conclusao_automatica",
                table: "empresas",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conclusao_automatica",
                table: "empresas");
        }
    }
}
