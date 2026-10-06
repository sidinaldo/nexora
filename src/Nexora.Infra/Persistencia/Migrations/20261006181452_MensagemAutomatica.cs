using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class MensagemAutomatica : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.AddColumn<long>(
                name: "negociacao_id",
                table: "mensagens",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "origem",
                table: "mensagens",
                type: "origem_mensagem_enum",
                nullable: false,
                // ⚠️ `defaultValueSql` COM O ROTULO, nunca `defaultValue: 0`. O gerador do EF
                // escreve o ORDINAL do enum, e o Postgres recusa `DEFAULT 0` numa coluna de tipo
                // enum: "coluna e do tipo origem_mensagem_enum mas expressao padrao e integer".
                // A migration e aplicada no boot do teste, entao o erro derruba a suite inteira.
                defaultValueSql: "'humana'");

            migrationBuilder.AddColumn<int>(
                name: "tipo_automacao",
                table: "mensagens",
                type: "tipo_automacao_enum",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_msg_negociacao",
                table: "mensagens",
                columns: new[] { "negociacao_id", "empresa_id" },
                principalTable: "negociacoes",
                principalColumns: new[] { "id", "empresa_id" },
                onDelete: ReferentialAction.SetNull);

            // ===================== O BACKFILL, POR DUAS PORTAS DISTINTAS =====================
            //
            // 1. LEMBRETE: o vinculo e confiavel. `uq_msg_lembrete` e unico sobre `lembrete_id`,
            //    entao mensagem com ele preenchido veio de um lembrete e so de um.
            migrationBuilder.Sql(@"
                UPDATE mensagens
                   SET origem = 'automatica', tipo_automacao = 'lembrete'
                 WHERE lembrete_id IS NOT NULL;");

            // ===================== E SO ESSA PORTA =====================
            //
            // ⚠️ HAVIA UMA SEGUNDA REGRA AQUI, E ELA ESTAVA ERRADA:
            //
            //     direcao = 'saida' AND enviado_por IS NULL AND lembrete_id IS NULL -> follow_up
            //
            // Rodada contra o `nexora_dev`, ela marcou 459 mensagens — e as 459 tinham
            // `payload_raw` preenchido, ou seja, vieram do WEBHOOK. Sao mensagens que o vendedor
            // mandou DO CELULAR: o INSERT do webhook nao grava `enviado_por` (ele nao sabe qual
            // usuario do painel seria), entao a coluna fica nula exatamente como numa automatica.
            //
            // Marca-las como robo tiraria do relatorio de tempo de resposta todo atendimento feito
            // pelo celular — justamente o vendedor que responde na rua.
            //
            // ⚠️ E NAO HA REGRA MELHOR: `payload_raw IS NULL` separaria webhook de outbox, mas a
            // outbox hoje so produz lembrete, que a regra 1 ja pega. Nao existe follow-up antigo
            // sem `lembrete_id` — o `MotorFollowUp` sempre cria o lembrete primeiro. Entao a
            // segunda regra nao tinha o que marcar, so o que estragar.
            //
            // Mensagem automatica nova nasce marcada no INSERT (`DadosMensagem`), nao aqui.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_msg_negociacao",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "negociacao_id",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "origem",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "tipo_automacao",
                table: "mensagens");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:fonte_rastreio_enum", "formulario_site,anuncio_whatsapp,importacao")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro,meta_ads")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
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
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
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
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_automacao_enum", "follow_up,lembrete,nps")
                .OldAnnotation("Npgsql:Enum:tipo_conversao_enum", "lead,compra")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");
        }
    }
}
