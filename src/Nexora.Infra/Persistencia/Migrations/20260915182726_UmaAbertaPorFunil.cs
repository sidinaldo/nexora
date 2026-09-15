using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class UmaAbertaPorFunil : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // =================== A RECUSA ANTES DO INDICE ===================
            // Criar um indice unico sobre dado que ja viola falha com uma mensagem do Postgres
            // ("could not create unique index ... Key ... is duplicated"), que nao diz QUANTOS
            // nem QUEM. Quem opera a migracao precisa saber o tamanho do problema antes de
            // decidir o que fazer com ele.
            //
            // Medido no `nexora_dev` antes de escrever: zero violacoes.
            // ===============================================================
            migrationBuilder.Sql("""
                DO $$
                DECLARE pares bigint;
                BEGIN
                    SELECT count(*) INTO pares FROM (
                        SELECT contato_id, pipeline_id
                          FROM negociacoes
                         WHERE status = 'aberta'
                         GROUP BY contato_id, pipeline_id
                        HAVING count(*) > 1) AS x;

                    IF pares > 0 THEN
                        RAISE EXCEPTION
                            '% contato(s) tem MAIS DE UMA negociacao ABERTA no mesmo funil. '
                            'Encerre ou mova as duplicadas antes desta migracao — a consulta que '
                            'as lista esta no corpo deste bloco.', pares;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "uq_negociacoes_aberta_por_funil",
                table: "negociacoes",
                columns: new[] { "empresa_id", "contato_id", "pipeline_id" },
                unique: true,
                filter: "status = 'aberta'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_negociacoes_aberta_por_funil",
                table: "negociacoes");
        }
    }
}
