using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>===================== A EDICAO PASSA A TROCAR O TEXTO =====================
    ///
    /// `contatos.lid` guarda o identificador que abre as edicoes da pessoa, e
    /// `mensagens.texto_original` guarda o texto de antes da troca (ver `EdicaoMensagem`).
    ///
    /// SEM BACKFILL. O LID chega na proxima confirmacao de entrega de cada contato. E as edicoes
    /// ja recebidas nao tem como ser abertas: o conteudo cifrado delas estava na linha que a
    /// `MensagemEditada` apagou, ou nunca foi guardado.
    /// ===========================================================================</summary>
    public partial class EdicaoComTexto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "texto_original",
                table: "mensagens",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lid",
                table: "contatos",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "texto_original",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "lid",
                table: "contatos");
        }
    }
}
