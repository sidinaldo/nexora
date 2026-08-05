using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infra.Persistencia.Migrations
{
    /// <summary>O que a série temporal precisa do banco: a função de minutos úteis e dois índices.
    ///
    /// ===================== A TERCEIRA IMPLEMENTAÇÃO DA MESMA REGRA =====================
    /// Minutos úteis já existia duas vezes — `TempoUtil.MinutosUteis` (C#) e `minutosUteis`
    /// (nucleo/semaforo.ts). Agora existe uma terceira, aqui, e a razão é a proibição de agregar
    /// em memória: a média de tempo de resposta de um mês inteiro não pode subir do banco linha a
    /// linha para ser descontada no C#.
    ///
    /// Três cópias da mesma regra é dívida, e ela é paga do único jeito que funciona: os MESMOS
    /// casos de `tests/paridade/minutos-uteis.json` rodam contra esta função
    /// (`ParidadeMinutosUteisSqlDbTests`). Se alguém mexer num lado só, o teste fica vermelho.
    /// ==================================================================================</summary>
    public partial class SerieTemporal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // O laço é o MESMO do C#, inclusive a trava de 400 iterações: com bitmask zerado
            // (dado ruim) o laço não terminaria nunca, e travar dentro do banco é bem pior do
            // que travar num processo que se pode reiniciar.
            //
            // STABLE e não IMMUTABLE: `AT TIME ZONE <text>` depende do tzdata do servidor, que
            // pode ser recarregado. Declarar IMMUTABLE aqui autorizaria o planejador a pré-
            // calcular a chamada e a guardar o resultado num índice — errado por construção.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_minutos_uteis(
                    p_inicio      timestamptz,
                    p_fim         timestamptz,
                    p_fuso        text,
                    p_hora_inicio int,
                    p_hora_fim    int,
                    p_dias_semana int,
                    p_feriados    date[]
                ) RETURNS integer
                LANGUAGE plpgsql
                STABLE
                PARALLEL SAFE
                AS $func$
                DECLARE
                    v_ini   timestamp;
                    v_fim   timestamp;
                    v_cur   timestamp;
                    v_dia   date;
                    v_abre  timestamp;
                    v_fecha timestamp;
                    v_de    timestamp;
                    v_ate   timestamp;
                    v_total numeric := 0;
                    v_i     int := 0;
                BEGIN
                    IF p_inicio IS NULL OR p_fim IS NULL THEN
                        RETURN NULL;
                    END IF;

                    -- Para o fuso de NEGÓCIO antes de qualquer conta: a janela é 8h-20h na hora
                    -- da empresa, não na do servidor.
                    v_ini := p_inicio AT TIME ZONE p_fuso;
                    v_fim := p_fim    AT TIME ZONE p_fuso;

                    IF v_fim <= v_ini THEN
                        RETURN 0;
                    END IF;

                    v_cur := v_ini;

                    WHILE v_i < 400 AND v_cur < v_fim LOOP
                        v_dia := v_cur::date;

                        -- Bit 0 = domingo, igual ao DayOfWeek do .NET e ao getDay() do JS.
                        IF ((p_dias_semana >> EXTRACT(DOW FROM v_dia)::int) & 1) = 1
                           AND NOT (p_feriados IS NOT NULL AND v_dia = ANY (p_feriados)) THEN

                            v_abre := v_dia + make_interval(hours => p_hora_inicio);
                            -- hora_fim = 24 significa meia-noite do dia seguinte; sem este ramo
                            -- `make_interval(hours => 24)` daria o mesmo instante, mas deixar
                            -- explícito evita que alguém "otimize" o caso e mude o resultado.
                            v_fecha := CASE WHEN p_hora_fim >= 24
                                            THEN (v_dia + 1)::timestamp
                                            ELSE v_dia + make_interval(hours => p_hora_fim)
                                       END;

                            v_de  := GREATEST(v_cur, v_abre);
                            v_ate := LEAST(v_fim, v_fecha);

                            IF v_ate > v_de THEN
                                v_total := v_total + EXTRACT(EPOCH FROM (v_ate - v_de)) / 60;
                            END IF;
                        END IF;

                        -- Próximo dia, já na abertura.
                        v_cur := (v_dia + 1) + make_interval(hours => p_hora_inicio);
                        v_i := v_i + 1;
                    END LOOP;

                    RETURN floor(v_total);
                END;
                $func$;
                """);

            // ===== ÍNDICES =====
            // `contatos` já tem ix_contatos_criado e ix_contatos_ganho, que servem as séries de
            // leads, vendas e faturamento. Faltavam os dois abaixo.

            // Mensagens por tempo. Serve DOIS caminhos: o cálculo de tempo de resposta (que
            // precisa varrer a janela de mensagens do período) e o feed de atividades. Um índice
            // só em vez de dois: para mensagem de ENTRADA, `recebida_em` e `criado_em` recebem o
            // mesmo instante no webhook, então ordenar por `criado_em` dá a mesma ordem sem
            // custar uma segunda árvore para manter em cada INSERT.
            migrationBuilder.Sql("""
                CREATE INDEX ix_msg_serie ON mensagens (empresa_id, criado_em DESC);
                """);

            // Lembretes concluídos, para o feed de atividades. PARCIAL: pendente e cancelado não
            // são evento de atividade, e mantê-los no índice seria carregar a maioria das linhas
            // para nunca lê-las.
            migrationBuilder.Sql("""
                CREATE INDEX ix_lembretes_concluido ON lembretes (empresa_id, concluido_em DESC)
                    WHERE concluido_em IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_lembretes_concluido;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_msg_serie;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS nexora_minutos_uteis(timestamptz, timestamptz, text, int, int, int, date[]);");
        }
    }
}
