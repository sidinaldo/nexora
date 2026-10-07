using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Ver `IServicoPesquisaNps` para o porque de cada gesto.
///
/// ⚠️ ESTE SERVICO RODA NA REQUISICAO, com tenant no contexto — diferente do `MotorNps` e da
/// `LeituraDaResposta`, que sao job e webhook. Entao aqui o filtro global VALE, e nao ha
/// `IgnoreQueryFilters` nenhum: a pesquisa de outra empresa simplesmente nao volta.</summary>
public class ServicoPesquisaNps(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    IAcoesDaNota acoes,
    ColetorAuditoria trilha,
    TimeProvider relogio) : IServicoPesquisaNps
{
    public async Task<IReadOnlyList<NotaDaCompra>> DoContatoAsync(long contatoId, CancellationToken ct)
    {
        // A compra mais recente primeiro; o `Id` desempata duas compras fechadas no mesmo instante
        // (importacao em lote), para a ordem nao mudar entre dois carregamentos.
        return await db.PesquisasNps.AsNoTracking()
            .Where(p => p.ContatoId == contatoId)
            .OrderByDescending(p => p.Negociacao.GanhaEm)
            .ThenByDescending(p => p.Id)
            .Select(p => new NotaDaCompra(
                p.Id, p.NegociacaoId, p.Negociacao.GanhaEm, p.Negociacao.Valor,
                p.Status, p.Nota, p.Comentario, p.DataAgendada, p.DataEnvio, p.DataResposta))
            .ToListAsync(ct);
    }

    public async Task<NotaEmDuvida?> EmDuvidaNaConversaAsync(long conversaId, CancellationToken ct)
    {
        var contatoId = await db.Conversas.AsNoTracking()
            .Where(c => c.Id == conversaId)
            .Select(c => (long?)c.ContatoId)
            .FirstOrDefaultAsync(ct);

        if (contatoId == null) return null;

        // ⚠️ `Nota != null` E PARTE DA PERGUNTA, nao enfeite: a tela pergunta "e a nota X?", e uma
        // `PossivelNota` sem numero nao tem X para perguntar. O leitor nunca grava assim — mas uma
        // pergunta com "nota ?" seria pior que pergunta nenhuma.
        return await db.PesquisasNps.AsNoTracking()
            .Where(p => p.ContatoId == contatoId
                     && p.Status == StatusPesquisaNps.PossivelNota
                     && p.Nota != null)
            .OrderByDescending(p => p.DataResposta)
            .ThenByDescending(p => p.Id)
            .Select(p => new NotaEmDuvida(
                p.Id,
                p.Nota!.Value,
                db.Mensagens.Where(m => m.Id == p.MensagemRespostaId).Select(m => m.Texto).FirstOrDefault(),
                p.DataResposta))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>===================== QUEM DECIDE E O UPDATE, E SO ELE =====================
    ///
    /// ⚠️ ERA "LER, CONFERIR, SALVAR", e isso deixava a acao correr DUAS VEZES. Dono e vendedor
    /// clicando "E nota 2" ao mesmo tempo (ou a mesma pessoa em duas abas) liam os dois
    /// `PossivelNota`, os dois salvavam `Respondida`, e os dois chamavam as acoes: lembrete do
    /// detrator em dobro e duas mensagens ao cliente. O `IAcoesDaNota` exige o contrario — a acao
    /// so corre quando um UPDATE condicional mudou UMA linha —, e a leitura automatica ja seguia.
    ///
    /// Agora o UPDATE vem PRIMEIRO, com o estado no `WHERE`. No Postgres a segunda confirmacao
    /// espera a primeira e reavalia o `WHERE` contra a linha ja `respondida`: zero linhas. So
    /// quando ninguem mudou e que a linha e LIDA — para escolher a frase do erro, nao para decidir.
    /// Nao ha janela entre "conferir" e "gravar" porque nao ha mais o "conferir".
    ///
    /// A trilha e o fato na MESMA transacao: o UPDATE direto nao passa pelo interceptor, e a
    /// declaracao entra no `SaveChanges` logo depois. As acoes ficam FORA, depois do commit — elas
    /// mandam WhatsApp, e mensagem enviada nao volta atras com rollback.
    /// =================================================================================</summary>
    public async Task ConfirmarNotaAsync(long pesquisaId, CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;

        // ⚠️ E ESTA COLUNA E O QUE PERMITE MEDIR O LEITOR DEPOIS: muita confirmacao manual quer
        // dizer que as regras do `LeitorDeNota` estao estreitas demais.
        long? quem = contexto.UsuarioId == 0 ? null : contexto.UsuarioId;

        await using var tx = db.Database.CurrentTransaction == null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        // Sem `IgnoreQueryFilters`: roda na requisicao, e pesquisa de outra empresa nao e achada.
        var mudou = await db.PesquisasNps
            .Where(x => x.Id == pesquisaId
                     && x.Status == StatusPesquisaNps.PossivelNota
                     && x.Nota != null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, StatusPesquisaNps.Respondida)
                // A hora em que o CLIENTE respondeu, gravada quando a duvida nasceu — e nao a do
                // clique. So a duvida de antes dessa regra chega sem ela, e fica com a do clique.
                .SetProperty(x => x.DataResposta, x => x.DataResposta ?? agora)
                .SetProperty(x => x.ConfirmadaPorUsuarioId, quem), ct);

        if (mudou == 0) await RecusarConfirmacaoAsync(pesquisaId, ct);

        var p = await db.PesquisasNps.AsNoTracking()
            .Where(x => x.Id == pesquisaId)
            .Select(x => new { x.ContatoId, x.Nota })
            .SingleAsync(ct);

        // ===================== A TRILHA, COM A NOTA DENTRO =====================
        // ⚠️ ESTE E O UNICO PONTO EM QUE UMA PESSOA MUDA UM NUMERO DE RELATORIO COM UM CLIQUE.
        // "Quem disse que aquele 2 era uma nota" e pergunta que aparece quando a metrica do mes nao
        // fecha, e sem o valor na linha a trilha diria so "alguem confirmou algo".
        // ======================================================================
        trilha.Declarar(
            EntidadeAuditada.Contato, p.ContatoId, AcaoAuditoria.Resolveu,
            new Dictionary<string, AlteracaoValor>
            {
                ["notaNps"] = new(null, p.Nota)
            });

        await db.SaveChangesAsync(ct);
        if (tx != null) await tx.CommitAsync(ct);

        // ⚠️ AS ACOES DA FAIXA CORREM AQUI TAMBEM, e so para quem MUDOU a linha: confirmar nota 2
        // na mao tem de criar o lembrete do detrator igual a nota 2 lida sozinha. Sem isto, o aviso
        // dependeria de o leitor ter acertado — e a `PossivelNota` existe justamente para quando ele
        // nao acertou.
        await acoes.ExecutarAsync(pesquisaId, ct);
    }

    /// <summary>O UPDATE nao pegou: a frase do erro sai do estado ATUAL da linha. Sempre lanca.</summary>
    private async Task RecusarConfirmacaoAsync(long pesquisaId, CancellationToken ct)
    {
        var atual = await db.PesquisasNps.AsNoTracking()
            .Where(x => x.Id == pesquisaId)
            .Select(x => new { x.Status, x.Nota })
            .FirstOrDefaultAsync(ct);

        if (atual == null)
            throw new RegraDeNegocioException("Pesquisa não encontrada.");

        if (atual.Status == StatusPesquisaNps.Respondida)
            throw new RegraDeNegocioException(
                "Esta nota já foi registrada — talvez por outra pessoa agora há pouco.", conflito: true);

        if (atual.Status != StatusPesquisaNps.PossivelNota)
            throw new RegraDeNegocioException(
                "Esta pesquisa não está aguardando confirmação.", conflito: true);

        throw new RegraDeNegocioException("Não há nota para confirmar.", conflito: true);
    }

    public async Task NaoEhNotaAsync(long pesquisaId, CancellationToken ct)
    {
        var p = await CarregarAsync(pesquisaId, ct);

        if (p.Status != StatusPesquisaNps.PossivelNota)
            throw new RegraDeNegocioException(
                "Esta pesquisa não está aguardando confirmação.", conflito: true);

        trilha.Declarar(
            EntidadeAuditada.Contato, p.ContatoId, AcaoAuditoria.Resolveu,
            new Dictionary<string, AlteracaoValor>
            {
                ["notaNps"] = new(p.Nota, null)
            });

        p.Status = StatusPesquisaNps.Enviada;

        // ⚠️ A SUSPEITA SAI, e nao so o status: uma `nota` pendurada numa pesquisa `enviada` seria
        // lida como resultado pelo proximo que olhasse a linha, e o check do banco NAO barra —
        // `ck_pesquisas_nps_respondida` so exige nota em `respondida`.
        //
        // A `mensagem_resposta_id` sai junto, pelo mesmo motivo: ela aponta para a mensagem que
        // *parecia* ser a resposta.
        p.Nota = null;
        p.MensagemRespostaId = null;
        // E a hora da resposta tambem: nao houve resposta. Deixa-la faria a pesquisa `enviada`
        // carregar a hora de uma mensagem que nao era nota.
        p.DataResposta = null;

        // A pesquisa segue esperando, e expira no prazo normal se a nota nao vier. `DataEnvio` nao
        // e tocada: o relogio da expiracao conta de quando a PERGUNTA saiu.
        await db.SaveChangesAsync(ct);
    }

    public async Task CancelarAsync(long pesquisaId, CancellationToken ct)
    {
        var p = await CarregarAsync(pesquisaId, ct);

        if (p.Status == StatusPesquisaNps.Respondida)
            throw new RegraDeNegocioException(
                "Esta pesquisa já foi respondida.", conflito: true);

        if (p.Status == StatusPesquisaNps.Cancelada)
            throw new RegraDeNegocioException("Esta pesquisa já está cancelada.", conflito: true);

        trilha.Declarar(EntidadeAuditada.Contato, p.ContatoId, AcaoAuditoria.Cancelou);

        p.Status = StatusPesquisaNps.Cancelada;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>⚠️ SEM `IgnoreQueryFilters`: este servico roda na REQUISICAO, com tenant no
    /// contexto. A pesquisa de outra empresa nao volta, e e o filtro global que garante.</summary>
    private async Task<PesquisaNps> CarregarAsync(long id, CancellationToken ct) =>
        await db.PesquisasNps.FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw new RegraDeNegocioException("Pesquisa não encontrada.");
}
