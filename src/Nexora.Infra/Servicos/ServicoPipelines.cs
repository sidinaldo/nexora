using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Os funis da empresa: criar, renomear, trocar a cor, apagar, escolher o padrão.
///
/// ===================== AS TRÊS INVARIANTES =====================
///   1. `uq_pipelines_padrao` — no máximo UMA padrão por empresa. Índice único parcial, logo
///      NÃO adiável: marcar a nova antes de desmarcar a antiga viola no meio do caminho. Mesmo
///      problema e mesma solução de `DefinirGanhoAsync` nas etapas — duas passadas.
///   2. `fk_etapas_pipeline` é ON DELETE RESTRICT — apagar pipeline com etapa estoura no banco.
///      Por isso `RemoverAsync` apaga as etapas explicitamente, na mesma transação.
///   3. Precisa sobrar ao menos UMA pipeline, e ela tem de ser a padrão. Esta o banco não
///      garante: zero pipelines é um estado válido para o Postgres e quebrado para o produto —
///      o lead que chegar não teria onde entrar.
/// ===============================================================</summary>
public class ServicoPipelines(NexoraDbContext db, IContextoEmpresa contexto) : IServicoPipelines
{
    /// <summary>Teto de funis. Não é limite técnico: cada pipeline é um item no menu lateral, e
    /// a barra foi dimensionada para caber sem rolar num notebook de 768px de altura. Passar
    /// disso troca uma garantia de navegação por uma lista que ninguém percorre — e "tenho vinte
    /// funis" nunca foi o problema de uma PME.</summary>
    public const int MaximoPipelines = 8;

    private const int TamanhoMinimoNome = 2;
    private const int TamanhoMaximoNome = 40;

    /// <summary>As duas etapas com que toda pipeline nova nasce.
    ///
    /// Duas, e não cinco como no cadastro da empresa: ali são os cinco passos de um funil de
    /// vendas, que é o caso conhecido. Aqui o processo é desconhecido — quem cria "Pós-venda"
    /// desenha as fases dele. O mínimo que faz o quadro funcionar é uma porta de entrada e uma
    /// de saída.</summary>
    private static readonly (string Nome, short Ordem, string Cor, bool EGanho)[] EtapasIniciais =
    [
        ("Entrada", 1, "#7FA88B", false),
        ("Fechado", 2, "#1E4028", true)
    ];

    public async Task<IReadOnlyList<PipelineDto>> ListarAsync(CancellationToken ct) =>
        await db.Pipelines.AsNoTracking()
            .OrderBy(p => p.Ordem).ThenBy(p => p.Nome)
            .Select(p => new PipelineDto(
                p.Id, p.Nome, p.Cor, p.Ordem, p.Padrao,
                db.EtapasFunil.Count(e => e.PipelineId == p.Id)))
            .ToListAsync(ct);

    public async Task<long> PadraoAsync(CancellationToken ct)
    {
        // `Padrao` primeiro, depois ordem, depois id. O desempate existe para base restaurada de
        // antes de `uq_pipelines_padrao`: devolver sempre a mesma é melhor que devolver qualquer.
        var id = await db.Pipelines.AsNoTracking()
            .OrderByDescending(p => p.Padrao).ThenBy(p => p.Ordem).ThenBy(p => p.Id)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(ct);

        if (id == 0)
            throw new RegraDeNegocioException("Esta empresa não tem funil configurado.");

        return id;
    }

    // ==================================================================== criar
    public async Task<long> CriarAsync(NovaPipeline nova, CancellationToken ct)
    {
        var nome = ValidarNome(nova.Nome);

        var existentes = await db.Pipelines.AsNoTracking()
            .Select(p => new { p.Id, p.Nome, p.Ordem }).ToListAsync(ct);

        // 409, e não 400: o nome que chegou é válido e nada o disputa. O que impede é o ESTADO —
        // a empresa chegou no teto.
        //
        // ⚠️ O 422 seria mais preciso ("entendi o pedido e mesmo assim não dá"), e o mecanismo
        // para isso (`RegraDeNegocioException.StatusHttp`) existe na branch das etiquetas, que
        // ainda não foi mesclada. Duplicá-lo aqui garantiria conflito nas mesmas linhas. Quando as
        // duas se encontrarem, este caso troca para 422 junto com o teto de etiquetas.
        if (existentes.Count >= MaximoPipelines)
            throw new RegraDeNegocioException(
                $"A empresa já tem {MaximoPipelines} funis. Apague algum antes de criar outro.",
                conflito: true);

        ExigirNomeLivre(existentes.Select(p => (p.Id, p.Nome)), nome, ignorarId: null);

        var pipeline = new Pipeline
        {
            EmpresaId = contexto.EmpresaId,
            Nome = nome,
            Cor = ValidarCor(nova.Cor),
            Ordem = (short)(existentes.Count == 0 ? 1 : existentes.Max(p => p.Ordem) + 1),
            // NUNCA como padrão: a padrão já existe, e trocar qual é ela muda para onde todo lead
            // novo vai. Isso tem operação própria, pelo mesmo motivo que a etapa de ganho tem.
            Padrao = false
        };

        // Transação própria só se não houver uma em curso — o mesmo cuidado de `ServicoEtapas`.
        // A pipeline e as etapas dela têm de nascer juntas ou não nascer: pipeline sem etapa é um
        // item de menu que leva a uma tela quebrada.
        var transacaoPropria = db.Database.CurrentTransaction is null;
        var tx = transacaoPropria ? await db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(ct);

            foreach (var (nomeEtapa, ordem, cor, eGanho) in EtapasIniciais)
                db.EtapasFunil.Add(new EtapaFunil
                {
                    EmpresaId = contexto.EmpresaId,
                    PipelineId = pipeline.Id,
                    Nome = nomeEtapa,
                    Ordem = ordem,
                    Cor = cor,
                    EGanho = eGanho
                });

            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }

