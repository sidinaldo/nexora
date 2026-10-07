using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class EnvioDaPesquisa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "nps_texto",
                table: "empresas",
                type: "text",
                nullable: false,
                defaultValue: "{{saudacao}} Aqui é da {{empresa}}. De 0 a 10, quanto você recomendaria a gente para um amigo? É só responder com o número.",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "Oi, {{nome}}! Aqui é da {{empresa}}. De 0 a 10, quanto você recomendaria a gente para um amigo? É só responder com o número.");

            // ===================== TROCAR O DEFAULT NAO MUDA AS LINHAS =====================
            //
            // ⚠️ O `AlterColumn` acima muda so o DEFAULT da coluna. As empresas criadas pela
            // migration anterior ja tem `"Oi, {{nome}}! ..."` GRAVADO, e ficariam com o texto que
            // produz "Oi, ! Aqui é da..." para contato cujo nome e um telefone formatado.
            //
            // ⚠️ O `WHERE` COM O TEXTO ANTIGO E O PONTO: quem personalizou nao e tocado. Um UPDATE
            // sem ele sobrescreveria a pergunta que o dono escreveu — e a pesquisa nasce
            // desligada, entao todo mundo que tem o texto antigo o tem por nao ter mexido nele.
            // ============================================================================
            migrationBuilder.Sql("""
                UPDATE empresas
                   SET nps_texto = '{{saudacao}} Aqui é da {{empresa}}. De 0 a 10, quanto você '
                                   || 'recomendaria a gente para um amigo? É só responder com o número.'
                 WHERE nps_texto = 'Oi, {{nome}}! Aqui é da {{empresa}}. De 0 a 10, quanto você '
                                   || 'recomendaria a gente para um amigo? É só responder com o número.';
                """);

            migrationBuilder.CreateIndex(
                name: "uq_msg_nps",
                table: "mensagens",
                column: "negociacao_id",
                unique: true,
                filter: "tipo_automacao = 'nps'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_msg_nps",
                table: "mensagens");

            migrationBuilder.AlterColumn<string>(
                name: "nps_texto",
                table: "empresas",
                type: "text",
                nullable: false,
                defaultValue: "Oi, {{nome}}! Aqui é da {{empresa}}. De 0 a 10, quanto você recomendaria a gente para um amigo? É só responder com o número.",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "{{saudacao}} Aqui é da {{empresa}}. De 0 a 10, quanto você recomendaria a gente para um amigo? É só responder com o número.");
        }
    }
}
