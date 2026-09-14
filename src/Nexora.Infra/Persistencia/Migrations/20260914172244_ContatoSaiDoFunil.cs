using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ContatoSaiDoFunil : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // =================== O BACKFILL VEM ANTES, E ELE NAO E FORMALIDADE ===================
            // Encontrado medindo o `nexora_dev` antes de escrever esta migracao: 1015 contatos e
            // ZERO negociacoes. Sem as linhas abaixo, este arquivo apagaria a posicao de funil dos
            // 1015 e o quadro abriria vazio — sem erro nenhum, porque as colunas simplesmente nao
            // existiriam mais para ninguem reclamar.
            //
            // O E4b ja tinha criado negociacao para todo contato. O que produz orfao depois disso e
            // apagar negociacao sem apagar o contato — foi o que aconteceu aqui, ao limpar os dados
            // de lead. Em producao a causa seria outra; o efeito e o mesmo, e e irreversivel.
            //
            // ⚠️ EF NAO GERA BACKFILL. Ele compara dois modelos e emite DDL; dado nao entra nessa
            // conta. Toda migracao deste projeto que precisou mover dado precisou de SQL escrito a
            // mao — e e por isso que `migrations remove` aqui exige apagar o arquivo na unha.
            // ====================================================================================

            // ---------- 1. A RECUSA, ANTES DE QUALQUER ESCRITA ----------
            // `ck_negociacoes_valor` exige valor > 0 em negociacao ganha, de proposito: ganha sem
            // valor entra no faturamento como zero e o dono so nota fechando o mes.
            //
            // Um contato orfao COM `ganho_em` e SEM valor positivo nao tem traducao honesta. As
            // alternativas seriam inventar um numero (receita falsa) ou rebaixa-lo para aberto
            // (venda apagada). As duas mentem, e em silencio. Entao a migracao PARA e diz quantos
            // sao — quem opera decide, com o dado ainda na mao.
            migrationBuilder.Sql("""
                DO $$
                DECLARE quantos bigint;
                BEGIN
                    SELECT count(*) INTO quantos
                      FROM contatos c
                     WHERE c.ganho_em IS NOT NULL
                       AND (c.valor IS NULL OR c.valor <= 0)
                       AND NOT EXISTS (SELECT 1 FROM negociacoes n WHERE n.contato_id = c.id);

                    IF quantos > 0 THEN
                        RAISE EXCEPTION
                            'E4e/4: % contato(s) tem ganho_em sem valor positivo e nenhuma '
                            'negociacao. Converte-los inventaria faturamento ou apagaria a venda. '
                            'Resolva antes: preencha contatos.valor ou limpe ganho_em.', quantos;
                    END IF;
                END $$;
                """);

            // ---------- 2. A NEGOCIACAO QUE FALTA ----------
            // O status sai do CARIMBO, na mesma precedencia que `ck_contatos_terminal` garantia:
            // perdido e ganho nunca coexistiam, entao a ordem dos ramos nao muda resultado — ela
            // so torna a regra legivel.
            //
            // `criado_em` copia o do contato e NAO usa o default `now()`: o quadro e os relatorios
            // contam a idade do negocio por ele, e carimbar tudo com hoje faria uma base inteira
            // parecer criada no dia da migracao.
            migrationBuilder.Sql("""
                INSERT INTO negociacoes (
                    empresa_id, contato_id, pipeline_id, etapa_id, ordem_kanban,
                    responsavel_id, valor, status, ganha_em, perdida_em, motivo_perda,
                    criado_em, atualizado_em)
                SELECT c.empresa_id, c.id, e.pipeline_id, c.etapa_id, c.ordem_kanban,
                       c.responsavel_id, c.valor,
                       CASE WHEN c.perdido_em IS NOT NULL THEN 'perdida'
                            WHEN c.ganho_em   IS NOT NULL THEN 'ganha'
                            ELSE 'aberta' END::status_negociacao_enum,
                       c.ganho_em, c.perdido_em, c.motivo_perda,
                       c.criado_em, c.criado_em
                  FROM contatos c
                  JOIN etapas_funil e ON e.id = c.etapa_id
                 WHERE NOT EXISTS (SELECT 1 FROM negociacoes n WHERE n.contato_id = c.id);
                """);

            // ---------- 3. A CONFERENCIA, DEPOIS ----------
            // ⚠️ SEM ISTO O BACKFILL SERIA UMA ESPERANCA. Um `JOIN` que nao casasse, um filtro
            // errado, e a migracao seguiria feliz derrubando as colunas — e so o quadro vazio
            // contaria, dias depois. A checagem custa um `count` e transforma um dado perdido para
            // sempre num erro que aborta a transacao da migracao.
            migrationBuilder.Sql("""
                DO $$
                DECLARE sobraram bigint;
                BEGIN
                    SELECT count(*) INTO sobraram
                      FROM contatos c
                     WHERE NOT EXISTS (SELECT 1 FROM negociacoes n WHERE n.contato_id = c.id);

                    IF sobraram > 0 THEN
                        RAISE EXCEPTION
                            'E4e/4: % contato(s) continuam sem negociacao depois do backfill. '
                            'Derrubar contatos.etapa_id agora apagaria a posicao deles para '
                            'sempre.', sobraram;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_contatos_etapa",
                table: "contatos");

            migrationBuilder.DropIndex(
                name: "ix_contatos_ganho",
                table: "contatos");

            migrationBuilder.DropIndex(
                name: "ix_contatos_kanban",
                table: "contatos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_contatos_terminal",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "etapa_id",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "ganho_em",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "motivo_perda",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "ordem_kanban",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "perdido_em",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "valor",
                table: "contatos");
        }

        /// <inheritdoc />
        /// <summary>⚠️ O `Down` RECRIA AS COLUNAS VAZIAS, E ISSO NAO E UMA VOLTA ATRAS.
        ///
        /// EF sabe recriar a forma; o dado que estava nelas foi para `negociacoes` e ninguem o
        /// traz de volta daqui. Um contato voltaria com `etapa_id = 0`, que nem existe — a FK
        /// recriada logo abaixo recusaria, e a reversao falharia no meio.
        ///
        /// Isto e deliberado e esta escrito aqui para quem tentar: a volta atras de verdade e
        /// restaurar o backup de antes da migracao. Reverter uma migracao que MOVE dado nunca e
        /// simetrico, e fingir que e seria a forma mais cara de descobrir.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "etapa_id",
                table: "contatos",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "ganho_em",
                table: "contatos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "motivo_perda",
                table: "contatos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ordem_kanban",
                table: "contatos",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "perdido_em",
                table: "contatos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "valor",
                table: "contatos",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_contatos_ganho",
                table: "contatos",
                columns: new[] { "empresa_id", "ganho_em" },
                descending: new[] { false, true },
                filter: "ganho_em IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_contatos_kanban",
                table: "contatos",
                columns: new[] { "empresa_id", "etapa_id", "ordem_kanban" },
                filter: "perdido_em IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_contatos_terminal",
                table: "contatos",
                sql: "ganho_em IS NULL OR perdido_em IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_contatos_etapa",
                table: "contatos",
                columns: new[] { "etapa_id", "empresa_id" },
                principalTable: "etapas_funil",
                principalColumns: new[] { "id", "empresa_id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