        return pipeline.Id;
    }

    // ==================================================================== editar
    public async Task AtualizarAsync(long id, EditarPipeline dados, CancellationToken ct)
    {
        var pipeline = await MinhaPipelineAsync(id, ct);
        var nome = ValidarNome(dados.Nome);

        var outras = await db.Pipelines.AsNoTracking()
            .Select(p => new { p.Id, p.Nome }).ToListAsync(ct);
        ExigirNomeLivre(outras.Select(p => (p.Id, p.Nome)), nome, ignorarId: id);

        pipeline.Nome = nome;
        pipeline.Cor = ValidarCor(dados.Cor);
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== padrão
    public async Task DefinirPadraoAsync(long id, CancellationToken ct)
    {
        var nova = await MinhaPipelineAsync(id, ct);
        if (nova.Padrao) return;

        var atual = await db.Pipelines.FirstOrDefaultAsync(p => p.Padrao, ct);

        // ⚠️ DUAS PASSADAS, na ordem certa. `uq_pipelines_padrao` é índice único parcial e NÃO é
        // adiável — só CONSTRAINT é. Marcar a nova antes de desmarcar a antiga viola no meio do
        // UPDATE. Mesma mecânica de `ServicoEtapas.DefinirGanhoAsync`.
        var transacaoPropria = db.Database.CurrentTransaction is null;
        var tx = transacaoPropria ? await db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            if (atual is not null)
            {
                atual.Padrao = false;
                await db.SaveChangesAsync(ct);
            }

            nova.Padrao = true;
            await db.SaveChangesAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // ==================================================================== remover
    public async Task RemoverAsync(long id, CancellationToken ct)
    {
        var pipeline = await MinhaPipelineAsync(id, ct);

        // A invariante que o banco NÃO garante. Sem pipeline padrão, o próximo lead que chegar
        // pelo WhatsApp não teria onde entrar — e o erro apareceria no processamento do webhook,
        // longe de quem apagou.
        if (pipeline.Padrao)
            throw new RegraDeNegocioException(
                "Este é o funil padrão e não pode ser apagado. " +
                "Marque outro como padrão primeiro.");

        var etapas = await db.EtapasFunil.Where(e => e.PipelineId == id).ToListAsync(ct);
        var ids = etapas.Select(e => e.Id).ToList();

        // Contagem CRUA, sem `RegrasContato.NoQuadro`: perdido e anonimizado continuam com
        // `etapa_id` apontando para cá, e é isso que a FK enxerga. Contar como o quadro conta
        // diria "0 contatos" numa pipeline que o banco recusa apagar. Mesma decisão, pelo mesmo
        // motivo, de `ServicoEtapas.ListarAsync`.
        var contatos = await db.Contatos.CountAsync(c => ids.Contains(c.EtapaId), ct);

        if (contatos > 0)
            throw new RegraDeNegocioException(
                $"Este funil tem {contatos} contato(s) nas etapas dele. " +
                "Mova os contatos para outro funil antes de apagar.");

        var transacaoPropria = db.Database.CurrentTransaction is null;
        var tx = transacaoPropria ? await db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            // As etapas ANTES da pipeline: `fk_etapas_pipeline` é RESTRICT, e a ordem inversa
            // estoura no banco.
            db.EtapasFunil.RemoveRange(etapas);
            await db.SaveChangesAsync(ct);

            db.Pipelines.Remove(pipeline);
            await db.SaveChangesAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // ==================================================================== validação
    private async Task<Pipeline> MinhaPipelineAsync(long id, CancellationToken ct) =>
        await db.Pipelines.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new RegraDeNegocioException("Funil não encontrado.");

    private static string ValidarNome(string? nome)
    {
        var limpo = (nome ?? "").Trim();

        if (limpo.Length < TamanhoMinimoNome)
            throw new RegraDeNegocioException(
                $"Dê um nome ao funil (mínimo {TamanhoMinimoNome} caracteres).");

        if (limpo.Length > TamanhoMaximoNome)
            throw new RegraDeNegocioException(
                $"O nome do funil tem no máximo {TamanhoMaximoNome} caracteres.");

        return limpo;
    }

    /// <summary>Nome único por empresa. Checado aqui para a mensagem ser legível, e garantido por
    /// `uq_pipelines_empresa_nome` no banco — que é quem de fato fecha a janela entre esta
    /// consulta e o `SaveChanges`.
    ///
    /// `conflito: true` ⇒ 409: o nome que chegou é válido em si; o que impede é o ESTADO.</summary>
    private static void ExigirNomeLivre(
        IEnumerable<(long Id, string Nome)> existentes, string nome, long? ignorarId)
    {
        if (existentes.Any(p => p.Id != ignorarId
                             && string.Equals(p.Nome, nome, StringComparison.OrdinalIgnoreCase)))
            throw new RegraDeNegocioException(
                $"Já existe um funil chamado \"{nome}\".", conflito: true);
    }

    private static string ValidarCor(string? cor)
    {
        var limpo = (cor ?? "").Trim();
        if (limpo.Length == 0) return "#2F5D3A";

        if (limpo.Length != 7 || limpo[0] != '#' || !limpo[1..].All(Uri.IsHexDigit))
            throw new RegraDeNegocioException(
                $"Cor inválida: \"{limpo}\". Use o formato #RRGGBB.");

        return limpo.ToUpperInvariant();
    }
}
