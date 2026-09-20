using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ImportacaoOrigem : Migration
    {
        /// <inheritdoc />
        /// <remarks>⚠️ SQL CRU, e não `AddColumn`. O gerador escreve `defaultValue: 0` — o número do
        /// primeiro membro do enum —, e a coluna é um enum NATIVO do Postgres, que não aceita número
        /// como default. É a mesma armadilha que já derrubou o `Down` da migração da importação.
        ///
        /// O `DEFAULT` existe só para preencher as linhas que já estavam na tabela, e sai logo
        /// depois: quem grava sempre diz a origem, e um default no banco faria o EF trocar a escolha
        /// "Instagram" (o valor zero do enum) por `manual`, em silêncio — foi o que ele avisou
        /// quando o modelo tinha `HasDefaultValue`.</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE importacoes "
              + "ADD COLUMN origem origem_lead_enum NOT NULL DEFAULT 'manual';");

            migrationBuilder.Sql("ALTER TABLE importacoes ALTER COLUMN origem DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE importacoes DROP COLUMN origem;");
        }
    }
}
