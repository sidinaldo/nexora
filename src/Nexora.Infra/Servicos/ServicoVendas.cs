using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O historico de vendas (NEG-1).
///
/// Ler e cancelar. QUEM GRAVA e o `ServicoContatos.MarcarGanhoAsync`, junto do carimbo e na mesma
/// transacao — separar a gravacao aqui criaria duas portas para o mesmo fato, e a chance de uma
/// delas ser chamada sozinha.</summary>
public class ServicoVendas(
    NexoraDbContext db, IContextoEmpresa contexto, ColetorAuditoria trilha, TimeProvider relogio)
    : IServicoVendas
{
    public async Task<IReadOnlyList<VendaDto>> DoContatoAsync(long contatoId, CancellationToken ct)
    {
        // Canceladas VEM JUNTO, com o carimbo: a lista as mostra riscadas. Filtra-las aqui faria
        // a linha sumir da tela, e quem confere o mes depois nao teria como saber que existiu.
        return await db.Vendas.AsNoTracking()
            .Where(v => v.ContatoId == contatoId)
            .OrderByDescending(v => v.FechadaEm).ThenByDescending(v => v.Id)
            .Select(v => new VendaDto(
                v.Id, v.Valor, v.FechadaEm, v.ResponsavelId,
                v.Responsavel == null ? null : v.Responsavel.Nome,
                v.Observacao, v.CanceladaEm,
                v.Status.ToString().ToLower(), v.ConcluidaEm))
            .ToListAsync(ct);
    }

    // ==================================================================== NEG-2
    public async Task<int> ConcluirAsync(IReadOnlyList<long> vendaIds, CancellationToken ct)
    {
        if (vendaIds.Count == 0) return 0;

        var agora = relogio.GetUtcNow().UtcDateTime;
        var quem = contexto.UsuarioId == 0 ? (long?)null : contexto.UsuarioId;

        // Os donos dos pedidos, lidos ANTES do UPDATE — depois dele o `status` mudou e nao ha
        // como reencontra-los pelo mesmo predicado. Le com o filtro de tenant ligado, entao id
        // de outra empresa nao traz contato nenhum para a liberacao abaixo.
        var contatos = await db.Vendas.AsNoTracking()
            .Where(v => vendaIds.Contains(v.Id) && v.Status == StatusVenda.Fechada)
            .Select(v => v.ContatoId)
            .Distinct()
            .ToListAsync(ct);

        // ===================== UM UPDATE, NAO UM LACO =====================
        // O lote existe justamente para o vendedor concluir trinta de uma vez; trinta idas ao
        // banco seriam trinta transacoes e trinta chances de parar no meio.
        //
        // `Status == Fechada` no WHERE, e nao uma checagem antes: e o que torna a operacao
        // IDEMPOTENTE e segura contra corrida. Se outra pessoa concluiu no meio, aquela linha
        // simplesmente nao e afetada — e o retorno diz quantas de fato mudaram.
        //
        // O query filter global restringe ao tenant, entao id de outra empresa afeta zero linhas.
        // =================================================================
        var quantas = await db.Vendas
            .Where(v => vendaIds.Contains(v.Id) && v.Status == StatusVenda.Fechada)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, StatusVenda.Concluida)
                .SetProperty(v => v.ConcluidaEm, agora)
                .SetProperty(v => v.ConcluidaPor, quem), ct);

        // ⚠️ O CONTATO NAO E TOCADO. `ganho_em` e `valor` ficam: concluir e sobre o PEDIDO, nao
        // sobre o negocio. Limpar o carimbo faria o kanban devolver o card para "Novo Lead" —
        // o contato pareceria reaberto, que e o oposto de "acabou".

        // NEG-3: acabou o pedido, a conversa volta para a fila. DEPOIS do UPDATE, e nao antes:
        // o `NOT EXISTS` la dentro precisa enxergar as vendas que acabaram de sair de 'fechada',
        // e no mesmo comando elas ainda pareceriam abertas. Ver `LiberacaoDeCiclo`.
        if (quantas > 0)
            await LiberacaoDeCiclo.ExecutarAsync(db, contatos, agora, ct);

        foreach (var id in vendaIds)
            trilha.Declarar(EntidadeAuditada.Venda, id, AcaoAuditoria.Concluiu);

        // `ExecuteUpdate` nao passa pelo interceptor da trilha (e SQL cru), entao o SaveChanges
        // abaixo e o que grava os eventos declarados acima.
        await db.SaveChangesAsync(ct);

        return quantas;
    }

    public async Task<int> ConcluirDoContatoAsync(
        IReadOnlyList<long> contatoIds, CancellationToken ct)
    {
        if (contatoIds.Count == 0) return 0;

        // Le os ids e DELEGA, em vez de repetir o UPDATE com outro predicado: a trilha precisa
        // do id de cada venda, e duas versoes da mesma escrita divergiriam no dia em que uma
        // delas mudasse. Uma ida a mais ao banco; o lote continua sendo um UPDATE so.
        var ids = await db.Vendas.AsNoTracking()
            .Where(v => contatoIds.Contains(v.ContatoId) && v.Status == StatusVenda.Fechada)
            .Select(v => v.Id)
            .ToListAsync(ct);

        return await ConcluirAsync(ids, ct);
    }

    public async Task CancelarAsync(long vendaId, CancellationToken ct)
    {
        // ===================== POR QUE SO DONO E GESTOR =====================
        // Cancelar tira faturamento da contagem. Vendedor errar o valor e comum e tem conserto;
        // vendedor apagar a propria meta ruim nao pode ser um clique. Mesma linha de corte do
        // resto do sistema: quem responde pelo numero decide sobre o numero.
        // ====================================================================
        ExigirDonoOuGestor("cancelar uma venda");

        // O query filter ja restringe ao tenant: venda de outra empresa simplesmente nao existe.
        var venda = await db.Vendas.FirstOrDefaultAsync(v => v.Id == vendaId, ct)
            ?? throw new RegraDeNegocioException("Venda não encontrada.");

        if (venda.Status == StatusVenda.Cancelada)
            throw new RegraDeNegocioException("Esta venda já está cancelada.", conflito: true);

        var agora = relogio.GetUtcNow().UtcDateTime;

        // NADA de DELETE. Faturamento que some sem rastro e pior que faturamento errado: o
        // primeiro nao tem investigacao possivel.
        venda.Status = StatusVenda.Cancelada;
        venda.CanceladaEm = agora;
        // Mesma razão do `responsavel_id`: 0 não é usuário.
        venda.CanceladaPor = contexto.UsuarioId == 0 ? null : contexto.UsuarioId;

        // O VALOR entra explicitamente: quem lê a trilha quer saber quanto foi desfeito, e o
        // diff sozinho traria só `canceladaEm: null → data`.
        trilha.Declarar(EntidadeAuditada.Venda, venda.Id, AcaoAuditoria.Cancelou,
            new Dictionary<string, AlteracaoValor> { ["valor"] = new(venda.Valor, null) });

        // ===================== O CARIMBO, SE FOR A VIGENTE =====================
        // "Vigente" = o contato esta ganho E esta e a venda MAIS RECENTE nao cancelada dele.
        // Sem isto o card ficaria na etapa de ganho sem venda nenhuma por tras — o estado
        // divergente que a porta unica do funil existe para impedir.
        //
        // Cancelar uma venda ANTIGA (de cliente que ja comprou de novo) nao toca em nada: o
        // carimbo pertence a compra atual.
        //
        // ⚠️ A PRIMEIRA VERSAO COMPARAVA `contato.GanhoEm == venda.FechadaEm`, e um teste a
        // derrubou: duas vendas no MESMO instante — o que acontece sempre que o relogio e
        // controlado, e em producao quando duas chamadas caem no mesmo tick — casavam as duas, e
        // cancelar a antiga limpava o carimbo da nova. Timestamp nao e chave.
        //
        // `ORDER BY fechada_em DESC, id DESC` desempata pelo id, que e monotonico: entre duas do
        // mesmo instante, a vigente e a que foi gravada depois.
        // =======================================================================
        var vigenteId = await db.Vendas.AsNoTracking()
            .Where(v => v.ContatoId == venda.ContatoId && v.Status == StatusVenda.Fechada)
            .OrderByDescending(v => v.FechadaEm).ThenByDescending(v => v.Id)
            .Select(v => (long?)v.Id)
            .FirstOrDefaultAsync(ct);

        var contato = await db.Contatos.FirstOrDefaultAsync(c => c.Id == venda.ContatoId, ct);
        if (contato is not null && contato.GanhoEm is not null && vigenteId == venda.Id)
        {
            contato.GanhoEm = null;
            contato.Valor = null;

            // Devolve o card ao quadro, como faz o `ReabrirAsync` — pelo mesmo motivo.
            var etapaEhGanho = await db.EtapasFunil.AsNoTracking()
                .AnyAsync(e => e.Id == contato.EtapaId && e.EGanho, ct);

            if (etapaEhGanho)
            {
                var primeira = await db.EtapasFunil.AsNoTracking()
                    .OrderBy(e => e.Ordem).Select(e => e.Id).FirstAsync(ct);

                contato.EtapaId = primeira;
                contato.OrdemKanban = await ProximaOrdemAsync(primeira, ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>A linha de corte de quem mexe em faturamento ja registrado.
    ///
    /// Vendedor errar o valor e comum e tem conserto; vendedor apagar a propria meta ruim nao
    /// pode ser um clique. Fica no SERVICO, e nao num `[Authorize(Roles=)]`, para valer tambem
    /// quando outro codigo chamar por dentro.</summary>
    private void ExigirDonoOuGestor(string acao)
    {
        var papel = contexto.Papel ?? "";
        if (!papel.Equals("dono", StringComparison.OrdinalIgnoreCase)
            && !papel.Equals("gestor", StringComparison.OrdinalIgnoreCase))
        {
            throw new RegraDeNegocioException($"Só o dono ou um gestor pode {acao}.");
        }
    }

    /// <summary>O ponto medio depois do ultimo card da coluna. Mesma conta do `ServicoContatos`;
    /// duplicada aqui e nao extraida porque sao quatro linhas e a alternativa seria expor um
    /// helper publico de ordenacao de kanban num servico de faturamento.</summary>
    private async Task<decimal> ProximaOrdemAsync(long etapaId, CancellationToken ct)
    {
        var ultima = await db.Contatos.AsNoTracking()
            .Where(c => c.EtapaId == etapaId && c.PerdidoEm == null)
            .MaxAsync(c => (decimal?)c.OrdemKanban, ct);

        return (ultima ?? 0m) + 1000m;
    }
}
