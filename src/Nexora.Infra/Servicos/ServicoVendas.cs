using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Core.Seguranca;

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

    public async Task<ResumoVendasContato?> ResumoDoContatoAsync(long contatoId, CancellationToken ct)
    {
        // Uma consulta só, agregada no banco. `GroupBy` por constante é como o EF escreve
        // "COUNT, SUM e MAX sobre o mesmo recorte" num SELECT só.
        var resumo = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == contatoId
                     && n.GanhaEm != null
                     && n.Status != StatusNegociacao.Cancelada)
            .GroupBy(n => 1)
            .Select(g => new
            {
                Quantidade = g.Count(),
                Total = g.Sum(n => n.Valor ?? 0m),
                UltimaEm = g.Max(n => n.GanhaEm)
            })
            .FirstOrDefaultAsync(ct);

        if (resumo == null)
        {
            return null;
        }

        return new ResumoVendasContato(resumo.Quantidade, resumo.Total, resumo.UltimaEm);
    }

    // ==================================================================== NEG-2
    public async Task<int> ConcluirAsync(IReadOnlyList<long> negociacaoIds, CancellationToken ct)
    {
        if (negociacaoIds.Count == 0) return 0;

        var agora = relogio.GetUtcNow().UtcDateTime;
        var quem = contexto.UsuarioId == 0 ? (long?)null : contexto.UsuarioId;

        // ===================== UM UPDATE, NAO UM LACO =====================
        // O lote existe justamente para o vendedor concluir trinta de uma vez; trinta idas ao
        // banco seriam trinta transacoes e trinta chances de parar no meio.
        //
        // `status = 'ganha'` no WHERE, e nao uma checagem antes: e o que torna a operacao
        // IDEMPOTENTE e segura contra corrida. Se outra pessoa concluiu no meio, aquela linha
        // simplesmente nao e afetada.
        //
        // ⚠️ `RETURNING` (BUG-XX): o lote gravava "Concluiu" na trilha para TODOS os ids pedidos,
        // inclusive os que ja estavam concluidos e nao mudaram. Agora a trilha e a liberacao saem
        // so do que o UPDATE de fato mudou. SQL cru, entao o tenant vai explicito no WHERE.
        // =================================================================
        var mudaram = await db.Database.SqlQueryRaw<long>("""
            UPDATE negociacoes
               SET status = 'concluida', concluida_em = @agora, concluida_por = @quem
             WHERE empresa_id = @empresa AND id = ANY(@ids) AND status = 'ganha'
            RETURNING id AS "Value"
            """,
            new NpgsqlParameter("agora", agora),
            new NpgsqlParameter("quem", NpgsqlDbType.Bigint) { Value = (object?)quem ?? DBNull.Value },
            new NpgsqlParameter("empresa", contexto.EmpresaId),
            new NpgsqlParameter("ids", negociacaoIds.ToArray())).ToListAsync(ct);

        var contatos = await db.Negociacoes.AsNoTracking()
            .Where(n => mudaram.Contains(n.Id))
            .Select(n => n.ContatoId)
            .Distinct()
            .ToListAsync(ct);

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
        if (mudaram.Count > 0)
            await LiberacaoDeCiclo.ExecutarAsync(db, contatos, agora, ct);

        foreach (var id in mudaram)
            trilha.Declarar(EntidadeAuditada.Venda, id, AcaoAuditoria.Concluiu);

        // `ExecuteUpdate` nao passa pelo interceptor da trilha (e SQL cru), entao o SaveChanges
        // abaixo e o que grava os eventos declarados acima.
        await db.SaveChangesAsync(ct);

        return mudaram.Count;
    }

    public async Task CancelarAsync(long negociacaoId, string? motivo, CancellationToken ct)
    {
        // ===================== POR QUE SO DONO E GESTOR =====================
        // Cancelar tira faturamento da contagem. Vendedor errar o valor e comum e tem conserto;
        // vendedor apagar a propria meta ruim nao pode ser um clique. Mesma linha de corte do
        // resto do sistema: quem responde pelo numero decide sobre o numero.
        // ====================================================================
        contexto.Exigir(Permissao.CancelarVenda, "Você não tem permissão para cancelar vendas. Peça ao dono da conta.");

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
                "Esta negociação ainda não virou venda. Para encerrá-la, marque como perdida.");

        var agora = relogio.GetUtcNow().UtcDateTime;

        // NADA de DELETE. Faturamento que some sem rastro e pior que faturamento errado: o
        // primeiro nao tem investigacao possivel. O `ganha_em` fica, e quem tira do relatorio e o
        // filtro do indice (`status <> 'cancelada'`), nao o carimbo em branco.
        // ===================== O PORQUE, E O QUE ELE DECIDE (CAN-1) =====================
        // Vazio e nulo sao a MESMA coisa aqui, e viram nulo: "registrei errado". Guardar uma
        // string em branco deixaria a coluna dizendo "houve um motivo" sem motivo nenhum, e o
        // relatorio ganharia uma linha de perda com nome vazio.
        //
        // ⚠️ O MOTIVO E O SINAL. Nao ha flag separada dizendo "isto foi perda": se existe um
        // porque escrito, e porque alguem perdeu alguma coisa. Ver o comentario longo em
        // `Negociacao.CancelamentoMotivo`.
        // ===============================================================================
        var porque = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim();
        var foiPerda = porque is not null;

        negocio.Status = StatusNegociacao.Cancelada;
        negocio.CanceladaEm = agora;
        negocio.CanceladaPor = contexto.UsuarioId == 0 ? null : contexto.UsuarioId;
        negocio.CancelamentoMotivo = porque;

        // O VALOR entra explicitamente: quem le a trilha quer saber quanto foi desfeito, e o
        // diff sozinho traria so `canceladaEm: null → data`.
        trilha.Declarar(EntidadeAuditada.Venda, negocio.Id, AcaoAuditoria.Cancelou,
            new Dictionary<string, AlteracaoValor> { ["valor"] = new(negocio.Valor, null) });

        // ===================== E NA LINHA DO TEMPO DO CONTATO TAMBEM =====================
        // `Ganhou`, `Perdeu`, `Reabriu` e `Abriu` sempre foram declarados sobre o CONTATO. Só
        // `Cancelou` ficava de fora, e o resultado era uma assimetria que tirava a credibilidade
        // do historico inteiro: quem abria a tela do contato via "marcou venda fechada" e NUNCA
        // o desfazimento. A venda nascia e nao morria.
        //
        // E e justamente o cancelamento que alguem vai querer auditar depois — ele reescreve um
        // mes ja fechado, tirando faturamento que o relatorio ja tinha mostrado.
        //
        // ⚠️ SAO DOIS EVENTOS DE PROPOSITO, NAO DUPLICACAO. A trilha da VENDA responde "quem
        // desfez quanto"; a do CONTATO responde "o que aconteceu com esta pessoa". As duas telas
        // leem por (entidade, id) e nenhuma mostra a outra — mover o evento em vez de somar
        // deixaria a primeira pergunta sem resposta.
        //
        // A frase ja existia no painel ("cancelou a venda", em `contato.ts`), esperando por um
        // evento que nunca chegava.
        //
        // ⚠️ `Concluiu` CONTINUA SO NA VENDA, e isso e decisao, nao esquecimento: concluir e o
        // fim normal do pedido e nao mexe em numero nenhum. Toda conclusao viraria uma linha a
        // mais no historico de todo cliente que compra — ruido que empurra para fora da tela
        // justamente os eventos que importam.
        trilha.Declarar(EntidadeAuditada.Contato, negocio.ContatoId, AcaoAuditoria.Cancelou,
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
        // ⚠️ `PipelineId` ENTROU NO FILTRO. Sem ele a pergunta era "esta pessoa tem outro negocio
        // vivo em QUALQUER funil?", e cancelar a venda de Vendas nao devolvia o card porque ela
        // tinha um aberto em Pos-venda — dois funis independentes, e um decidia pelo outro.
        //
        // Com o recorte por funil a pergunta fica certa nos dois sentidos: nao deixa o funil sem
        // card quando devia ter um, e nao cria o segundo quando ja ha
        // (`uq_negociacoes_card_por_funil`).
        // ⚠️ `Aberta` OU `Ganha`, NESTE funil. A pergunta e "ja ha um card desta pessoa aqui?" —
        // se ha, criar outro violaria `uq_negociacoes_card_por_funil`.
        //
        // O `PipelineId` no filtro nao e detalhe: sem ele a pergunta era "tem outro vivo em
        // QUALQUER funil?", e cancelar a venda de Vendas nao devolvia o card porque a pessoa
        // tinha um aberto em Pos-venda — dois funis independentes, e um decidia pelo outro.
        var temOutraViva = await db.Negociacoes.AsNoTracking().AnyAsync(
            n => n.ContatoId == negocio.ContatoId
              && n.PipelineId == negocio.PipelineId
              && n.Id != negocio.Id
              && (n.Status == StatusNegociacao.Aberta || n.Status == StatusNegociacao.Ganha), ct);

        // ⚠️ O CARIMBO NAO E MAIS LIMPO AQUI, e a razao some junto com a necessidade: este
        // bloco existia porque `MarcarGanhoAsync` recusava quando `contatos.ganho_em` estava
        // preenchido, entao cancelar a venda errada impedia registrar a certa. Desde o E4e/3b a
        // pergunta e "ha negocio ABERTO?" — e a negociacao aberta criada logo abaixo ja e a
        // resposta. Limpar a coluna agora seria escrever numa metade que ninguem le.
        // ⚠️ `!foiPerda` ENTROU NA CONDICAO, e e a metade perigosa deste bloco.
        //
        // "Registrei errado" devolve o card: a pessoa continua negociando, e sem isto ela sumia do
        // funil inteiro e o dono achava que tinha perdido o contato — e o defeito que o bloco
        // nasceu para impedir, e ele continua impedido.
        //
        // "O cliente desistiu" NAO devolve: o negocio acabou. Devolver aqui poria de volta no
        // quadro, como oportunidade viva, alguem que ja foi embora — e cedo ou tarde um vendedor
        // ligaria para cobrar uma venda que o proprio sistema sabe que morreu.
        //
        // Quem quiser voltar a negociar com essa pessoa usa "Abrir negociacao", que e o gesto de
        // dizer que ha uma conversa nova — e nao um efeito colateral de desfazer a antiga.
        if (!foiPerda && !temOutraViva)
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
