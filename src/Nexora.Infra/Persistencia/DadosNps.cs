using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Tempo;

namespace Nexora.Infra.Persistencia;

/// <summary>===================== O ACESSO A DADOS DA RODADA DE NPS =====================
///
/// ⚠️ TUDO AQUI RODA SEM TENANT, e e por isso que todo comando carrega `empresa_id = $1` A MAO. A
/// rodada e um job de fundo: nao ha requisicao, nao ha `IContextoEmpresa` preenchido, e o filtro
/// global do EF nao tem o que filtrar. E a mesma disciplina de `DadosFollowUp`.
///
/// ⚠️ E TODO CORTE DE TEMPO VEM COMO PARAMETRO JA CALCULADO, nunca derivado na consulta. Nao ha
/// `::date` nem `AT TIME ZONE` dentro de WHERE nenhum: o fuso de negocio e resolvido no motor, em
/// C#, e o que chega aqui e um instante ou uma data pronta. E a regra da casa — funcao sobre coluna
/// em filtro descarta o indice — e o LPA-1 custou uma reescrita por esquecer dela.
/// ================================================================================</summary>
public class DadosNps(NexoraDbContext db, TimeProvider relogio) : IDadosNps
{
    /// <summary>===================== AGENDAR O QUE A CONCLUSAO DEIXOU PARA TRAS =====================
    ///
    /// `INSERT ... SELECT` com `NOT EXISTS`: procura negociacao concluida sem pesquisa e cria uma.
    ///
    /// ⚠️ `n.concluida_em >= $4` E O QUE IMPEDE A ENXURRADA, e o corte vem pronto do motor. Uma
    /// empresa com quinhentas vendas antigas liga o NPS e recebe pesquisa so das concluidas nos
    /// ultimos `dias + 7` dias. As mais velhas nasceriam com `data_limite` no passado — canceladas
    /// na mesma rodada —, e perguntar sobre uma compra de tres meses atras seria pior que calar.
    ///
    /// ⚠️ O CORTE E SARGAVEL DE PROPOSITO. A forma obvia seria
    /// `(concluida_em AT TIME ZONE $2)::date + $3 + 7 >= hoje`, e ela tem DUAS funcoes sobre a
    /// coluna dentro do WHERE. O instante equivalente, calculado em C#, diz a mesma coisa e deixa
    /// o predicado sobre a coluna nua.
    ///
    /// ⚠️ CONTATO ANONIMIZADO FICA DE FORA: a LGPD zerou o telefone, e nao ha para onde mandar.
    ///
    /// ===================== QUEM GARANTE E QUEM SO OTIMIZA =====================
    /// `ON CONFLICT DO NOTHING` contra `uq_pesquisas_nps_negociacao` e a GARANTIA de uma pesquisa
    /// por venda: duas rodadas sobrepostas — o agendador nao tem lock distribuido — tentam inserir,
    /// e a segunda nao faz nada em vez de estourar.
    ///
    /// ⚠️ O `NOT EXISTS` ABAIXO E SO OTIMIZACAO, e eu medi: sabotei-o e NENHUM teste caiu, porque o
    /// indice ja barra. Ele evita TENTAR um INSERT por venda do periodo todo dia — com o corte de
    /// data acima isso sao poucas linhas, mas tentar e descartar e trabalho por nada.
    ///
    /// Esta distincao esta escrita porque a tentacao inversa e real: quem ler o `NOT EXISTS` pode
    /// concluir que ele e a protecao e, num refactor, trocar o `ON CONFLICT` por um INSERT seco.
    /// =========================================================================
    ///
    /// ⚠️ O PLANO E `Seq Scan`, E EU MEDI: nenhum indice cobre
    /// `(empresa_id, status = 'concluida', concluida_em)`, e o Postgres varre as 1265 negociacoes
    /// do `nexora_dev` em 5ms. Para um trabalho de UMA VEZ POR DIA isso esta pago. Se `negociacoes`
    /// chegar a centenas de milhares, o conserto e um indice parcial
    /// `(empresa_id, concluida_em) WHERE status = 'concluida'` — gemeo do `ix_negociacoes_perdidas`
    /// que o LPA-1 criou. Nao vale agora, e fica escrito para nao ser redescoberto.
    /// =====================================================================================</summary>
    public async Task<int> AgendarPendentesAsync(Empresa empresa, DateOnly hoje, CancellationToken ct)
    {
        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);

        // O dia mais antigo que ainda rende pesquisa viva: `hoje - dias - 7`. Antes disso, a
        // `data_limite` ja passou.
        var maisAntigo = hoje.AddDays(-empresa.NpsDiasAposConclusao - DiasDeAdiamento);
        var corte = TimeZoneInfo.ConvertTimeToUtc(maisAntigo.ToDateTime(TimeOnly.MinValue), fuso);

