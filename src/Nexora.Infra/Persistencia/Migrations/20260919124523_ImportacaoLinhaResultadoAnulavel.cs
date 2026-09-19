using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ImportacaoLinhaResultadoAnulavel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "resultado",
                table: "importacao_linhas",
                type: "resultado_linha_enum",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "resultado_linha_enum");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ⚠️ O `Down` GERADO NÃO RODAVA: o EF escreveu `defaultValue: 0` para uma coluna de enum
            // nativo, e o Postgres recusa ("invalid input value for enum"). Um rollback quebrado
            // esperando o dia em que alguém precisar dele é pior que rollback nenhum.
            //
            // Voltar para NOT NULL exige um valor para as linhas que ainda não foram processadas, e
            // o esquema antigo não tinha como dizer "pendente". Elas viram `invalido` com o motivo
            // explícito — o dono vê o porquê em vez de um resultado inventado.
            migrationBuilder.Sql("""
                UPDATE importacao_linhas
                   SET resultado = 'invalido', motivo = COALESCE(motivo, 'nao_processada')
                 WHERE resultado IS NULL;

                ALTER TABLE importacao_linhas ALTER COLUMN resultado SET NOT NULL;
                """);
        }
    }
}
