using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class EtiquetaCriadoPor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===================== POR QUE A COLUNA E ANULAVEL =====================
            // Nao e "pode faltar": e que EXISTE etiqueta sem criador conhecido. As que ja estao
            // no banco nasceram antes desta coluna, e as da semente de desenvolvimento nascem
            // fora de requisicao autenticada — `IContextoEmpresa.UsuarioId` devolve 0 ali, e o
            // servico grava null em vez de uma FK apontando para usuario inexistente.
            //
            // NOT NULL com default exigiria inventar um criador para as linhas antigas, o que e
            // pior que admitir que nao se sabe.
            // =======================================================================
            migrationBuilder.AddColumn<long>(
                name: "criado_por",
                table: "etiquetas",
                type: "bigint",
                nullable: true);

            // FK COMPOSTA com empresa_id, como toda relacao de tenant do projeto: sem ela, um bug
            // gravaria como criador um usuario de OUTRA empresa e o banco aceitaria.
            //
            // SEM INDICE em `criado_por`, de proposito. A convencao de indice automatico de FK e
            // removida no DbContext, entao ele so existe se for declarado — e nenhuma consulta
            // filtra por criador. O unico custo e a verificacao de RESTRICT ao apagar um usuario,
            // que varre uma tabela de no maximo 60 linhas por empresa.
            migrationBuilder.AddForeignKey(
                name: "fk_etiquetas_criado_por",
                table: "etiquetas",
                columns: new[] { "criado_por", "empresa_id" },
                principalTable: "usuarios",
                principalColumns: new[] { "id", "empresa_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_etiquetas_criado_por",
                table: "etiquetas");

            migrationBuilder.DropColumn(
                name: "criado_por",
                table: "etiquetas");
        }
    }
}