        return await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO pesquisas_nps (
                empresa_id, negociacao_id, contato_id, status,
                data_agendada, data_limite, criado_em)
            SELECT n.empresa_id, n.id, n.contato_id, 'agendada'::status_pesquisa_nps_enum,
                   (n.concluida_em AT TIME ZONE {1})::date + {2},
                   (n.concluida_em AT TIME ZONE {1})::date + {2} + {3},
                   {4}
              FROM negociacoes n
              JOIN contatos c ON c.id = n.contato_id AND c.empresa_id = n.empresa_id
             WHERE n.empresa_id = {0}
               AND n.status = 'concluida'
               AND n.concluida_em >= {5}
               AND c.anonimizado_em IS NULL
               AND NOT EXISTS (
                     SELECT 1 FROM pesquisas_nps p WHERE p.negociacao_id = n.id)
            ON CONFLICT DO NOTHING
            """,
            // ⚠️ O NOME IANA, e nao `fuso.Id` — ver `FusoDeNegocio.NomeIana`.
            empresa.Id, FusoDeNegocio.NomeIana(empresa.FusoHorario),
            (int)empresa.NpsDiasAposConclusao, DiasDeAdiamento,
            relogio.GetUtcNow().UtcDateTime, corte);
    }

    /// <summary>Sete dias de adiamento, do prompt. Constante e nao configuracao: e um freio de
    /// seguranca, nao uma preferencia — e dono nenhum pediu para escolher quantas vezes o sistema
    /// insiste.</summary>
    public const int DiasDeAdiamento = 7;

    public Task<int> CancelarDeVendaDesfeitaAsync(long empresaId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE pesquisas_nps p
               SET status = 'cancelada'::status_pesquisa_nps_enum
             WHERE p.empresa_id = {0}
               AND p.status = 'agendada'
               AND EXISTS (
                     SELECT 1 FROM negociacoes n
                      WHERE n.id = p.negociacao_id
                        AND n.empresa_id = p.empresa_id
                        AND n.status <> 'concluida')
            """,
            [empresaId], ct);

    public Task<int> CancelarAdiadasDemaisAsync(long empresaId, DateOnly hoje, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE pesquisas_nps
               SET status = 'cancelada'::status_pesquisa_nps_enum
             WHERE empresa_id = {0}
               AND status = 'agendada'
               AND data_limite < {1}
            """,
            [empresaId, hoje], ct);

    /// <summary>===================== O QUE SAI HOJE =====================
    ///
    /// ⚠️ `JOIN conversas` E NAO `LEFT JOIN`, e a escolha tem consequencia: contato que comprou e
    /// nunca teve conversa no WhatsApp — cadastro manual, importacao — simplesmente nao aparece
    /// aqui. A mensagem precisa de `conversa_id`, e nao ha de onde tirar.
    ///
    /// E ele nao fica preso: a pesquisa segue `agendada` ate a `data_limite` passar, e a varredura
    /// de adiadas-demais a cancela. Auto-resolve, sem caso especial.
    ///
    /// A conexao vem DA CONVERSA, nao da empresa: com dois numeros, a pergunta tem de sair pelo
    /// mesmo por onde a conversa aconteceu.
    /// ==========================================================</summary>
    /// <summary>⚠️ LINQ COM PROJECAO, E NAO `SqlQueryRaw`. Escrevi esta consulta em SQL cru
    /// primeiro e ela voltava VAZIA, em silencio: `Database.SqlQueryRaw&lt;T&gt;` do EF Core 8 so
    /// materializa tipo ESCALAR, e um record com oito propriedades nao e um. Nada estourou — os
    /// seis testes de disparo simplesmente falharam todos de uma vez.
    ///
    /// `DadosFollowUp.ConversasInativasAsync` ja fazia assim, e por isso: record sai de `Select`.
    /// SQL cru aqui fica para `ExecuteSqlRaw` (que devolve contagem) e para escalar.</summary>
    public async Task<IReadOnlyList<PesquisaADisparar>> ADispararAsync(
        long empresaId, DateOnly hoje, CancellationToken ct) =>
        await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.EmpresaId == empresaId
                     && p.Status == StatusPesquisaNps.Agendada
                     && p.DataAgendada <= hoje
                     && p.Contato.AnonimizadoEm == null)
            .OrderBy(p => p.DataAgendada).ThenBy(p => p.Id)
            .SelectMany(p => db.Conversas.IgnoreQueryFilters()
                .Where(cv => cv.ContatoId == p.ContatoId && cv.EmpresaId == p.EmpresaId)
                .Select(cv => new PesquisaADisparar(
                    p.Id, p.NegociacaoId, p.ContatoId,
                    cv.Id, cv.ConexaoId, cv.Conexao.InstanceName,
                    p.Contato.Telefone, p.Contato.Nome)))
            .ToListAsync(ct);

    /// <summary>===================== O CONTATO PODE RECEBER HOJE? =====================
    ///
    /// Duas perguntas numa ida ao banco, as duas sobre a mesma conversa:
    ///
    ///   · houve mensagem HUMANA nas ultimas 24h? Entao ha conversa viva, e entrar com um robo no
    ///     meio dela e grosseria — alem de poder apagar o semaforo de outro card;
    ///   · saiu alguma AUTOMATICA hoje? ⚠️ ESTE E O "TETO DIARIO" DE VERDADE. O prompt falava de um
    ///     teto da EMPRESA e ele NAO EXISTE: `uq_lembrete_teto_diario` e por CONTATO por dia. Sem
    ///     esta metade, o cliente que recebeu follow-up de manha recebe a pesquisa de tarde.
    ///
    /// ⚠️ POR `conversa_id`, E NAO POR `contato_id`: nao ha indice nenhum por contato em
    /// `mensagens`. O contato tem UMA conversa, entao as duas perguntas sao a mesma — e esta usa
    /// indice.
    ///
    /// MEDIDO: as DUAS subconsultas entram por indice, e nao uma como eu tinha escrito. O
    /// planejador escolhe `ix_msg_serie (empresa_id, criado_em DESC)` para a de 24h — o corte de
    /// tempo e mais seletivo que a conversa — e `ix_msg_timeline` para a do dia. 1ms e 2ms.
    /// ======================================================================</summary>
    public async Task<bool> PodeReceberHojeAsync(
        long empresaId, long conversaId, DateOnly hoje, CancellationToken ct)
    {
        var vinteQuatroHoras = relogio.GetUtcNow().UtcDateTime.AddHours(-24);

        var podem = await db.Database.SqlQueryRaw<bool>(
            """
            SELECT NOT EXISTS (
                     SELECT 1 FROM mensagens m
                      WHERE m.empresa_id = {0} AND m.conversa_id = {1}
                        AND m.origem = 'humana'
                        AND m.criado_em >= {2})
                   AND NOT EXISTS (
                     SELECT 1 FROM mensagens m
                      WHERE m.empresa_id = {0} AND m.conversa_id = {1}
                        AND m.origem = 'automatica'
                        AND m.direcao = 'saida'
                        AND m.data_disparo = {3}) AS "Value"
            """,
            empresaId, conversaId, vinteQuatroHoras, hoje).ToListAsync(ct);

        return podem.Count > 0 && podem[0];
    }

    /// <summary>⚠️ `mensagemId` E ANULAVEL porque o caminho `Barrada` nao sabe o id: a mensagem foi
    /// posta por OUTRA rodada. Gravar 0 ali estouraria a FK composta — e a pesquisa precisa ficar
    /// `enviada` de qualquer jeito, senao ela volta todo dia tentando reenviar o que ja saiu.
    ///
    /// E e por isso que a leitura da resposta nao pode DEPENDER deste vinculo: ela casa por
    /// contato, e a citacao da mensagem e reforco, nao chave.</summary>
    public Task MarcarEnviadaAsync(
        long pesquisaId, long? mensagemId, DateTime quando, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE pesquisas_nps
               SET status = 'enviada'::status_pesquisa_nps_enum,
                   data_envio = {1},
                   mensagem_envio_id = COALESCE({2}, mensagem_envio_id)
             WHERE id = {0}
               AND status = 'agendada'
            """,
            [pesquisaId, quando, (object?)mensagemId ?? DBNull.Value], ct);

    public Task AdiarAsync(long pesquisaId, DateOnly novaData, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE pesquisas_nps
               SET data_agendada = {1}
             WHERE id = {0}
               AND status = 'agendada'
            """,
            [pesquisaId, novaData], ct);

    /// <summary>⚠️ `data_envio < limite` E NAO `data_agendada`: o relogio da expiracao conta de
    /// quando a pergunta SAIU. Uma pesquisa adiada tres dias por janela fechada teria expirado
    /// antes de o cliente ler.
    ///
    /// ⚠️ E A DUVIDA NAO DECIDIDA EXPIRA TAMBEM, no mesmo prazo contado da RESPOSTA. Antes ela
    /// ficava aberta para sempre — e `LeituraDaResposta` continuava lendo a pesquisa como viva: um
    /// "2, por favor" sobre outro pedido, semanas depois, virava a nota dela, com aviso de detrator
    /// ao dono e mensagem ao cliente. A suspeita (`nota`) fica na linha como registro; o relatorio
    /// so conta `respondida`.
    ///
    /// O `COALESCE` cobre a duvida gravada antes de a leitura passar a carimbar `data_resposta`:
    /// sem hora da resposta, conta do envio. Rodada diaria, fora do caminho quente: o indice nao
    /// faz falta aqui.</summary>
    public Task<int> ExpirarAsync(long empresaId, DateTime limite, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE pesquisas_nps
               SET status = 'expirada'::status_pesquisa_nps_enum
             WHERE empresa_id = {0}
               AND (
                     (status = 'enviada'
                      AND data_envio IS NOT NULL
                      AND data_envio < {1})
                  OR (status = 'possivel_nota'
                      AND COALESCE(data_resposta, data_envio) < {1})
                   )
            """,
            [empresaId, limite], ct);
}
