using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class TextosDoCliente : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "nps_texto",
                table: "empresas",
                type: "text",
                nullable: false,
                defaultValue: "{{saudacao}} Aqui é da equipe {{empresa}}, obrigado pela compra! De 0 a 10, quanto você nos indicaria a um amigo? É só responder com o número.",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "{{saudacao}} Aqui é da {{empresa}}. De 0 a 10, quanto você recomendaria a gente para um amigo? É só responder com o número.");

            // BUG-XX (T6): o `AlterColumn` muda so o DEFAULT. As empresas que nunca editaram a pergunta
            // tem o texto antigo GRAVADO ("Aqui é da Salão Bela", sem citar a compra) e passam para o
            // novo. ⚠️ O `WHERE` com o texto antigo e o ponto: quem personalizou nao e tocado.
            migrationBuilder.Sql("""
                UPDATE empresas
                   SET nps_texto = '{{saudacao}} Aqui é da equipe {{empresa}}, obrigado pela compra! '
                                   || 'De 0 a 10, quanto você nos indicaria a um amigo? É só responder com o número.'
                 WHERE nps_texto = '{{saudacao}} Aqui é da {{empresa}}. De 0 a 10, quanto você '
                                   || 'recomendaria a gente para um amigo? É só responder com o número.';
                """);

            migrationBuilder.AddColumn<string>(
                name: "followup_texto",
                table: "empresas",
                type: "text",
                nullable: false,
                defaultValue: "{{saudacao}} Aqui é da equipe {{empresa}}. Ficou alguma dúvida sobre o que conversamos? Se quiser continuar, é só responder aqui.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE empresas
                   SET nps_texto = '{{saudacao}} Aqui é da {{empresa}}. De 0 a 10, quanto você '
                                   || 'recomendaria a gente para um amigo? É só responder com o número.'
                 WHERE nps_texto = '{{saudacao}} Aqui é da equipe {{empresa}}, obrigado pela compra! '
                                   || 'De 0 a 10, quanto você nos indicaria a um amigo? É só responder com o número.';
                """);

            migrationBuilder.DropColumn(
                name: "followup_texto",
                table: "empresas");

            migrationBuilder.AlterColumn<string>(
                name: "nps_texto",
                table: "empresas",
                type: "text",
                nullable: false,
                defaultValue: "{{saudacao}} Aqui é da {{empresa}}. De 0 a 10, quanto você recomendaria a gente para um amigo? É só responder com o número.",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "{{saudacao}} Aqui é da equipe {{empresa}}, obrigado pela compra! De 0 a 10, quanto você nos indicaria a um amigo? É só responder com o número.");
        }
    }
}
