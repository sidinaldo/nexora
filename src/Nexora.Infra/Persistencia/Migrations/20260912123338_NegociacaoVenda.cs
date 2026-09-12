using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class NegociacaoVenda : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "venda_id",
                table: "negociacoes",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "uq_negociacoes_venda",
                table: "negociacoes",
                column: "venda_id",
                unique: true,
                filter: "venda_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_negociacoes_venda",
                table: "negociacoes",
                column: "venda_id",
                principalTable: "vendas",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // ============================================================================
            //                 O ELO, REFEITO EM VEZ DE ADIVINHADO
            //
            // O backfill do E4a criou uma negociacao por venda mas nao guardou QUAL venda. Nao
            // ha regra que recupere isso depois: casar por (contato, valor, data) e casar por
            // TIMESTAMP, e o proprio `ServicoVendas.CancelarAsync` registra que essa tentativa ja
            // derrubou um teste — duas vendas no mesmo instante casavam as duas.
            //
            // Como NADA le nem escreve `negociacoes` ainda, a saida exata e refazer: apagar as
            // linhas que vieram de venda e reinserir com o elo. A fonte da verdade (`vendas`)
            // esta intacta, entao nao se perde nada — e o resultado e o mesmo que o E4a teria
            // produzido se a coluna existisse desde o inicio.
            //
            // ⚠️ As negociacoes ABERTAS e PERDIDAS nao sao tocadas: elas vieram de `contatos`,
            // nao de venda, e sao as que carregam a posicao viva no quadro.
            // ============================================================================
            migrationBuilder.Sql("""
                DELETE FROM negociacoes WHERE status IN ('ganha', 'concluida', 'cancelada');
                """);

            migrationBuilder.Sql("""
                INSERT INTO negociacoes (
                    empresa_id, contato_id, pipeline_id, etapa_id, valor, ordem_kanban,
                    responsavel_id, status, ganha_em, concluida_em, concluida_por,
                    cancelada_em, cancelada_por, observacao, canal_ciclo_id, venda_id,
                    criado_em, atualizado_em)
                SELECT
                    v.empresa_id, v.contato_id, e.pipeline_id, v.etapa_id, v.valor,
                    CASE WHEN v.status = 'fechada' THEN c.ordem_kanban ELSE 0 END,
                    v.responsavel_id,
                    (CASE v.status
                        WHEN 'fechada'   THEN 'ganha'
                        WHEN 'concluida' THEN 'concluida'
                        ELSE                  'cancelada'
                     END)::status_negociacao_enum,
                    v.fechada_em, v.concluida_em, v.concluida_por,
                    v.cancelada_em, v.cancelada_por, v.observacao, v.canal_id, v.id,
                    v.criado_em, v.criado_em
                FROM vendas v
                JOIN contatos c     ON c.id = v.contato_id
                JOIN etapas_funil e ON e.id = v.etapa_id;
                """);

            // As mesmas travas do E4a, mais a do elo. Se qualquer uma falhar, a transacao inteira
            // volta e o banco fica como estava.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    orfaos      bigint;
                    sem_elo     bigint;
                    de_vendas   numeric;
                    de_negocios numeric;
                BEGIN
                    SELECT count(*) INTO orfaos FROM contatos c
                    WHERE NOT EXISTS (SELECT 1 FROM negociacoes n WHERE n.contato_id = c.id);

                    IF orfaos > 0 THEN
                        RAISE EXCEPTION
                            'Elo da venda: % contato(s) ficaram sem nenhuma negociacao.', orfaos;
                    END IF;

                    SELECT count(*) INTO sem_elo FROM vendas v
                    WHERE NOT EXISTS (SELECT 1 FROM negociacoes n WHERE n.venda_id = v.id);

                    IF sem_elo > 0 THEN
                        RAISE EXCEPTION
                            'Elo da venda: % venda(s) ficaram sem negociacao espelho.', sem_elo;
                    END IF;

                    SELECT coalesce(sum(valor), 0) INTO de_vendas
                      FROM vendas WHERE status <> 'cancelada';
                    SELECT coalesce(sum(valor), 0) INTO de_negocios
                      FROM negociacoes WHERE status IN ('ganha', 'concluida');

                    IF de_vendas <> de_negocios THEN
                        RAISE EXCEPTION
                            'Elo da venda: faturamento mudou de % para %.', de_vendas, de_negocios;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_negociacoes_venda",
                table: "negociacoes");

            migrationBuilder.DropIndex(
                name: "uq_negociacoes_venda",
                table: "negociacoes");

            migrationBuilder.DropColumn(
                name: "venda_id",
                table: "negociacoes");
        }
    }
}
