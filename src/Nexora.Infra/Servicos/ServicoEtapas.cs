using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Configuração do funil: criar, renomear, reordenar e apagar etapa.
///
/// Até aqui as cinco etapas eram semeadas no cadastro e nunca mais mudavam — o que serve à
/// primeira empresa e a mais nenhuma. Todo negócio tem um funil diferente.
///
/// ===================== AS QUATRO INVARIANTES =====================
///   1. `uq_etapas_ordem` é (empresa_id, ordem) ÚNICO — e é um ÍNDICE, não uma constraint, logo
///      NÃO é adiável. Trocar duas posições num só UPDATE viola no meio do caminho.
///   2. `uq_etapas_ganho` — no máximo uma etapa de ganho.
///   3. `fk_contatos_etapa` é ON DELETE RESTRICT — apagar etapa com contato estoura no banco.
///   4. Precisa sobrar ao menos uma etapa NÃO-ganho. Esta o banco não garante, e é a mais
///      perigosa: o lead novo entra na etapa de MENOR ordem, e se a única etapa restante for a
///      de ganho, todo lead nasce ganho. A "porta única do ganho" cairia por dentro.
/// =================================================================</summary>
public class ServicoEtapas(NexoraDbContext db, IContextoEmpresa contexto) : IServicoEtapas
{
    /// <summary>Teto de colunas do kanban. Não é limite técnico: é que um quadro com trinta
    /// colunas não se lê, e o funil deixa de responder "onde este negócio está".</summary>
    public const int MaximoEtapas = 12;

    private const int TamanhoMinimoNome = 2;
    private const int TamanhoMaximoNome = 40;

