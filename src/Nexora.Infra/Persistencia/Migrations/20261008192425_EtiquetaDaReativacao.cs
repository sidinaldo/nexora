using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>===================== DE ONDE VEIO A ETIQUETA DO NEGÓCIO =====================
    ///
    /// `da_reativacao` diz se a etiqueta foi colada pela CAMPANHA de Leads parados (a etiqueta em
    /// lote) ou à mão no card. O resultado da reativação passa a contar só a primeira: contava as
    /// duas, e a tela mostrava "1 reativado, R$ 345" de um negócio que nunca esteve parado.
    ///
    /// ⚠️ AS ETIQUETAS QUE JÁ EXISTEM ganham a origem pelo histórico. A etiqueta em lote grava, na
    /// mesma gravação, um "Editou" do CONTATO com alterações vazias (`{}`); a edição normal do
    /// contato grava os campos que mudaram, e a etiqueta do card não grava nada. A janela de cinco
    /// segundos cobre a diferença entre o relógio do serviço e o `now()` do banco.
    /// ==========================================================================================</summary>
    public partial class EtiquetaDaReativacao : Migration
    {
        /// <summary>Público para o teste rodar o MESMO texto contra um banco com dados.</summary>
        public const string SqlRecuperarOrigem = """
            UPDATE negociacoes_etiquetas ne
               SET da_reativacao = true
              FROM negociacoes n
             WHERE n.id = ne.negociacao_id
               AND n.empresa_id = ne.empresa_id
               AND EXISTS (
                     SELECT 1
                       FROM auditoria a
                      WHERE a.empresa_id = ne.empresa_id
                        AND a.entidade = 'Contato'
                        AND a.acao = 'Editou'
                        AND a.entidade_id = n.contato_id
                        AND a.alteracoes::text = '{}'
                        AND a.quando BETWEEN ne.criado_em - interval '5 seconds'
                                         AND ne.criado_em + interval '5 seconds');
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "da_reativacao",
                table: "negociacoes_etiquetas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(SqlRecuperarOrigem);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "da_reativacao",
                table: "negociacoes_etiquetas");
        }
    }
}
