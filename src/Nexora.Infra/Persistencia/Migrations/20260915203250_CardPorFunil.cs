using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class CardPorFunil : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // =================== A RECUSA, E ELA DISPAROU DE VERDADE ===================
            // Criar um indice unico sobre dado que ja viola falha com uma mensagem do Postgres
            // que nao diz QUANTOS nem QUEM. Quem opera precisa saber o tamanho do problema.
            //
            // ⚠️ ESTA GUARDA NAO E HIPOTETICA: ela recusou esta propria migracao no `nexora_dev`.
            // A regra anterior permitia `aberta` + `ganha` no mesmo funil, e havia um contato
            // exatamente assim — a venda de R$300 em "Venda" e uma negociacao nova em "Separado".
            // Foi resolvido concluindo o pedido, que tira o card do quadro e MANTEM o dinheiro
            // no faturamento.
            // ==========================================================================
            migrationBuilder.Sql("""
                DO $$
                DECLARE pares bigint;
                BEGIN
                    SELECT count(*) INTO pares FROM (
                        SELECT contato_id, pipeline_id
                          FROM negociacoes
                         WHERE status IN ('aberta', 'ganha')
                         GROUP BY contato_id, pipeline_id
                        HAVING count(*) > 1) AS x;

                    IF pares > 0 THEN
                        RAISE EXCEPTION
                            '% contato(s) aparecem DUAS VEZES no mesmo funil (aberta + ganha, ou '
                            'duas ganhas). Conclua o pedido pendente ou mova a negociacao para '
                            'outro funil antes desta migracao.', pares;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "uq_negociacoes_aberta_por_funil",
                table: "negociacoes");

            migrationBuilder.CreateIndex(
                name: "uq_negociacoes_card_por_funil",
                table: "negociacoes",
                columns: new[] { "empresa_id", "contato_id", "pipeline_id" },
                unique: true,
                filter: "status IN ('aberta', 'ganha')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_negociacoes_card_por_funil",
                table: "negociacoes");

            migrationBuilder.CreateIndex(
                name: "uq_negociacoes_aberta_por_funil",
                table: "negociacoes",
                columns: new[] { "empresa_id", "contato_id", "pipeline_id" },
                unique: true,
                filter: "status = 'aberta'");
        }
    }
}
