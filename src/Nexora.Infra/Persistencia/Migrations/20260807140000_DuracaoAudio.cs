using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>A duracao da nota de voz (bloco 13).
    ///
    /// ESCRITA A MAO, e nao gerada: a `dotnet ef` precisa compilar o projeto da API, e o `bin/`
    /// dela estava travado por uma instancia rodando no Visual Studio. Uma coluna anulavel, sem
    /// transformacao de dado, e o caso em que escrever a mao e seguro — e derrubar o processo de
    /// outra pessoa para gerar seis linhas nao e.
    ///
    /// O snapshot do modelo foi ajustado junto, senao a proxima migration gerada recriaria a
    /// coluna.</summary>
    public partial class DuracaoAudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "midia_duracao_segundos",
                table: "mensagens",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "midia_duracao_segundos",
                table: "mensagens");
        }
    }
}
