using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>UF da empresa (para semear feriados estaduais) e o token de concorrência do card.
    ///
    /// ===================== O QUE FOI REMOVIDO DESTA MIGRATION À MÃO =====================
    /// O `dotnet ef migrations add` gerou também um `AddColumn&lt;uint&gt;("xmin", "contatos")`,
    /// e ele foi APAGADO de propósito.
    ///
    /// `xmin` é coluna de SISTEMA do PostgreSQL — existe em toda tabela desde sempre e guarda a
    /// transação que escreveu a linha por último. Tentar criá-la falha com
    /// `column name "xmin" conflicts with a system column name`. O mapeamento em
    /// `NexoraDbContext` (HasColumnName("xmin"), tipo `xid`, IsConcurrencyToken) é só uma LEITURA
    /// do que o banco já mantém; não há nada a criar.
    ///
    /// Quem regenerar esta migration vai ver o AddColumn voltar. Ele não deve entrar.
    /// ====================================================================================</summary>
    public partial class UfEConcorrencia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "uf",
                table: "empresas",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "uf",
                table: "empresas");
        }
    }
}
