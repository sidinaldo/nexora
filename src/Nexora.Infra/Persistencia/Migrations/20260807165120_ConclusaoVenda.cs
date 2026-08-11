using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ConclusaoVenda : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_vendas_periodo",
                table: "vendas");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .Annotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .Annotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .Annotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:status_venda_enum", "fechada,concluida,cancelada")
                .Annotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video")
                .OldAnnotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.AddColumn<DateTime>(
                name: "concluida_em",
                table: "vendas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "concluida_por",
                table: "vendas",
                type: "bigint",
                nullable: true);

            // ===================== POR QUE ESTA COLUNA É SQL CRU =====================
            // O `AddColumn` gerado emitia `DEFAULT 0` — o inteiro da posição do enum em C# — e o
            // Postgres recusa: "coluna status é do tipo status_venda_enum mas expressão padrão é
            // do tipo integer". Enum NATIVO não aceita o ordinal; aceita o rótulo.
            //
            // O `DROP DEFAULT` logo depois é intencional: o padrão existe só para preencher as
            // linhas que já estão na tabela. Mantê-lo faria o banco divergir do modelo (que não
            // declara default nenhum), e a próxima migração gerada tentaria removê-lo sozinha.
            //
            // Efeito colateral desejado: todo INSERT cru em `vendas` passa a ter que DIZER em que
            // estado a venda nasce — ver `ReconciliadorVendas`.
            // =========================================================================
            // ⚠️ O `;` NO FIM NÃO É ENFEITE, e a falta dele só aparece em produção.
            // `migrationBuilder.Sql` aplicado por `database update` manda cada comando sozinho e
            // o ponto e vírgula é dispensável. Mas `migrations script --idempotent` — que é como
            // o deploy aplica — embrulha o SQL dentro de `IF NOT EXISTS(...) THEN <sql> END IF;`.
            // Sem o `;`, o Postgres lê `... DEFAULT 'fechada' END IF` como um comando só e recusa
            // com "erro de sintaxe em ou próximo a END".
            //
            // O banco de desenvolvimento nunca reclamou porque foi migrado pelo outro caminho.
            // Encontrado ensaiando o script num banco descartável, antes do primeiro deploy.
            migrationBuilder.Sql(
                "ALTER TABLE vendas ADD COLUMN status status_venda_enum NOT NULL DEFAULT 'fechada';");
            migrationBuilder.Sql("ALTER TABLE vendas ALTER COLUMN status DROP DEFAULT;");

            // ===================== O BACKFILL DO `status` =====================
            // O default acima carimba TODAS as linhas existentes como `fechada`, inclusive as
            // canceladas — e a partir daqui é `status` que decide o faturamento. Sem esta linha,
            // toda venda cancelada do histórico VOLTARIA ao relatório no dia do deploy.
            //
            // Uma direção só: `cancelada_em IS NOT NULL` → `cancelada`. `concluida` não existia
            // antes deste bloco, então não há o que deduzir para ela — e inventar conclusão
            // retroativa esvaziaria a coluna Venda de todo cliente de uma vez, sem ninguém ter
            // decidido isso. A rodada diária conclui o passivo depois, no prazo de cada empresa.
            // ==================================================================
            migrationBuilder.Sql(
                "UPDATE vendas SET status = 'cancelada' WHERE cancelada_em IS NOT NULL;");

            // ===================== `IF NOT EXISTS`, E NÃO É DESCUIDO =====================
            // Esta coluna é do bloco 13 e já foi aplicada nos bancos existentes pela migração
            // `20260807140000_DuracaoAudio`. Mas aquela migração foi escrita à mão SEM o
            // `Designer.cs` — e é ele que carrega o atributo `[Migration]`. Sem o atributo, o EF
            // NÃO A ENXERGA: `database update DuracaoAudio` responde "não encontrada", e num banco
            // criado do zero ela nunca roda. O snapshot tem a coluna; o SQL nunca a criaria.
            //
            // Então a criação passa a morar aqui, condicional: banco novo ganha a coluna, banco
            // existente segue em frente. Registrado como pendência no docs/NEG-2.md.
            // ============================================================================
            migrationBuilder.Sql(
                "ALTER TABLE mensagens ADD COLUMN IF NOT EXISTS midia_duracao_segundos integer;");

            migrationBuilder.AddColumn<short>(
                name: "dias_para_concluir_venda",
                table: "empresas",
                type: "smallint",
                nullable: false,
                defaultValue: (short)7);

            migrationBuilder.CreateIndex(
                name: "ix_vendas_contato_status",
                table: "vendas",
                columns: new[] { "empresa_id", "contato_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_vendas_periodo",
                table: "vendas",
                columns: new[] { "empresa_id", "fechada_em" },
                descending: new[] { false, true },
                filter: "status <> 'cancelada'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_empresas_conclusao",
                table: "empresas",
                sql: "dias_para_concluir_venda BETWEEN 0 AND 90");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_vendas_contato_status",
                table: "vendas");

            migrationBuilder.DropIndex(
                name: "ix_vendas_periodo",
                table: "vendas");

            migrationBuilder.DropCheckConstraint(
                name: "ck_empresas_conclusao",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "concluida_em",
                table: "vendas");

            migrationBuilder.DropColumn(
                name: "concluida_por",
                table: "vendas");

            migrationBuilder.DropColumn(
                name: "status",
                table: "vendas");

            // `IF EXISTS` pelo mesmo motivo do `Up`: num banco onde a `DuracaoAudio` chegou a
            // rodar, quem responde por esta coluna é ela, não esta migração.
            migrationBuilder.Sql(
                "ALTER TABLE mensagens DROP COLUMN IF EXISTS midia_duracao_segundos;");

            migrationBuilder.DropColumn(
                name: "dias_para_concluir_venda",
                table: "empresas");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .Annotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .Annotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .Annotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .Annotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .Annotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .Annotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .Annotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .Annotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .Annotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .Annotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .Annotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video")
                .OldAnnotation("Npgsql:Enum:abrangencia_feriado_enum", "nacional,estadual,manual")
                .OldAnnotation("Npgsql:Enum:direcao_mensagem_enum", "entrada,saida")
                .OldAnnotation("Npgsql:Enum:evento_webhook_enum", "lead_criado,lead_movido,venda_fechada,venda_perdida,mensagem_recebida,teste")
                .OldAnnotation("Npgsql:Enum:origem_lead_enum", "instagram,facebook,whatsapp,google,site,qrcode,indicacao,manual,outro")
                .OldAnnotation("Npgsql:Enum:origem_lembrete_enum", "automatico,manual")
                .OldAnnotation("Npgsql:Enum:papel_usuario_enum", "dono,gestor,vendedor")
                .OldAnnotation("Npgsql:Enum:status_conexao_enum", "nao_criada,conectando,conectado,desconectado,offline")
                .OldAnnotation("Npgsql:Enum:status_conversa_enum", "aberta,resolvida")
                .OldAnnotation("Npgsql:Enum:status_entrega_webhook_enum", "pendente,entregue,falhou")
                .OldAnnotation("Npgsql:Enum:status_lembrete_enum", "pendente,concluido,cancelado")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:status_venda_enum", "fechada,concluida,cancelada")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.CreateIndex(
                name: "ix_vendas_periodo",
                table: "vendas",
                columns: new[] { "empresa_id", "fechada_em" },
                descending: new[] { false, true },
                filter: "cancelada_em IS NULL");
        }
    }
}
