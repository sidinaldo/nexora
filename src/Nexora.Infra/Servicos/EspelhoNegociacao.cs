using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== O ESPELHO, E POR QUANTO TEMPO ELE EXISTE =====================
///
/// Entre o E4b e o E4e as DUAS representacoes convivem: `contatos`/`vendas` continuam sendo a
/// fonte da verdade que todo mundo le, e `negociacoes` e mantida em dia ao lado. Este arquivo e o
/// unico lugar que sabe traduzir uma na outra.
///
/// Ele existe para que a virada de leitura (E4c/E4d) seja uma troca de consulta, e nao uma
/// migracao de dados com o sistema no ar — e para que ate la qualquer divergencia seja um bug
/// de UM arquivo, e nao de sete servicos.
///
/// ⚠️ ELE MORRE NO E4e, junto com as seis colunas de `contatos` e com a tabela `vendas`. Se este
/// arquivo ainda existir depois disso, alguem parou no meio.
///
/// ===================== A REGRA QUE MANTEM TUDO ALINHADO =====================
/// O quadro mostra exatamente dois estados, e e assim que `ServicoFunil` ja recorta hoje:
///
///   coluna comum  ->  `RegrasContato.NoQuadro`                       ->  Aberta
///   coluna ganho  ->  contato COM venda `fechada`                    ->  Ganha
///
/// O resto esta fora do quadro: `Concluida` (o pedido acabou), `Perdida` e `Cancelada`. Por isso
/// um contato tem no maximo UMA negociacao aberta — a que carrega `etapa_id` e `ordem_kanban`.
/// ==========================================================================</summary>
internal static class EspelhoNegociacao
{
    /// <summary>A negociacao aberta que nasce junto com o contato.
    ///
    /// ⚠️ Liga pela NAVEGACAO (`Contato = contato`) e nao por id, de proposito: os cinco pontos
    /// que criam contato gravam tudo num `SaveChanges` so, e exigir o id ja gerado obrigaria cada
    /// um deles a partir em dois — abrindo uma janela em que existe contato sem negociacao.</summary>
    internal static Negociacao Nova(Contato contato, long pipelineId, long? canalCicloId = null) =>
        new()
        {
            EmpresaId = contato.EmpresaId,
            Contato = contato,
            PipelineId = pipelineId,
            EtapaId = contato.EtapaId,
            OrdemKanban = contato.OrdemKanban,
            ResponsavelId = contato.ResponsavelId,
            Valor = contato.Valor,
            Status = StatusNegociacao.Aberta,
            CanalCicloId = canalCicloId
        };

    /// <summary>A negociacao ABERTA do contato — a que carrega o card no quadro.
    ///
    /// ⚠️ CRIA SE NAO HOUVER, e nao e remendo: e o estado normal de quem ja fechou e concluiu
    /// tudo. `ConcluirAsync` nao limpa `ganho_em`, entao um contato pode estar carimbado como
    /// ganho com todas as vendas concluidas — sem nenhuma negociacao viva. Reabrir esse contato e
    /// comecar uma rodada NOVA, e a rodada nova e uma linha nova.</summary>
    internal static async Task<Negociacao> AbertaAsync(
        NexoraDbContext db, Contato contato, CancellationToken ct)
    {
        var aberta = await db.Negociacoes
            .Where(n => n.ContatoId == contato.Id && n.Status == StatusNegociacao.Aberta)
            .OrderByDescending(n => n.Id)
            .FirstOrDefaultAsync(ct);

        if (aberta is not null)
            return aberta;

        var nova = Nova(
            contato, await PipelineDaEtapaAsync(db, contato.EtapaId, contato.EmpresaId, ct));
        db.Negociacoes.Add(nova);
        return nova;
    }

    /// <summary>A negociacao GANHA do contato — a que espelha a venda vigente.
    ///
    /// Devolve nulo quando nao ha: contato nunca ganho, ou ja concluido. Quem chama decide, e nos
    /// dois casos a resposta certa e nao fazer nada.</summary>
    internal static Task<Negociacao?> GanhaAsync(
        NexoraDbContext db, long contatoId, CancellationToken ct) =>
        db.Negociacoes
            .Where(n => n.ContatoId == contatoId && n.Status == StatusNegociacao.Ganha)
            .OrderByDescending(n => n.Id)
            .FirstOrDefaultAsync(ct)!;

