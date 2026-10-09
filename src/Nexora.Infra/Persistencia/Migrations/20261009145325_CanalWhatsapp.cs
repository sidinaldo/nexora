using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>===================== EVOLUTION OU CLOUD API, POR CONEXAO (INT-XX) =====================
    ///
    /// `conexoes.canal` e os campos da Cloud API (os segredos, cifrados), `empresas.canal_padrao`,
    /// `conversas.ultima_entrada_em` (a janela de 24h) e `contatos.wa_id` (o numero exato da Meta).
    ///
    /// Toda conexao que ja existe e Evolution, pelo default da coluna. A `ultima_entrada_em` das
    /// conversas que ja existem sai do historico — so a entrada pela conexao da propria conversa
    /// conta, a mesma regra da `RecepcaoMensagem`.
    /// ==========================================================================================</summary>
    public partial class CanalWhatsapp : Migration
    {
        /// <summary>Publico para o teste rodar o MESMO texto contra um banco com dados.</summary>
        public const string SqlUltimaEntrada = """
            UPDATE conversas c
               SET ultima_entrada_em = s.ultima
              FROM (SELECT m.conversa_id, max(m.recebida_em) AS ultima
                      FROM mensagens m
                      JOIN conversas c2 ON c2.id = m.conversa_id AND c2.conexao_id = m.conexao_id
                     WHERE m.direcao = 'entrada' AND m.recebida_em IS NOT NULL
                     GROUP BY m.conversa_id) s
             WHERE c.id = s.conversa_id;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:canal_whatsapp_enum", "evolution,cloud_api")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .Annotation("Npgsql:Enum:origem_mensagem_enum", "humana,automatica")
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:plataforma_conversao_enum", "meta")
                .Annotation("Npgsql:Enum:resultado_linha_enum", "importado,duplicado,invalido")
                .Annotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .Annotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .Annotation("Npgsql:Enum:status_conversao_enum", "pendente,entregue,falhou,expirado,cancelado")
                .Annotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .Annotation("Npgsql:Enum:status_importacao_enum", "aguardando_mapeamento,processando,concluida,erro")
                .Annotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .Annotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .Annotation("Npgsql:Enum:status_pesquisa_nps_enum", "agendada,enviada,respondida,possivel_nota,expirada,cancelada")
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .Annotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .Annotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video")
                .OldAnnotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .OldAnnotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:origem_mensagem_enum", "humana,automatica")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:plataforma_conversao_enum", "meta")
                .OldAnnotation("Npgsql:Enum:resultado_linha_enum", "importado,duplicado,invalido")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_conversao_enum", "pendente,entregue,falhou,expirado,cancelado")
                .OldAnnotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .OldAnnotation("Npgsql:Enum:status_importacao_enum", "aguardando_mapeamento,processando,concluida,erro")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .OldAnnotation("Npgsql:Enum:status_pesquisa_nps_enum", "agendada,enviada,respondida,possivel_nota,expirada,cancelada")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .OldAnnotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.AddColumn<int>(
                name: "canal_padrao",
                table: "empresas",
                type: "canal_whatsapp_enum",
                nullable: false,
                defaultValueSql: "'evolution'");

            migrationBuilder.AddColumn<DateTime>(
                name: "ultima_entrada_em",
                table: "conversas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "wa_id",
                table: "contatos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "access_token_cifrado",
                table: "conexoes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "app_secret_cifrado",
                table: "conexoes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "canal",
                table: "conexoes",
                type: "canal_whatsapp_enum",
                nullable: false,
                defaultValueSql: "'evolution'");

            migrationBuilder.AddColumn<string>(
                name: "phone_number_id",
                table: "conexoes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verify_token",
                table: "conexoes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "waba_id",
                table: "conexoes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "webhook_verificado_em",
                table: "conexoes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "uq_conexoes_phone_number_id",
                table: "conexoes",
                column: "phone_number_id",
                unique: true,
                filter: "phone_number_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_conexoes_verify_token",
                table: "conexoes",
                column: "verify_token",
                unique: true,
                filter: "verify_token IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conexoes_cloud_api",
                table: "conexoes",
                sql: "canal = 'evolution' OR (phone_number_id IS NOT NULL AND waba_id IS NOT NULL AND access_token_cifrado IS NOT NULL AND app_secret_cifrado IS NOT NULL)");

            migrationBuilder.Sql(SqlUltimaEntrada);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_conexoes_phone_number_id",
                table: "conexoes");

            migrationBuilder.DropIndex(
                name: "uq_conexoes_verify_token",
                table: "conexoes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conexoes_cloud_api",
                table: "conexoes");

            migrationBuilder.DropColumn(
                name: "canal_padrao",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "ultima_entrada_em",
                table: "conversas");

            migrationBuilder.DropColumn(
                name: "wa_id",
                table: "contatos");

            migrationBuilder.DropColumn(
                name: "access_token_cifrado",
                table: "conexoes");

            migrationBuilder.DropColumn(
                name: "app_secret_cifrado",
                table: "conexoes");

            migrationBuilder.DropColumn(
                name: "canal",
                table: "conexoes");

            migrationBuilder.DropColumn(
                name: "phone_number_id",
                table: "conexoes");

            migrationBuilder.DropColumn(
                name: "verify_token",
                table: "conexoes");

            migrationBuilder.DropColumn(
                name: "waba_id",
                table: "conexoes");

            migrationBuilder.DropColumn(
                name: "webhook_verificado_em",
                table: "conexoes");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .Annotation("Npgsql:Enum:origem_mensagem_enum", "humana,automatica")
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:plataforma_conversao_enum", "meta")
                .Annotation("Npgsql:Enum:resultado_linha_enum", "importado,duplicado,invalido")
                .Annotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .Annotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .Annotation("Npgsql:Enum:status_conversao_enum", "pendente,entregue,falhou,expirado,cancelado")
                .Annotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .Annotation("Npgsql:Enum:status_importacao_enum", "aguardando_mapeamento,processando,concluida,erro")
                .Annotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .Annotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .Annotation("Npgsql:Enum:status_pesquisa_nps_enum", "agendada,enviada,respondida,possivel_nota,expirada,cancelada")
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .Annotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .Annotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video")
                .OldAnnotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .OldAnnotation("Npgsql:Enum:canal_whatsapp_enum", "evolution,cloud_api")
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .OldAnnotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:origem_mensagem_enum", "humana,automatica")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:plataforma_conversao_enum", "meta")
                .OldAnnotation("Npgsql:Enum:resultado_linha_enum", "importado,duplicado,invalido")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_conversao_enum", "pendente,entregue,falhou,expirado,cancelado")
                .OldAnnotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .OldAnnotation("Npgsql:Enum:status_importacao_enum", "aguardando_mapeamento,processando,concluida,erro")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .OldAnnotation("Npgsql:Enum:status_pesquisa_nps_enum", "agendada,enviada,respondida,possivel_nota,expirada,cancelada")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .OldAnnotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");
        }
    }
}
