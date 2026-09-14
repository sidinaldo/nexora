using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O historico de vendas (NEG-1), agora sobre `negociacoes` (E4e).
///
/// ===================== O QUE ESTE ARQUIVO DEIXOU DE SER =====================
/// Ele lia e escrevia a tabela `vendas`. Depois deste bloco, NADA le nem escreve nela — a venda
/// deixou de ser uma linha separada e virou um ESTADO da negociacao:
///
///   `fechada`   -> `ganha`       o negocio fechou e o pedido esta em aberto
///   `concluida` -> `concluida`   o pedido acabou; o dinheiro fica
///   `cancelada` -> `cancelada`   aquilo nao aconteceu; sai do relatorio
///
/// O nome do servico e do DTO continuam "venda" de proposito: e a palavra que o dono usa, e a
/// rota publica `/api/vendas` nao deve trocar de nome por causa de uma mudanca interna.
///
/// ⚠️ OS IDS QUE ELE RECEBE SAO DE NEGOCIACAO, e nao mais de venda. Os dois sao `long`, entao
/// trocar um pelo outro COMPILA — foi por isso que o `VendaDto.Id` passou a ser o id da
/// negociacao no mesmo commit: quem le a lista e quem manda concluir falam do mesmo numero.
/// ============================================================================
///
/// Ler, concluir e cancelar. QUEM ABRE o negocio ganho e o `ServicoContatos.MarcarGanhoAsync`,
/// na mesma transacao do carimbo — separar a gravacao aqui criaria duas portas para o mesmo
/// fato, e a chance de uma delas ser chamada sozinha.</summary>
public class ServicoVendas(
    NexoraDbContext db, IContextoEmpresa contexto, ColetorAuditoria trilha, TimeProvider relogio)
    : IServicoVendas
{
    /// <summary>As negociacoes que VIRARAM venda, que e o que `ganha_em` marca.
    ///
    /// `ganha_em IS NOT NULL` faz o recorte inteiro: aberta e perdida tem a coluna nula e caem
    /// fora sozinhas, sem precisar listar status.</summary>
    public async Task<IReadOnlyList<VendaDto>> DoContatoAsync(long contatoId, CancellationToken ct)
    {
        // Canceladas VEM JUNTO, com o carimbo: a lista as mostra riscadas. Filtra-las aqui faria
        // a linha sumir da tela, e quem confere o mes depois nao teria como saber que existiu.
        return await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == contatoId && n.GanhaEm != null)
            .OrderByDescending(n => n.GanhaEm).ThenByDescending(n => n.Id)
            .Select(n => new VendaDto(
                n.Id, n.Valor ?? 0m, n.GanhaEm!.Value, n.ResponsavelId,
                n.Responsavel == null ? null : n.Responsavel.Nome,
                n.Observacao, n.CanceladaEm,
                n.Status.ToString().ToLower(), n.ConcluidaEm))
            .ToListAsync(ct);
    }

    // ==================================================================== NEG-2
    public async Task<int> ConcluirAsync(IReadOnlyList<long> negociacaoIds, CancellationToken ct)
    {
        if (negociacaoIds.Count == 0) return 0;

        var agora = relogio.GetUtcNow().UtcDateTime;
        var quem = contexto.UsuarioId == 0 ? (long?)null : contexto.UsuarioId;

        // Os donos dos pedidos, lidos ANTES do UPDATE — depois dele o `status` mudou e nao ha
        // como reencontra-los pelo mesmo predicado. Le com o filtro de tenant ligado, entao id
        // de outra empresa nao traz contato nenhum para a liberacao abaixo.
        var contatos = await db.Negociacoes.AsNoTracking()
            .Where(n => negociacaoIds.Contains(n.Id) && n.Status == StatusNegociacao.Ganha)
            .Select(n => n.ContatoId)
            .Distinct()
            .ToListAsync(ct);

        // ===================== UM UPDATE, NAO UM LACO =====================
        // O lote existe justamente para o vendedor concluir trinta de uma vez; trinta idas ao
        // banco seriam trinta transacoes e trinta chances de parar no meio.
        //
        // `Status == Ganha` no WHERE, e nao uma checagem antes: e o que torna a operacao
        // IDEMPOTENTE e segura contra corrida. Se outra pessoa concluiu no meio, aquela linha
        // simplesmente nao e afetada — e o retorno diz quantas de fato mudaram.
        //
        // O query filter global restringe ao tenant, entao id de outra empresa afeta zero linhas.
        // =================================================================
        var quantas = await db.Negociacoes
            .Where(n => negociacaoIds.Contains(n.Id) && n.Status == StatusNegociacao.Ganha)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, StatusNegociacao.Concluida)
                .SetProperty(n => n.ConcluidaEm, agora)
                .SetProperty(n => n.ConcluidaPor, quem), ct);

        // ⚠️ `ganha_em` FICA. Concluir e sobre o PEDIDO, nao sobre o dinheiro: o valor continua
        // no faturamento, e e `ganha_em` que diz em qual mes. Quem tira do relatorio e cancelar.
        //
        // O CARD sai do quadro sozinho, porque `RegrasNegociacao.NoQuadro` so aceita `aberta` e
        // `ganha`. Era isso que o carimbo em `contatos` fazia antes, e por isso ele nao precisa
        // mais existir.

        // NEG-3: acabou o pedido, a conversa volta para a fila. DEPOIS do UPDATE, e nao antes:
        // o `NOT EXISTS` la dentro precisa enxergar as negociacoes que acabaram de sair de
        // `ganha`, e no mesmo comando elas ainda pareceriam abertas. Ver `LiberacaoDeCiclo`.
        //
        // `ExecuteUpdate` vai direto ao banco, entao a leitura crua de la enxerga o que ele fez.
        if (quantas > 0)
            await LiberacaoDeCiclo.ExecutarAsync(db, contatos, agora, ct);

        foreach (var id in negociacaoIds)
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
        // do id de cada negocio, e duas versoes da mesma escrita divergiriam no dia em que uma
        // delas mudasse. Uma ida a mais ao banco; o lote continua sendo um UPDATE so.
        var ids = await db.Negociacoes.AsNoTracking()
            .Where(n => contatoIds.Contains(n.ContatoId) && n.Status == StatusNegociacao.Ganha)
            .Select(n => n.Id)
            .ToListAsync(ct);

        return await ConcluirAsync(ids, ct);
    }

    public async Task CancelarAsync(long negociacaoId, CancellationToken ct)
    {
        // ===================== POR QUE SO DONO E GESTOR =====================
        // Cancelar tira faturamento da contagem. Vendedor errar o valor e comum e tem conserto;
        // vendedor apagar a propria meta ruim nao pode ser um clique. Mesma linha de corte do
        // resto do sistema: quem responde pelo numero decide sobre o numero.
        // ====================================================================
        ExigirDonoOuGestor("cancelar uma venda");

        // O query filter ja restringe ao tenant: negocio de outra empresa simplesmente nao existe.
        var negocio = await db.Negociacoes.FirstOrDefaultAsync(n => n.Id == negociacaoId, ct)
            ?? throw new RegraDeNegocioException("Venda não encontrada.");

        if (negocio.Status == StatusNegociacao.Cancelada)
            throw new RegraDeNegocioException("Esta venda já está cancelada.", conflito: true);

        // ⚠️ SO SE VIROU VENDA. Cancelar uma negociacao ABERTA nao e cancelar venda nenhuma — e
        // perder o negocio, que tem operacao propria e pede motivo. Sem esta recusa, a rota de
        // cancelamento viraria uma porta lateral para tirar card do quadro sem registrar por que.
        if (negocio.GanhaEm is null)
            throw new RegraDeNegocioException(
                "Este negócio ainda não virou venda. Para encerrá-lo, marque como perdido.");

        var agora = relogio.GetUtcNow().UtcDateTime;

        // NADA de DELETE. Faturamento que some sem rastro e pior que faturamento errado: o
        // primeiro nao tem investigacao possivel. O `ganha_em` fica, e quem tira do relatorio e o
        // filtro do indice (`status <> 'cancelada'`), nao o carimbo em branco.
        negocio.Status = StatusNegociacao.Cancelada;
        negocio.CanceladaEm = agora;
        negocio.CanceladaPor = contexto.UsuarioId == 0 ? null : contexto.UsuarioId;

        // O VALOR entra explicitamente: quem le a trilha quer saber quanto foi desfeito, e o
        // diff sozinho traria so `canceladaEm: null → data`.
        trilha.Declarar(EntidadeAuditada.Venda, negocio.Id, AcaoAuditoria.Cancelou,
            new Dictionary<string, AlteracaoValor> { ["valor"] = new(negocio.Valor, null) });

        // ===================== ⚠️ E UM NEGOCIO NOVO, SENAO O CARD SOME =====================
        // A cancelada sai do quadro — certo, aquilo nao aconteceu. Mas o contato precisa VOLTAR,
        // e desde que o quadro le `negociacoes` voltar deixou de ser mover o contato: e precisar
        // de uma negociacao ABERTA.
        //
        // Sem isto o contato sumia do funil inteiro, e o dono achava que tinha perdido o contato.
        // E o mesmo gesto de `ReabrirAsync`: a rodada nova e uma linha nova, e a cancelada fica
        // como historico do que foi desfeito.
        //
        // ⚠️ SO QUANDO NAO SOBRA OUTRA VIVA. Cancelar uma venda ANTIGA de quem ja esta
        // negociando de novo nao pode criar um segundo card — o contato passaria a aparecer duas
        // vezes por causa de um gesto sobre historico.
        // ==================================================================================
        var temOutraViva = await db.Negociacoes.AsNoTracking().AnyAsync(
            n => n.ContatoId == negocio.ContatoId
              && n.Id != negocio.Id
              && (n.Status == StatusNegociacao.Aberta || n.Status == StatusNegociacao.Ganha), ct);

        // ⚠️ O CARIMBO NAO E MAIS LIMPO AQUI, e a razao some junto com a necessidade: este
        // bloco existia porque `MarcarGanhoAsync` recusava quando `contatos.ganho_em` estava
        // preenchido, entao cancelar a venda errada impedia registrar a certa. Desde o E4e/3b a
        // pergunta e "ha negocio ABERTO?" — e a negociacao aberta criada logo abaixo ja e a
        // resposta. Limpar a coluna agora seria escrever numa metade que ninguem le.
        if (!temOutraViva)
        {
            // Volta para o inicio DO PROPRIO funil, nao do funil padrao: cancelar nao e gesto de
            // trocar de pipeline, e mudar o funil do contato aqui seria uma surpresa.
            var primeira = await db.EtapasFunil.AsNoTracking()
                .Where(e => e.PipelineId == negocio.PipelineId && !e.EGanho)
                .OrderBy(e => e.Ordem).Select(e => e.Id).FirstAsync(ct);
            var ordem = await ProximaOrdemAsync(primeira, ct);

            // ⚠️ O CONTATO NAO E MAIS TOCADO AQUI (E4e/4). Ate a coluna cair era preciso
            // reposiciona-lo junto, senao ele ficava registrado na etapa de ganho enquanto o
            // negocio dele estava na primeira, e a lista de contatos mostrava a etapa errada.
            // A negociacao nova abaixo ja e a posicao — nao ha segunda metade para sincronizar.
            db.Negociacoes.Add(new Negociacao
            {
                EmpresaId = negocio.EmpresaId,
                ContatoId = negocio.ContatoId,
                PipelineId = negocio.PipelineId,
                EtapaId = primeira,
                OrdemKanban = ordem,
                ResponsavelId = negocio.ResponsavelId,
                Status = StatusNegociacao.Aberta
            });
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

    /// <summary>O fim da coluna. Le de `negociacoes`, que e onde a posicao mora desde o E4c —
    /// `contatos.ordem_kanban` sai no proximo bloco e ja nao manda em nada que se veja.</summary>
    private async Task<decimal> ProximaOrdemAsync(long etapaId, CancellationToken ct)
    {
        var ultima = await db.Negociacoes.AsNoTracking()
            .Where(n => n.EtapaId == etapaId)
            .Where(RegrasNegociacao.NoQuadro)
            .MaxAsync(n => (decimal?)n.OrdemKanban, ct);

        return (ultima ?? 0m) + 1000m;
    }
}
