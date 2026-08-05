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

    public async Task<IReadOnlyList<EtapaDto>> ListarAsync(CancellationToken ct) =>
        await db.EtapasFunil.AsNoTracking()
            .OrderBy(e => e.Ordem)
            .Select(e => new EtapaDto(
                e.Id, e.Nome, e.Ordem, e.Cor, e.EGanho,
                // ===================== POR QUE A CONTAGEM É CRUA =====================
                // Aqui NÃO entra `RegrasContato.NoQuadro`. O quadro esconde perdido e
                // anonimizado, mas as duas linhas continuam com `etapa_id` apontando para cá —
                // e é isso que a FK enxerga. Contar como o kanban conta mostraria "0 contatos"
                // numa etapa que o banco recusa apagar, e o dono levaria um erro depois do
                // clique. O número aqui responde "o que trava a remoção", não "o que aparece
                // no funil".
                // ====================================================================
                db.Contatos.Count(c => c.EtapaId == e.Id)))
            .ToListAsync(ct);

    // ==================================================================== criar
    public async Task<long> CriarAsync(NovaEtapa nova, CancellationToken ct)
    {
        var nome = ValidarNome(nova.Nome);

        var etapas = await db.EtapasFunil.AsNoTracking()
            .Select(e => new { e.Id, e.Nome, e.Ordem }).ToListAsync(ct);

        if (etapas.Count >= MaximoEtapas)
            throw new RegraDeNegocioException(
                $"O funil já tem {MaximoEtapas} etapas. Junte ou apague alguma antes de criar outra.");

        ExigirNomeLivre(etapas.Select(e => (e.Id, e.Nome)), nome, ignorarId: null);

        var etapa = new EtapaFunil
        {
            EmpresaId = contexto.EmpresaId,
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
    public async Task ReordenarAsync(IReadOnlyList<long> idsNaOrdem, CancellationToken ct)
    {
        var etapas = await db.EtapasFunil.ToListAsync(ct);

        // Lista COMPLETA, sempre. Aplicar permutação parcial deixaria posições repetidas ou
        // buracos, e o erro apareceria como violação de índice único — ilegível para quem só
        // arrastou uma coluna na tela.
        if (idsNaOrdem.Count != etapas.Count || idsNaOrdem.Distinct().Count() != idsNaOrdem.Count
            || idsNaOrdem.Any(id => etapas.All(e => e.Id != id)))
            throw new RegraDeNegocioException(
                "A nova ordem precisa listar todas as etapas do funil, uma vez cada.");

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

        var atual = await db.EtapasFunil.FirstOrDefaultAsync(e => e.EGanho, ct);

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
            .Where(e => e.Id != id)
            .Select(e => new { e.Id, e.EGanho })
            .ToListAsync(ct);

        // A invariante que o banco NÃO garante. Sem uma etapa não-ganho, o lead novo — que entra
        // na etapa de menor ordem — nasceria na etapa de ganho, e a "porta única do ganho"
        // cairia por dentro: todo contato criado já contaria como venda.
        if (restantes.All(e => e.EGanho))
            throw new RegraDeNegocioException(
                "O funil precisa de ao menos uma etapa além da de ganho — é onde o lead novo entra.");

        var contatos = await db.Contatos.CountAsync(c => c.EtapaId == id, ct);

        if (contatos > 0)
        {
            // `fk_contatos_etapa` é ON DELETE RESTRICT, então o banco recusaria de qualquer
            // forma. Mas erro de FK não é fluxo de controle: viraria 500 numa tela de
            // configuração. Aqui a pergunta é feita ANTES, e a resposta é uma escolha do dono.
            //
            // E não existe apagar em cascata: contato é o ativo do cliente. Apagar uma coluna do
            // kanban nunca pode significar perder as pessoas que estavam nela.
            if (destinoId is null)
                throw new RegraDeNegocioException(
                    $"Esta etapa tem {contatos} {(contatos == 1 ? "contato" : "contatos")}. " +
                    "Escolha para qual etapa eles vão antes de apagar.");

            if (destinoId == id)
                throw new RegraDeNegocioException("O destino precisa ser outra etapa.");

            if (restantes.All(e => e.Id != destinoId))
                throw new RegraDeNegocioException("Etapa de destino não encontrada.");
        }

        var transacaoPropria = db.Database.CurrentTransaction is null;
        var tx = transacaoPropria ? await db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            if (contatos > 0)
                await db.Contatos.Where(c => c.EtapaId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.EtapaId, destinoId!.Value), ct);

            db.EtapasFunil.Remove(etapa);
            await db.SaveChangesAsync(ct);

            await RenumerarAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // ==================================================================== apoio

    /// <summary>Fecha os buracos de `ordem` depois de uma remoção.
    ///
    /// Buraco não quebraria nada — `OrderBy(Ordem)` ignora lacuna. Mas renumerar mantém o número
    /// que aparece na tela igual à posição real, e sem isso "mover para cima" precisaria raciocinar
    /// sobre vizinhos em vez de sobre índices.
    ///
    /// Aqui NÃO precisa de duas passadas: depois de apagar, renumerar em ordem CRESCENTE só move
    /// cada linha para uma posição já vaga (o buraco vem sempre antes dela).</summary>
    private async Task RenumerarAsync(CancellationToken ct)
    {
        var etapas = await db.EtapasFunil.OrderBy(e => e.Ordem).ToListAsync(ct);

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
