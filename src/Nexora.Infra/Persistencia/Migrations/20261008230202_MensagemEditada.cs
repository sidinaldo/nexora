using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>===================== A EDICAO DEIXA DE SER UM BALAO A MAIS =====================
    ///
    /// `editada_em` marca a mensagem que alguem editou no WhatsApp (ver `Mensagem.EditadaEm`).
    ///
    /// ⚠️ AS EDICOES QUE JA ENTRARAM viraram linha propria, com
    /// "[mensagem nao suportada: secretEncryptedMessage]". O conserto, em tres passos:
    ///
    ///   1. a ORIGINAL ganha `editada_em` (a hora da ultima edicao);
    ///   2. a CONVERSA que terminava em edicoes volta a mostrar a ultima mensagem de verdade, e
    ///      devolve as nao lidas e o semaforo que elas tinham somado. So quando as edicoes eram o
    ///      FIM da conversa: ai e certo que nada depois mexeu nesses numeros. Nas outras, ler ou
    ///      responder a conversa ja os acerta;
    ///   3. a linha da edicao SAI. Ela nunca foi conteudo — o codigo novo nem a cria.
    ///
    /// ⚠️ SO A EDICAO CUJA ORIGINAL ESTA NO BANCO. A de uma mensagem anterior ao Nexora criou, no
    /// codigo antigo, contato e conversa so para ela: apaga-la deixaria a conversa vazia, com o
    /// rotulo na previa e uma nao lida. Ela fica como estava. Linha citada por pesquisa de NPS
    /// tambem fica, por precaucao.
    ///
    /// Medido no `nexora_dev`: DUAS edicoes, as duas com a original no banco.
    ///
    /// `Down` so derruba a coluna: as linhas apagadas nao voltam, e nao ha por que voltarem.
    /// ==========================================================================================</summary>
    public partial class MensagemEditada : Migration
    {
        /// <summary>O `wa_message_id` da original, lido da edicao de alias `a`.</summary>
        private static string Alvo(string a) =>
            $"{a}.payload_raw->'data'->'message'->'secretEncryptedMessage'->'targetMessageKey'->>'id'";

        /// <summary>A edicao que este conserto resolve, sobre a linha de alias `a`. O rotulo no
        /// texto e a trava: so sai linha que nao tinha conteudo nenhum.</summary>
        private static string Resolvida(string a) => $"""
            {a}.payload_raw->'data'->>'messageType' = 'secretEncryptedMessage'
            AND {a}.payload_raw->'data'->'message'->'secretEncryptedMessage'->>'secretEncType' IN ('2', 'MESSAGE_EDIT')
            AND {a}.texto LIKE '[mensagem não suportada:%'
            AND EXISTS (SELECT 1 FROM mensagens orig
                         WHERE orig.empresa_id = {a}.empresa_id AND orig.wa_message_id = {Alvo(a)})
            AND NOT EXISTS (SELECT 1 FROM pesquisas_nps p
                             WHERE p.mensagem_envio_id = {a}.id OR p.mensagem_resposta_id = {a}.id)
            """;

        /// <summary>Público para o teste rodar o MESMO texto contra um banco com dados. A empresa
        /// recorta o teste: o banco de teste é compartilhado, e a migração de verdade roda uma
        /// vez, no banco inteiro.</summary>
        public static string SqlConsertar(long? empresaId = null)
        {
            var empresa = empresaId is { } id ? $"AND e.empresa_id = {id}" : "";

            return $"""
                UPDATE mensagens o
                   SET editada_em = x.quando
                  FROM (SELECT e.empresa_id, {Alvo("e")} AS alvo, max(e.criado_em) AS quando
                          FROM mensagens e
                         WHERE {Resolvida("e")} {empresa}
                         GROUP BY 1, 2) x
                 WHERE o.empresa_id = x.empresa_id
                   AND o.wa_message_id = x.alvo
                   AND (o.editada_em IS NULL OR o.editada_em < x.quando);

                UPDATE conversas c
                   SET ultima_mensagem_em = u.criado_em,
                       ultima_mensagem_direcao = u.direcao,
                       ultima_mensagem_previa = left(u.texto, 120),
                       nao_lidas = greatest(c.nao_lidas - d.entradas, 0),
                       -- Tudo o que esperava DEPOIS da ultima mensagem que fica foi aceso por edicao:
                       -- se ela e entrada, a espera comecou nela ou antes; se e saida, ela zerou.
                       aguardando_desde = CASE WHEN c.aguardando_desde > u.criado_em
                                               THEN NULL ELSE c.aguardando_desde END
                  FROM (SELECT DISTINCT e.conversa_id
                          FROM mensagens e
                         WHERE {Resolvida("e")} {empresa}) alvo
                 CROSS JOIN LATERAL (
                       -- A ultima que FICA e que a conversa mostraria. Automatica nunca vira previa:
                       -- o despacho dela nao mexe na conversa.
                       -- `coalesce`: o que sai do painel nao tem `payload_raw`, e NOT NULL e NULL.
                       SELECT m.criado_em, m.direcao, m.texto
                         FROM mensagens m
                        WHERE m.conversa_id = alvo.conversa_id
                          AND m.origem <> 'automatica'
                          AND NOT coalesce(({Resolvida("m")}), false)
                        ORDER BY m.criado_em DESC, m.id DESC
                        LIMIT 1) u
                 CROSS JOIN LATERAL (
                       -- As edicoes DEPOIS dela: foram essas que o codigo antigo contou.
                       SELECT max(e.criado_em) AS ultima,
                              count(*) FILTER (WHERE e.direcao = 'entrada') AS entradas
                         FROM mensagens e
                        WHERE e.conversa_id = alvo.conversa_id
                          AND e.criado_em > u.criado_em
                          AND {Resolvida("e")}) d
                 WHERE c.id = alvo.conversa_id
                   AND c.ultima_mensagem_em = d.ultima
                   AND c.ultima_mensagem_previa LIKE '[mensagem não suportada:%';

                DELETE FROM mensagens e
                 WHERE {Resolvida("e")} {empresa};
                """;
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "editada_em",
                table: "mensagens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(SqlConsertar());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "editada_em",
                table: "mensagens");
        }
    }
}
