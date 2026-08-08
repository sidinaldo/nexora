using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>A frase pré-preenchida do link do canal.
    ///
    /// Antes, o texto do QR era fixo — "Olá! Tenho interesse. #k7m2" para todo canal de toda
    /// empresa. Agora cada canal escreve a frase da campanha dele.
    ///
    /// ⚠️ O CÓDIGO NÃO MORA NESTA COLUNA. Ele é acrescentado por `CodigoCanal.TextoDoLink` na
    /// hora de montar o link, e não pode ser desligado: sem código não há atribuição, e canal
    /// que não atribui é o problema que o canal existe para resolver.
    ///
    /// NULA = usa a frase padrão. Nenhum canal existente muda de comportamento.
    ///
    /// A coluna tem 300 e o domínio limita em 120 (`CodigoCanal.LimiteMensagem`): a folga existe
    /// para uma mudança de regra de produto não virar migração.</summary>
    public partial class MensagemDoLinkDoCanal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mensagem_link",
                table: "canais_captacao",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mensagem_link",
                table: "canais_captacao");
        }
    }
}
