using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>===================== AS MENSAGENS QUE JÁ CHEGARAM INVISÍVEIS (REC-2) =====================
    ///
    /// Só dados: nenhuma coluna muda. O `ConteudoLegivel` conserta o que chega de agora em diante;
    /// esta migração conserta o que já está no banco.
    ///
    /// Sem ela, o cliente continua abrindo a conversa e vendo o balão branco que motivou o bloco —
    /// e o pior tipo de conserto é o que não vale para o caso que a pessoa reportou.
    ///
    /// Em `nexora_dev` eram 8 linhas em 1.984 entradas (3 templates, 2 reações, 2 imagens e 1
    /// áudio cujo download falhou). Em produção a proporção é a mesma; o custo é um UPDATE
    /// indexado sobre um recorte pequeno.
    ///
    /// ⚠️ NENHUMA LINHA É APAGADA, nem as de reação — que de agora em diante nem chegam a existir.
    /// Mensagem gravada não some deste sistema; ela ganha explicação.
    ///
    /// ⚠️ E ela tem `Designer.cs` com o atributo `[Migration]`. Sem ele o EF NÃO ENXERGA a
    /// migração: `database update` responde "não encontrada" e um banco criado do zero pula o
    /// arquivo inteiro, em silêncio. Foi o que aconteceu com a `DuracaoAudio` do bloco 13.</summary>
    public partial class ConteudoLegivelBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A mesma ordem de decisão do `ConteudoLegivel`, em SQL:
            //
            //   1. template  -> o texto de verdade, mais o botão (o link é parte da mensagem);
            //   2. mídia     -> "[anexo não recebido]", porque o tipo ERA suportado e o download
            //                   falhou — chamar isso de "não suportado" mandaria a próxima pessoa
            //                   investigar o lado errado;
            //   3. o resto   -> o rótulo com o nome do tipo.
            //
            // O recorte é estreito de propósito: só ENTRADA, só o que está vazio nas duas pontas,
            // e só onde há payload para ler. Mensagem que já tem texto não é tocada.
            migrationBuilder.Sql("""
                WITH alvo AS (
                    SELECT m.id,
                           m.payload_raw::jsonb -> 'data' ->> 'messageType' AS tipo,
                           m.payload_raw::jsonb -> 'data' -> 'message'
                             -> 'templateMessage' -> 'hydratedTemplate' AS tpl
                      FROM mensagens m
                     WHERE m.direcao = 'entrada'
                       AND coalesce(m.texto, '') = ''
                       AND m.tipo_midia = 'nenhum'
                       AND m.payload_raw IS NOT NULL
                ),
                texto AS (
                    SELECT a.id,
                           CASE
                             WHEN a.tpl ->> 'hydratedContentText' IS NOT NULL THEN
                               a.tpl ->> 'hydratedContentText'
                               || coalesce(
                                    E'\n\n[' || (a.tpl -> 'hydratedButtons' -> 0 -> 'urlButton' ->> 'displayText')
                                    || '] '  || (a.tpl -> 'hydratedButtons' -> 0 -> 'urlButton' ->> 'url'),
                                    '')

                             WHEN a.tipo ~* '(image|audio|document|video|sticker)' THEN
                               '[anexo não recebido]'

                             ELSE '[mensagem não suportada: ' || coalesce(a.tipo, 'desconhecido') || ']'
                           END AS novo
                      FROM alvo a
                )
                UPDATE mensagens m
                   SET texto = t.novo
                  FROM texto t
                 WHERE m.id = t.id
                   AND coalesce(t.novo, '') <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SEM VOLTA, e é o comportamento certo: reverter significaria apagar o texto de
            // mensagens que voltariam a ser balões brancos. O estado anterior era o defeito.
        }
    }
}
