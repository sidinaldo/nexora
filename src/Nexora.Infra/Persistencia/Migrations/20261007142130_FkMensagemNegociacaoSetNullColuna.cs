using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>===================== O MESMO DEFEITO, NA OUTRA FK (revisao NPS-1) =====================
    ///
    /// ⚠️ `fk_msg_negociacao` E COMPOSTA — `(negociacao_id, empresa_id)` — e nasceu com `ON DELETE
    /// SET NULL` sem lista de colunas, gerado pelo `OnDelete(SetNull)` do EF. O Postgres entao zera
    /// TODAS as colunas da chave, inclusive `mensagens.empresa_id`, que e NOT NULL: apagar uma
    /// negociacao referenciada por mensagem estoura a restricao em vez de soltar o vinculo.
    ///
    /// E o defeito que a migration `PesquisaNps` ja tinha corrigido a mao em `pesquisas_nps`, e eu
    /// nao olhei a FK vizinha que eu mesmo tinha criado um dia antes. Hoje nenhum caminho apaga
    /// negociacao com mensagem — os expurgos apagam as mensagens primeiro —, entao nada quebrou
    /// ainda. E uma armadilha armada para o primeiro que fizer.
    ///
    /// `SET NULL (negociacao_id)`, do Postgres 15+: zera so o ponteiro e preserva o tenant. O EF nao
    /// sabe gerar, entao o modelo nao muda e a troca e por SQL. ⚠️ Quem recriar esta FK pelo EF um
    /// dia tem de reaplicar isto.
    /// ==========================================================================================</summary>
    public partial class FkMensagemNegociacaoSetNullColuna : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE mensagens DROP CONSTRAINT fk_msg_negociacao;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE mensagens ADD CONSTRAINT fk_msg_negociacao
                    FOREIGN KEY (negociacao_id, empresa_id)
                    REFERENCES negociacoes (id, empresa_id)
                    ON DELETE SET NULL (negociacao_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volta ao estado anterior, com o defeito — e o que `Down` deve fazer: desfazer esta
            // migration, nao consertar outra coisa.
            migrationBuilder.Sql("""
                ALTER TABLE mensagens DROP CONSTRAINT fk_msg_negociacao;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE mensagens ADD CONSTRAINT fk_msg_negociacao
                    FOREIGN KEY (negociacao_id, empresa_id)
                    REFERENCES negociacoes (id, empresa_id)
                    ON DELETE SET NULL;
                """);
        }
    }
}
