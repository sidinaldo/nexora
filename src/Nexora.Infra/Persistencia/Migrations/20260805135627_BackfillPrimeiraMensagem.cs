using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>Preenche `empresas.primeira_mensagem_em` para quem já recebia mensagem ANTES de a
    /// coluna existir.
    ///
    /// A migration anterior criou a coluna vazia, e o webhook só carimba dali em diante — então
    /// toda empresa em operação ficaria sem a métrica de tempo até o valor, justamente as que têm
    /// história para contar. O dado já está em `mensagens`; é só trazê-lo.
    ///
    /// `recebida_em` primeiro, `criado_em` como reserva: `recebida_em` é o instante que veio da
    /// Evolution (quando o cliente mandou de fato) e `criado_em` é quando a linha entrou aqui.
    /// Para mensagem de entrada os dois praticamente coincidem, mas o primeiro é o que o webhook
    /// grava hoje — o backfill tem que produzir o MESMO valor que o caminho normal produziria.
    ///
    /// Sem `WHERE primeira_mensagem_em IS NULL` isto seria destrutivo: reexecutar sobrescreveria
    /// o carimbo que o webhook já fez. Com ele, rodar de novo é no-op.</summary>
    public partial class BackfillPrimeiraMensagem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE empresas e
                   SET primeira_mensagem_em = m.primeira
                  FROM (
                        SELECT empresa_id,
                               MIN(COALESCE(recebida_em, criado_em)) AS primeira
                          FROM mensagens
                         WHERE direcao = 'entrada'
                         GROUP BY empresa_id
                       ) m
                 WHERE e.id = m.empresa_id
                   AND e.primeira_mensagem_em IS NULL;
                """);
        }

        /// <summary>Sem volta. Desfazer significaria zerar a coluna de todo mundo, inclusive o que
        /// o webhook carimbou depois — e não há como distinguir um do outro. A coluna some inteira
        /// se alguém reverter a migration anterior, que é o lugar certo para isso.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