    /// <summary>As negociacoes espelho de um lote de vendas, pelo elo explicito.
    ///
    /// ⚠️ Pelo `venda_id`, NUNCA por (contato, valor, data): `ServicoVendas.CancelarAsync` ja
    /// registra que casar por timestamp derrubou um teste — duas vendas no mesmo instante casavam
    /// as duas. E por isso que a coluna existe.</summary>
    internal static Task<List<Negociacao>> DasVendasAsync(
        NexoraDbContext db, IReadOnlyList<long> vendaIds, CancellationToken ct) =>
        db.Negociacoes.Where(n => n.VendaId != null && vendaIds.Contains(n.VendaId.Value))
            .ToListAsync(ct);

    /// <summary>Cria a negociacao que faltar para os contatos de uma empresa.
    ///
    /// ===================== PARA QUEM INSERE CONTATO EM LOTE =====================
    /// Os dois semeadores (`ServicoSemente` e `ServicoSeedDemonstracao`) montam dezenas de
    /// contatos num `AddRange` e carimbam `ganho_em`/`perdido_em` DEPOIS, com `ExecuteUpdate`.
    /// Espalhar a criacao do espelho por dentro desse fluxo repetiria as regras de estado em mais
    /// dois lugares — e seria mais um par de pontos para divergir.
    ///
    /// ⚠️ O status sai do CARIMBO do contato, e nao ha ramo para venda: nenhum dos dois
    /// semeadores cria venda. Se um dia criarem, este metodo passa a precisar do mesmo recorte do
    /// backfill (a venda representa o carimbo, entao o carimbo nao vira linha propria).
    /// ==========================================================================</summary>
    internal static async Task<int> ReconciliarAsync(
        NexoraDbContext db, long empresaId, CancellationToken ct)
    {
        var pendentes = await db.Contatos.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.EmpresaId == empresaId)
            .Where(c => !db.Negociacoes.IgnoreQueryFilters().Any(n => n.ContatoId == c.Id))
            .ToListAsync(ct);

        if (pendentes.Count == 0)
            return 0;

        var pipelines = await db.EtapasFunil.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.EmpresaId == empresaId)
            .ToDictionaryAsync(e => e.Id, e => e.PipelineId, ct);

        foreach (var c in pendentes)
        {
            var nova = new Negociacao
            {
                EmpresaId = c.EmpresaId,
                ContatoId = c.Id,
                PipelineId = pipelines[c.EtapaId],
                EtapaId = c.EtapaId,
                OrdemKanban = c.OrdemKanban,
                ResponsavelId = c.ResponsavelId,
                Valor = c.Valor,
                CriadoEm = c.CriadoEm,
                AtualizadoEm = c.CriadoEm
            };

            if (c.PerdidoEm is not null)
            {
                nova.Status = StatusNegociacao.Perdida;
                nova.PerdidaEm = c.PerdidoEm;
                nova.MotivoPerda = c.MotivoPerda;
            }
            else if (c.GanhoEm is not null)
            {
                nova.Status = StatusNegociacao.Ganha;
                nova.GanhaEm = c.GanhoEm;
            }
            else
            {
                nova.Status = StatusNegociacao.Aberta;
            }

            db.Negociacoes.Add(nova);
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return pendentes.Count;
    }

    /// <summary>Em qual funil esta a etapa. A negociacao guarda a pipeline redundantemente para o
    /// quadro e o relatorio nao precisarem de um join a cada consulta.
    ///
    /// ⚠️ `IgnoreQueryFilters` COM `empresa_id` EXPLICITO, e nao por comodidade: o webhook da
    /// Evolution e a captura publica rodam SEM tenant no contexto (`EmpresaId = 0`) — eles
    /// descobrem a empresa pela conexao ou pelo formulario. Com o filtro ligado esta consulta
    /// voltava vazia e o lead novo nao ganhava negociacao nenhuma.
    ///
    /// O recorte continua existindo: ele so passou a ser o parametro, que vem do proprio contato.
    /// E a FK composta `fk_negociacoes_etapa` e quem garante de verdade.</summary>
    internal static Task<long> PipelineDaEtapaAsync(
        NexoraDbContext db, long etapaId, long empresaId, CancellationToken ct) =>
        db.EtapasFunil.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.Id == etapaId && e.EmpresaId == empresaId)
            .Select(e => e.PipelineId)
            .FirstAsync(ct);
}