    public async Task<IReadOnlyList<EtapaDto>> ListarAsync(long pipelineId, CancellationToken ct)
    {
        return await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == pipelineId)
            .OrderBy(e => e.Ordem)
            .Select(e => new EtapaDto(
                e.Id, e.Nome, e.Ordem, e.Cor, e.EGanho,
                // ===================== POR QUE A CONTAGEM É CRUA =====================
                // Aqui NÃO entra `RegrasNegociacao.NoQuadro`. O quadro esconde perdido e
                // anonimizado, mas as duas linhas continuam com `etapa_id` apontando para cá —
                // e é isso que a FK enxerga. Contar como o kanban conta mostraria "0 contatos"
                // numa etapa que o banco recusa apagar, e o dono levaria um erro depois do
                // clique. O número aqui responde "o que trava a remoção", não "o que aparece
                // no funil".
                // ====================================================================
                db.Negociacoes.Count(n => n.EtapaId == e.Id)))
            .ToListAsync(ct);
    }

    // ==================================================================== criar
    public async Task<long> CriarAsync(long pipelineId, NovaEtapa nova, CancellationToken ct)
    {
        var nome = ValidarNome(nova.Nome);

        // O funil precisa existir (BUG-XX). Apagado em outra aba — ou de outra empresa, que o filtro
        // de tenant esconde —, a chave estrangeira recusava no INSERT e a tela via um 500.
        if (!await db.Pipelines.AnyAsync(p => p.Id == pipelineId && p.ArquivadoEm == null, ct))
            throw new RegraDeNegocioException("Este funil não existe mais. Atualize a tela.");

        var etapas = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == pipelineId)
            .Select(e => new { e.Id, e.Nome, e.Ordem }).ToListAsync(ct);

        if (etapas.Count >= MaximoEtapas)
            throw new RegraDeNegocioException(
                $"O funil já tem {MaximoEtapas} etapas. Junte ou apague alguma antes de criar outra.");

        ExigirNomeLivre(etapas.Select(e => (e.Id, e.Nome)), nome, ignorarId: null);

        var etapa = new EtapaFunil
        {
            EmpresaId = contexto.EmpresaId,
            PipelineId = pipelineId,
            Nome = nome,
            // Entra no FIM. Quem quiser no meio reordena depois — e reordenar é uma operação
            // com nome, que reescreve o funil inteiro de uma vez.
            Ordem = (short)(etapas.Count == 0 ? 1 : etapas.Max(e => e.Ordem) + 1),
            Cor = ValidarCor(nova.Cor),
            // NUNCA como ganho: a de ganho é única, já existe, e trocar qual é ela muda o
            // significado de todo o histórico de conversão. Isso tem operação própria.
            EGanho = false
        };

        db.EtapasFunil.Add(etapa);
        await db.SaveChangesAsync(ct);
        return etapa.Id;
    }

    // ==================================================================== editar
    public async Task AtualizarAsync(long id, EditarEtapa dados, CancellationToken ct)
    {
        var etapa = await MinhaEtapaAsync(id, ct);
        var nome = ValidarNome(dados.Nome);

        var outras = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == etapa.PipelineId)
            .Select(e => new { e.Id, e.Nome }).ToListAsync(ct);
        ExigirNomeLivre(outras.Select(e => (e.Id, e.Nome)), nome, ignorarId: id);

        // Renomear a etapa de GANHO é permitido de propósito: a flag `e_ganho` existe justamente
        // para a conversão não depender do nome. É o que deixa a empresa chamar "Venda" de
        // "Fechado", "Contrato assinado" ou o que fizer sentido no negócio dela.
        etapa.Nome = nome;
        etapa.Cor = ValidarCor(dados.Cor);
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== reordenar
    public async Task ReordenarAsync(
        long pipelineId, IReadOnlyList<long> idsNaOrdem, CancellationToken ct)
    {
        var etapas = await db.EtapasFunil.Where(e => e.PipelineId == pipelineId).ToListAsync(ct);

        // Lista COMPLETA, sempre. Aplicar permutação parcial deixaria posições repetidas ou
        // buracos, e o erro apareceria como violação de índice único — ilegível para quem só
        // arrastou uma coluna na tela.
        if (idsNaOrdem.Count != etapas.Count || idsNaOrdem.Distinct().Count() != idsNaOrdem.Count
            || idsNaOrdem.Any(id => etapas.All(e => e.Id != id)))
            throw new RegraDeNegocioException(
                "A nova ordem precisa listar todas as etapas do funil, uma vez cada.");

        // ⚠️ O GUARDA ANTES DE QUALQUER ESCRITA (POS-1). Jogar a etapa de ganho para depois de uma
        // que tem card vendido faz a `ConclusaoAutomatica` encerrar todos eles na rodada da noite.
        // Ver `RegrasDoQuadro.RecusaMexerNasEtapas`.
        //
        // Aqui o que muda é a ORDEM; qual etapa é a de ganho não muda.
        var ganho = etapas.FirstOrDefault(e => e.EGanho);
        var ordemNova = idsNaOrdem
            .Select((id, i) => (id, ordem: (short)(i + 1)))
            .ToDictionary(x => x.id, x => x.ordem);

        await ExigirQueOVendidoNaoVolteAsync(
            pipelineId, etapas,
            ordemDepois: e => ordemNova[e.Id],
            ordemDoGanhoDepois: ganho is null ? null : ordemNova[ganho.Id],
            ct);

        if (ganho is not null)
        {
            ExigirEtapaAntesDoGanho(primeiraEGanho: ordemNova[ganho.Id] == 1);
            await ExigirQueOAbertoFiqueAntesDoGanhoAsync(
                pipelineId, etapas, e => ordemNova[e.Id], ordemNova[ganho.Id], ct);
        }

        var porId = etapas.ToDictionary(e => e.Id);

        // ===================== POR QUE DUAS PASSADAS =====================
        // `uq_etapas_ordem` é um ÍNDICE único, e no Postgres índice não é adiável (só CONSTRAINT
        // é). Numa troca A↔B, o UPDATE que chega primeiro colide com a linha que ainda não se
        // moveu — e o erro é `duplicate key value violates unique constraint`, num fluxo em que
        // o dono só arrastou uma coluna.
        //
        // A primeira passada estaciona todo mundo em ordem NEGATIVA. Negativos são únicos entre
        // si e não podem colidir com nenhum positivo existente, então a passada é sempre segura
        // qualquer que seja a ordem em que o EF emita os UPDATEs. A segunda traz de volta.
        //
        // Alternativa descartada: trocar o índice por uma constraint DEFERRABLE. Custaria uma
        // migration e enfraqueceria a checagem no resto do sistema para resolver um caso que
        // duas passadas resolvem sem tocar no schema.
        // =================================================================
        var transacaoPropria = db.Database.CurrentTransaction is null;
        var tx = transacaoPropria ? await db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            for (var i = 0; i < idsNaOrdem.Count; i++)
                porId[idsNaOrdem[i]].Ordem = (short)-(i + 1);
            await db.SaveChangesAsync(ct);

            for (var i = 0; i < idsNaOrdem.Count; i++)
                porId[idsNaOrdem[i]].Ordem = (short)(i + 1);
            await db.SaveChangesAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // ==================================================================== marcar ganho
    public async Task DefinirGanhoAsync(long id, CancellationToken ct)
    {
        var nova = await MinhaEtapaAsync(id, ct);
        if (nova.EGanho) return;

        var atual = await db.EtapasFunil
            .FirstOrDefaultAsync(e => e.EGanho && e.PipelineId == nova.PipelineId, ct);

        // ⚠️ ESTA É A PORTA MAIS PROVÁVEL DAS TRÊS (POS-1), e era a que eu não tinha visto. "Agora
        // quem fecha é Entregue" é um clique natural — e empurrar a marca de ganho para frente
        // transforma todo card vendido que está ANTES do novo ponto em card "pré-venda", que a
        // rodada da noite conclui. Aqui a ordem das etapas não muda; muda QUAL é a de ganho.
        var todas = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == nova.PipelineId).ToListAsync(ct);

        await ExigirQueOVendidoNaoVolteAsync(
            nova.PipelineId, todas,
            ordemDepois: e => e.Ordem,
            ordemDoGanhoDepois: nova.Ordem,
            ct);

        ExigirEtapaAntesDoGanho(primeiraEGanho: todas.Min(e => e.Ordem) == nova.Ordem);
        await ExigirQueOAbertoFiqueAntesDoGanhoAsync(nova.PipelineId, todas, e => e.Ordem, nova.Ordem, ct);

        // Mesma história do reordenar: `uq_etapas_ganho` é parcial e único por empresa. Marcar a
        // nova antes de desmarcar a antiga viola. Duas passadas, na ordem certa.
        var transacaoPropria = db.Database.CurrentTransaction is null;
        var tx = transacaoPropria ? await db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            if (atual is not null)
            {
                atual.EGanho = false;
                await db.SaveChangesAsync(ct);
            }

            nova.EGanho = true;
            await db.SaveChangesAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // ==================================================================== remover
    public async Task RemoverAsync(long id, long? destinoId, CancellationToken ct)
    {
        var etapa = await MinhaEtapaAsync(id, ct);

        if (etapa.EGanho)
            throw new RegraDeNegocioException(
                "Esta é a etapa de ganho do funil e não pode ser apagada. " +
                "Marque outra etapa como ganho primeiro.");

        var restantes = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == etapa.PipelineId)
            .Where(e => e.Id != id)
            .Select(e => new { e.Id, e.EGanho })
            .ToListAsync(ct);

        // A invariante que o banco NÃO garante. Sem uma etapa não-ganho, o lead novo — que entra
        // na etapa de menor ordem — nasceria na etapa de ganho, e a "porta única do ganho"
        // cairia por dentro: todo contato criado já contaria como venda.
        if (restantes.All(e => e.EGanho))
            throw new RegraDeNegocioException(
                "O funil precisa de ao menos uma etapa além da de ganho — é onde o contato novo entra.");

        // E ela tem de vir ANTES da de ganho (BUG-XX): o lead novo entra na PRIMEIRA etapa.
        var primeiraQueFica = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == etapa.PipelineId && e.Id != id)
            .OrderBy(e => e.Ordem).Select(e => e.EGanho).FirstOrDefaultAsync(ct);
        ExigirEtapaAntesDoGanho(primeiraEGanho: primeiraQueFica);

        // ⚠️ SO A NEGOCIACAO MORA NA ETAPA AGORA (E4e/4). Ate aqui eram duas contagens, contato
        // e negocio, porque as duas tabelas tinham FK RESTRICT para `etapas_funil`.
        var negocios = await db.Negociacoes.CountAsync(n => n.EtapaId == id, ct);

        if (negocios > 0)
        {
            // `fk_negociacoes_etapa` é ON DELETE RESTRICT, então o banco recusaria de qualquer
            // forma. Mas erro de FK não é fluxo de controle: viraria 500 numa tela de
            // configuração. Aqui a pergunta é feita ANTES, e a resposta é uma escolha do dono.
            //
            // E não existe apagar em cascata: o negócio é o ativo do cliente. Apagar uma coluna
            // do kanban nunca pode significar perder o que estava nela.
            if (destinoId is null)
                throw new RegraDeNegocioException(
                    $"Esta etapa tem {negocios} {(negocios == 1 ? "negociação" : "negociações")}. " +
                    "Escolha para qual etapa elas vão antes de apagar.");

            if (destinoId == id)
                throw new RegraDeNegocioException("O destino precisa ser outra etapa.");

            if (restantes.All(e => e.Id != destinoId))
                throw new RegraDeNegocioException("Etapa de destino não encontrada.");
        }

        // ⚠️ APAGAR TAMBÉM MOVE CARD (POS-1), num `ExecuteUpdateAsync` que não passa por
        // `MoverAsync` e portanto não vê `RegrasDoQuadro`. Apagar "Pós-Venda" mandando os pedidos
        // para "Proposta" os põe atrás da venda, e a rodada da noite os encerra.
        //
        // A etapa de ganho em si não pode ser apagada (recusa lá em cima), então aqui só o DESTINO
        // importa. As ordens são renumeradas depois do delete, mas a renumeração preserva a ordem
        // RELATIVA — e a pergunta é relativa.
        if (negocios > 0 && destinoId is { } destino)
        {
            var todasAsEtapas = await db.EtapasFunil.AsNoTracking()
                .Where(e => e.PipelineId == etapa.PipelineId).ToListAsync(ct);

            var ordemDoDestino = todasAsEtapas.Single(e => e.Id == destino).Ordem;

            await ExigirQueOVendidoNaoVolteAsync(
                etapa.PipelineId, todasAsEtapas,
                // Os cards DESTA etapa vão para o destino; os das outras ficam onde estão.
                ordemDepois: e => e.Id == id ? ordemDoDestino : e.Ordem,
                ordemDoGanhoDepois: todasAsEtapas.FirstOrDefault(e => e.EGanho)?.Ordem,
                ct);

            if (todasAsEtapas.FirstOrDefault(e => e.EGanho) is { } doGanho)
                await ExigirQueOAbertoFiqueAntesDoGanhoAsync(
                    etapa.PipelineId, todasAsEtapas,
                    e => e.Id == id ? ordemDoDestino : e.Ordem, doGanho.Ordem, ct);
        }

        var transacaoPropria = db.Database.CurrentTransaction is null;
        var tx = transacaoPropria ? await db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            // O destino sai de `restantes`, que e filtrado pela MESMA pipeline — entao
            // `negociacoes.pipeline_id` continua valendo e nao precisa ser tocado.
            if (negocios > 0)
                await db.Negociacoes.Where(n => n.EtapaId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.EtapaId, destinoId!.Value), ct);

            db.EtapasFunil.Remove(etapa);
            await db.SaveChangesAsync(ct);

            await RenumerarAsync(etapa.PipelineId, ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // ==================================================================== apoio

    /// <summary>Recusa a mudança de etapas que jogaria um negócio VENDIDO para trás da etapa de
    /// ganho. Três chamadores: reordenar, mudar a etapa de ganho e apagar etapa.
    ///
    /// Roda ANTES de qualquer escrita, de propósito: `ReordenarAsync` trabalha com entidades
    /// rastreadas, e ler "o estado de antes" depois de mexer nelas devolveria o de depois.
    ///
    /// Custo: duas consultas, nenhuma delas no caminho quente — isto é tela de configuração, usada
    /// algumas vezes na vida de uma empresa. E a primeira (`AnyAsync` disfarçado de agrupamento) sai
    /// vazia no caso comum, que é não haver nada vendido no funil.</summary>
    private async Task ExigirQueOVendidoNaoVolteAsync(
        long pipelineId,
        IReadOnlyList<EtapaFunil> etapasAntes,
        Func<EtapaFunil, short> ordemDepois,
        short? ordemDoGanhoDepois,
        CancellationToken ct)
    {
        var vendidosPorEtapa = await db.Negociacoes.AsNoTracking()
            .Where(n => n.PipelineId == pipelineId && n.Status == StatusNegociacao.Ganha)
            .GroupBy(n => n.EtapaId)
            .Select(g => new { EtapaId = g.Key, Quantos = g.Count() })
            .ToListAsync(ct);

        if (vendidosPorEtapa.Count == 0) return;

        var porId = etapasAntes.ToDictionary(e => e.Id);

        var alvos = vendidosPorEtapa
            .Where(v => porId.ContainsKey(v.EtapaId))
            .Select(v => new RegrasDoQuadro.EtapaComVendido(
                porId[v.EtapaId].Ordem, ordemDepois(porId[v.EtapaId]), v.Quantos));

        if (RegrasDoQuadro.RecusaMexerNasEtapas(
                etapasAntes.FirstOrDefault(e => e.EGanho)?.Ordem,
                ordemDoGanhoDepois, alvos) is { } recusa)
            throw new RegraDeNegocioException(recusa, conflito: true);
    }

    /// <summary>===================== O NEGÓCIO ABERTO FICA ANTES DO GANHO (BUG-XX) =====================
    ///
    /// A coluna de ganho só mostra e conta venda (`ganha`), e depois dela é pós-venda — o mover já
    /// recusa levar negócio aberto para lá (`RegrasDoQuadro`, regra 4). As três operações de
    /// configuração não recusavam: apagar "Proposta" mandando para "Venda", marcar como ganho uma
    /// etapa com negócios abertos, ou pôr a de ganho antes deles. Os cards sumiam do quadro e das
    /// contagens — e continuavam ocupando o funil ("já tem negócio aberto em Vendas").
    /// ============================================================================================</summary>
    private async Task ExigirQueOAbertoFiqueAntesDoGanhoAsync(
        long pipelineId,
        IReadOnlyList<EtapaFunil> etapasAntes,
        Func<EtapaFunil, short> ordemDepois,
        short ordemDoGanhoDepois,
        CancellationToken ct)
    {
        var abertosPorEtapa = await db.Negociacoes.AsNoTracking()
            .Where(n => n.PipelineId == pipelineId && n.Status == StatusNegociacao.Aberta)
            .GroupBy(n => n.EtapaId)
            .Select(g => new { EtapaId = g.Key, Quantos = g.Count() })
            .ToListAsync(ct);

        var porId = etapasAntes.ToDictionary(e => e.Id);
        var ficariam = abertosPorEtapa
            .Where(a => porId.ContainsKey(a.EtapaId) && ordemDepois(porId[a.EtapaId]) >= ordemDoGanhoDepois)
            .Sum(a => a.Quantos);

        if (ficariam == 0) return;

        throw new RegraDeNegocioException(
            $"{ficariam} {(ficariam == 1 ? "negociação em aberto ficaria" : "negociações em aberto ficariam")} " +
            "na etapa de ganho ou depois dela, onde só ficam vendas — e sumiriam do quadro. " +
            "Mova-as para uma etapa antes da de ganho primeiro.",
            conflito: true);
    }

    /// <summary>A etapa de ganho nunca é a primeira (BUG-XX): é na PRIMEIRA que o negócio novo nasce,
    /// e nascer na de ganho o esconderia do quadro.</summary>
    private static void ExigirEtapaAntesDoGanho(bool primeiraEGanho)
    {
        if (primeiraEGanho)
            throw new RegraDeNegocioException(
                "A etapa de ganho não pode ser a primeira: é na primeira etapa que a negociação nova entra.",
                conflito: true);
    }

    /// <summary>Fecha os buracos de `ordem` depois de uma remoção.
    ///
    /// Buraco não quebraria nada — `OrderBy(Ordem)` ignora lacuna. Mas renumerar mantém o número
    /// que aparece na tela igual à posição real, e sem isso "mover para cima" precisaria raciocinar
    /// sobre vizinhos em vez de sobre índices.
    ///
    /// Aqui NÃO precisa de duas passadas: depois de apagar, renumerar em ordem CRESCENTE só move
    /// cada linha para uma posição já vaga (o buraco vem sempre antes dela).</summary>
    private async Task RenumerarAsync(long pipeline, CancellationToken ct)
    {
        var etapas = await db.EtapasFunil
            .Where(e => e.PipelineId == pipeline)
            .OrderBy(e => e.Ordem).ToListAsync(ct);

        var mudou = false;
        for (var i = 0; i < etapas.Count; i++)
        {
            var desejada = (short)(i + 1);
            if (etapas[i].Ordem == desejada) continue;
            etapas[i].Ordem = desejada;
            mudou = true;
        }

        if (mudou) await db.SaveChangesAsync(ct);
    }

    /// <summary>O query filter já recorta por empresa; o nulo vira "não encontrada", que é a
    /// resposta certa tanto para id inexistente quanto para id de outro tenant.</summary>
    private async Task<EtapaFunil> MinhaEtapaAsync(long id, CancellationToken ct) =>
        await db.EtapasFunil.FirstOrDefaultAsync(e => e.Id == id, ct)
        ?? throw new RegraDeNegocioException("Etapa não encontrada.");

    private static string ValidarNome(string? nome)
    {
        var limpo = (nome ?? "").Trim();
        if (limpo.Length < TamanhoMinimoNome)
            throw new RegraDeNegocioException(
                $"Dê um nome à etapa (mínimo {TamanhoMinimoNome} caracteres).");
        return limpo.Length <= TamanhoMaximoNome ? limpo : limpo[..TamanhoMaximoNome];
    }

    /// <summary>Nome repetido não corrompe nada — mas duas colunas "Proposta" no mesmo quadro
    /// tornam o funil inútil para responder onde o negócio está, que é a única coisa que ele faz.
    /// Comparação sem acento não entra: "Negociação" e "Negociacao" são nomes diferentes para
    /// quem lê, e inventar equivalência aqui surpreenderia mais do que ajudaria.</summary>
    private static void ExigirNomeLivre(
        IEnumerable<(long Id, string Nome)> existentes, string nome, long? ignorarId)
    {
        if (existentes.Any(e => e.Id != ignorarId
                             && string.Equals(e.Nome, nome, StringComparison.OrdinalIgnoreCase)))
            throw new RegraDeNegocioException($"Já existe uma etapa chamada \"{nome}\".");
    }

    /// <summary>Só hexadecimal de 6 dígitos. A cor vai direto para o `style` do cabeçalho da
    /// coluna: aceitar texto livre aqui seria deixar o dono escrever CSS na tela de todo mundo
    /// da empresa dele.</summary>
    private static string ValidarCor(string? cor)
    {
        var limpo = (cor ?? "").Trim();
        if (limpo.Length == 0) return "#2F5D3A";

        if (limpo.Length != 7 || limpo[0] != '#'
            || !limpo[1..].All(Uri.IsHexDigit))
            throw new RegraDeNegocioException(
                $"Cor inválida: \"{limpo}\". Use o formato #RRGGBB.");

        return limpo.ToUpperInvariant();
    }
}
