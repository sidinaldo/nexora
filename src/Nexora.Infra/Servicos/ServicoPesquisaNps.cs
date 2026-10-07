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
    public async Task ConfirmarNotaAsync(long pesquisaId, CancellationToken ct)
    {
        var p = await CarregarAsync(pesquisaId, ct);

        if (p.Status != StatusPesquisaNps.PossivelNota)
            throw new RegraDeNegocioException(
                "Esta pesquisa não está aguardando confirmação.", conflito: true);

        if (p.Nota == null)
            throw new RegraDeNegocioException("Não há nota para confirmar.", conflito: true);

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

        p.Status = StatusPesquisaNps.Respondida;
        p.DataResposta = relogio.GetUtcNow().UtcDateTime;

        // ⚠️ E ESTA COLUNA E O QUE PERMITE MEDIR O LEITOR DEPOIS: muita confirmacao manual quer
        // dizer que as regras do `LeitorDeNota` estao estreitas demais.
        p.ConfirmadaPorUsuarioId = contexto.UsuarioId == 0 ? null : contexto.UsuarioId;

        await db.SaveChangesAsync(ct);

        // ⚠️ AS ACOES DA FAIXA CORREM AQUI TAMBEM, e DEPOIS do SaveChanges: confirmar nota 2 na mao
        // tem de criar o lembrete do detrator igual a nota 2 lida sozinha. Sem isto, o aviso
        // dependeria de o leitor ter acertado — e a `PossivelNota` existe justamente para quando ele
        // nao acertou.
        await acoes.ExecutarAsync(p.Id, ct);
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
