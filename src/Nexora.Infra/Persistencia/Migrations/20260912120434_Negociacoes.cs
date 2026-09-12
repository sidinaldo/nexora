using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Negociacoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                .Annotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
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
                .OldAnnotation("Npgsql:Enum:status_venda_enum", "fechada,concluida,cancelada")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");

            migrationBuilder.CreateTable(
                name: "negociacoes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    empresa_id = table.Column<long>(type: "bigint", nullable: false),
                    contato_id = table.Column<long>(type: "bigint", nullable: false),
                    pipeline_id = table.Column<long>(type: "bigint", nullable: false),
                    etapa_id = table.Column<long>(type: "bigint", nullable: false),
                    titulo = table.Column<string>(type: "text", nullable: true),
                    valor = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    ordem_kanban = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 0m),
                    responsavel_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<int>(type: "status_negociacao_enum", nullable: false),
                    ganha_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    concluida_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    concluida_por = table.Column<long>(type: "bigint", nullable: true),
                    perdida_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelada_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelada_por = table.Column<long>(type: "bigint", nullable: true),
                    motivo_perda = table.Column<string>(type: "text", nullable: true),
                    observacao = table.Column<string>(type: "text", nullable: true),
                    canal_ciclo_id = table.Column<long>(type: "bigint", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    criado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    atualizado_em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_negociacoes", x => x.id);
                    table.UniqueConstraint("uq_negociacoes_id_empresa", x => new { x.id, x.empresa_id });
                    table.CheckConstraint("ck_negociacoes_terminal", "ganha_em IS NULL OR perdida_em IS NULL");
                    table.CheckConstraint("ck_negociacoes_valor", "status IN ('aberta', 'perdida') OR (valor IS NOT NULL AND valor > 0)");
                    table.ForeignKey(
                        name: "FK_negociacoes_empresas_empresa_id",
                        column: x => x.empresa_id,
                        principalTable: "empresas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_negociacoes_canal_ciclo",
                        column: x => x.canal_ciclo_id,
                        principalTable: "canais_captacao",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_negociacoes_contato",
                        columns: x => new { x.contato_id, x.empresa_id },
                        principalTable: "contatos",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_negociacoes_etapa",
                        columns: x => new { x.etapa_id, x.empresa_id },
                        principalTable: "etapas_funil",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_negociacoes_pipeline",
                        columns: x => new { x.pipeline_id, x.empresa_id },
                        principalTable: "pipelines",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_negociacoes_responsavel",
                        columns: x => new { x.responsavel_id, x.empresa_id },
                        principalTable: "usuarios",
                        principalColumns: new[] { "id", "empresa_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_negociacoes_contato",
                table: "negociacoes",
                columns: new[] { "empresa_id", "contato_id" });

            migrationBuilder.CreateIndex(
                name: "ix_negociacoes_ganhas",
                table: "negociacoes",
                columns: new[] { "empresa_id", "ganha_em" },
                filter: "ganha_em IS NOT NULL AND status <> 'cancelada'");

            migrationBuilder.CreateIndex(
                name: "ix_negociacoes_kanban",
                table: "negociacoes",
                columns: new[] { "empresa_id", "etapa_id", "ordem_kanban" },
                filter: "status NOT IN ('perdida', 'cancelada')");

            // ============================================================================
            //                                O BACKFILL
            //
            // ⚠️ O EF NAO GERA ISTO. Sem estas duas linhas de INSERT a tabela nasce vazia, e o
            // quadro de quem ja usa o sistema aparece sem nenhum card — sem erro nenhum.
            //
            // ⚠️ E NAO E 1 PARA 1. A negociacao junta duas metades que hoje moram separadas:
            //
            //   cada linha de `vendas`                -> uma negociacao (o historico)
            //   cada contato com `ganho_em` VAZIO     -> UMA a mais, a que esta viva no quadro
            //   cada contato com `ganho_em` PREENCHIDO-> nada a mais: as vendas dele ja o cobrem
            //
            // ===================== POR QUE O CONTATO GANHO NAO GERA LINHA =====================
            // Porque `contatos.ganho_em`/`valor` e um ESPELHO da venda, nunca um fato proprio.
            // Medido no banco de desenvolvimento: dos 200 contatos ganhos com venda vigente,
            // `contatos.valor = vendas.valor` em 200. Nao ha um unico caso divergente.
            //
            // Isso importa porque a primeira versao que escrevi criava uma negociacao GANHA
            // tambem para esses contatos, e ela COBRAVA O MESMO DINHEIRO DUAS VEZES: o
            // faturamento subia R$ 703,00 sozinho na hora da migracao. O dono so notaria
            // fechando o mes, e nao teria como saber de onde veio.
            //
            // O caso que parecia excecao nao era: 3 contatos tinham `ganho_em` preenchido com
            // todas as vendas ja `concluida`. Isso e o DESENHO — `ServicoVendas.ConcluirAsync`
            // diz em comentario que nao toca no contato, porque concluir e sobre o PEDIDO e nao
            // sobre o negocio. E o `valor` desses tres tambem espelhava: 50, 500 e 153 sao
            // exatamente as vendas concluidas mais recentes de cada um.
            //
            // Conferido antes de confiar: nao existe contato com `ganho_em` e ZERO vendas, entao
            // esta regra nao deixa ninguem sem card.
            // ================================================================================
            //
            // O sentido contrario existe e e inofensivo: um contato com `ganho_em` vazio que
            // tenha uma venda `fechada` pendurada vira DUAS negociacoes — a ganha, que veio da
            // venda, e a nova aberta. No modelo velho isso era um estado impossivel de
            // representar; no novo e so uma pessoa negociando de novo depois de ter comprado.
            // ============================================================================

            // ---- 1) o historico: uma negociacao por venda
            migrationBuilder.Sql("""
                INSERT INTO negociacoes (
                    empresa_id, contato_id, pipeline_id, etapa_id, valor, ordem_kanban,
                    responsavel_id, status, ganha_em, concluida_em, concluida_por,
                    cancelada_em, cancelada_por, observacao, canal_ciclo_id,
                    criado_em, atualizado_em)
                SELECT
                    v.empresa_id, v.contato_id, e.pipeline_id, v.etapa_id, v.valor,
                    -- So a venda VIGENTE ainda esta no quadro; a concluida e a cancelada sairam
                    -- dele, e para elas a posicao nao quer dizer nada.
                    CASE WHEN v.status = 'fechada' THEN c.ordem_kanban ELSE 0 END,
                    v.responsavel_id,
                    (CASE v.status
                        WHEN 'fechada'   THEN 'ganha'
                        WHEN 'concluida' THEN 'concluida'
                        ELSE                  'cancelada'
                     END)::status_negociacao_enum,
                    -- `ganha_em` fica preenchido ATE na cancelada: ela foi ganha e depois
                    -- desfeita. Quem a tira do faturamento e o filtro do `ix_negociacoes_ganhas`
                    -- (`status <> 'cancelada'`), nao o carimbo em branco.
                    v.fechada_em, v.concluida_em, v.concluida_por,
                    v.cancelada_em, v.cancelada_por, v.observacao, v.canal_id,
                    v.criado_em, v.criado_em
                FROM vendas v
                JOIN contatos c     ON c.id = v.contato_id
                JOIN etapas_funil e ON e.id = v.etapa_id;
                """);

            // ---- 2) o card vivo: uma negociacao para cada contato ainda nao ganho
            migrationBuilder.Sql("""
                INSERT INTO negociacoes (
                    empresa_id, contato_id, pipeline_id, etapa_id, valor, ordem_kanban,
                    responsavel_id, status, perdida_em, motivo_perda,
                    canal_ciclo_id, criado_em, atualizado_em)
                SELECT
                    c.empresa_id, c.id, e.pipeline_id, c.etapa_id, c.valor, c.ordem_kanban,
                    c.responsavel_id,
                    -- Sem ramo para 'ganha': quem tem `ganho_em` nao chega aqui, e e por isso
                    -- que o faturamento nao se mexe.
                    (CASE WHEN c.perdido_em IS NOT NULL THEN 'perdida' ELSE 'aberta' END)
                        ::status_negociacao_enum,
                    c.perdido_em, c.motivo_perda,
                    -- `conversas.canal_ciclo_id` era a campanha DESTA rodada guardada na conversa
                    -- por nao existir onde. `uq_conversas_contato` garante no maximo uma.
                    (SELECT k.canal_ciclo_id FROM conversas k WHERE k.contato_id = c.id LIMIT 1),
                    c.criado_em, c.criado_em
                FROM contatos c
                JOIN etapas_funil e ON e.id = c.etapa_id
                WHERE c.ganho_em IS NULL;
                """);

            // ---- 3) as travas
            // Um JOIN que descarta linha em silencio e o jeito de este bloco falhar sem avisar —
            // o sintoma seria um quadro com cards faltando, ou um faturamento diferente,
            // semanas depois. Aqui as duas coisas param a migracao inteira, dentro da transacao,
            // e nada foi aplicado.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    orfaos     bigint;
                    de_vendas  numeric;
                    de_negocios numeric;
                BEGIN
                    SELECT count(*) INTO orfaos FROM contatos c
                    WHERE NOT EXISTS (SELECT 1 FROM negociacoes n WHERE n.contato_id = c.id);

                    IF orfaos > 0 THEN
                        RAISE EXCEPTION
                            'Backfill de negociacoes: % contato(s) ficaram sem nenhuma negociacao.', orfaos;
                    END IF;

                    -- ⚠️ ESTA E A TRAVA QUE PEGOU O DEFEITO DE VERDADE. O faturamento e o numero
                    -- que o dono reconcilia; ele nao pode mudar por causa de uma migracao.
                    SELECT coalesce(sum(valor), 0) INTO de_vendas
                      FROM vendas WHERE status <> 'cancelada';
                    SELECT coalesce(sum(valor), 0) INTO de_negocios
                      FROM negociacoes WHERE status IN ('ganha', 'concluida');

                    IF de_vendas <> de_negocios THEN
                        RAISE EXCEPTION
                            'Backfill de negociacoes: faturamento mudou de % para %.',
                            de_vendas, de_negocios;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "negociacoes");

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
                .OldAnnotation("Npgsql:Enum:status_negociacao_enum", "aberta,ganha,concluida,perdida,cancelada")
                .OldAnnotation("Npgsql:Enum:status_usuario_enum", "ativo,convidado,inativo")
                .OldAnnotation("Npgsql:Enum:status_venda_enum", "fechada,concluida,cancelada")
                .OldAnnotation("Npgsql:Enum:tipo_midia_enum", "nenhum,imagem,documento,audio,video");
        }
    }
}
