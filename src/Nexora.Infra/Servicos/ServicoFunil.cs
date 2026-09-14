using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Webhooks;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O quadro kanban: leitura paginada por coluna e o cálculo de posição do card.</summary>
public class ServicoFunil(
    NexoraDbContext db, IPublicadorEventos eventos, ColetorAuditoria trilha) : IServicoFunil
{
    /// <summary>Abaixo desta distância entre vizinhos, a coluna é renormalizada antes de calcular
    /// o ponto médio.
    ///
    /// ===================== A ARMADILHA DO PONTO MÉDIO =====================
    /// Dividir ao meio repetidamente entre os MESMOS dois vizinhos encolhe o intervalo
    /// exponencialmente. Chega uma hora em que o "meio" arredonda para um dos extremos e o card
    /// para de aceitar reordenação — sem erro, sem log, sem nada. É o tipo de bug que ninguém
    /// liga ao commit certo, porque ele aparece meses depois num único card.
    ///
    /// NOTA SOBRE O TIPO DA COLUNA: o prompt deste bloco assume `numeric(18,6)`, que esgotaria em
    /// ~19 movimentos. O schema que foi construído usa `numeric` SEM escala (ver o comentário em
    /// Contato.OrdemKanban) — no Postgres isso vai a milhares de casas decimais, e o limite real
    /// passa a ser o `decimal` do C#, com ~28 dígitos significativos: mais de 90 divisões no
    /// mesmo par. Ou seja, na prática isto quase nunca dispara.
    ///
    /// O limiar fica em 2e-6 assim mesmo, e não é excesso de zelo mal calibrado: renormalizar é
    /// barato (um UPDATE por linha da coluna, numa operação que já é interativa e rara) e mantém
    /// as ordens em números legíveis. Depurar uma coluna cujos valores são 3.0000019073486328125
    /// é bem pior do que renormalizar cedo demais.
    /// =====================================================================</summary>
    public const decimal LimiarRenormalizacao = 0.000002m;

    // ==================================================================== leitura
    public async Task<QuadroFunil> QuadroAsync(long pipelineId, int porColuna, CancellationToken ct)
    {
        porColuna = Math.Clamp(porColuna, 1, 200);

        var etapas = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == pipelineId)
            .OrderBy(e => e.Ordem)
            .Select(e => new
            {
                e.Id, e.Nome, e.Ordem, e.Cor, e.EGanho,
                // Contagem e soma AGREGADAS NO SQL, sobre o conjunto inteiro da coluna — não
                // sobre a página. O cabeçalho mostra "38 · R$ 47.500" com 50 cards carregados.
                //
                // ===================== A FONTE MUDOU, O RECORTE NÃO (E4c) =====================
                // O que se perguntava a `contatos`/`vendas` agora se pergunta a `negociacoes`, e
                // a tradução está escrita em `RegrasNegociacao`:
                //
                //   `RegrasContato.NoQuadro` (perdido_em IS NULL)  ->  Status == Aberta
                //   `ComVendaEmAberto` (tem venda `fechada`)       ->  Status == Ganha
                //
                // Os predicados continuam vindo de UMA `Expression` compartilhada, pelo mesmo
                // motivo de antes: escritos por extenso em dois serviços eles já divergiram, e o
                // cliente viu o dashboard dizer 72 onde o quadro mostrava 69.
                //
                // ⚠️ NÃO HÁ MAIS "UM CARD POR CONTATO" (E4c/2). Quem reabre depois de ganhar
                // aparece duas vezes: a negociação ganha esperando conclusão, e a nova aberta.
                // É o estado que o modelo velho não sabia representar, e mostrá-lo é o ponto do
                // E4 — o vendedor vê que há pedido pendente enquanto negocia de novo.
                // ============================================================================
                Total = db.Negociacoes
                    .Where(RegrasNegociacao.NoQuadro)
                    .Where(n => (e.EGanho && n.Status == StatusNegociacao.Ganha)
                             || (!e.EGanho && n.Status == StatusNegociacao.Aberta))
                    .Count(n => n.EtapaId == e.Id),
                ValorTotal = db.Negociacoes
                    .Where(RegrasNegociacao.NoQuadro)
                    .Where(n => (e.EGanho && n.Status == StatusNegociacao.Ganha)
                             || (!e.EGanho && n.Status == StatusNegociacao.Aberta))
                    .Where(n => n.EtapaId == e.Id)
                    .Sum(n => (decimal?)n.Valor),
                // O QUE JA FOI CONCLUIDO, agregado no SQL. SEM `CardVigente`: é histórico de
                // pedido, e contato com três concluídas conta três — exatamente como contava
                // sobre `vendas`.
                Concluidas = db.Negociacoes.Count(
                    n => n.Status == StatusNegociacao.Concluida && n.EtapaId == e.Id)
            })
            .ToListAsync(ct);

        var colunas = new List<ColunaFunil>(etapas.Count);

        // Uma consulta por coluna. A alternativa — uma consulta só com ROW_NUMBER() particionado —
        // traria tudo de uma vez, mas o EF não expressa window function sem SQL cru, e são 5
        // consultas indexadas contra ix_contatos_kanban. Não vale o SQL cru aqui.
        foreach (var e in etapas)
        {
            var pagina = await ColunaAsync(e.Id, null, null, porColuna, ct);
            colunas.Add(new ColunaFunil(
                e.Id, e.Nome, e.Ordem, e.Cor, e.EGanho,
                e.Total, e.ValorTotal ?? 0m, e.Concluidas, pagina.Itens, pagina.TemMais));
        }

        return new QuadroFunil(colunas);
    }

    public async Task<PaginaCursor<CardFunil>> ColunaAsync(
        long etapaId, decimal? cursorOrdem, long? cursorId, int tamanho, CancellationToken ct)
    {
        tamanho = Math.Clamp(tamanho, 1, 200);

        // ===================== A COLUNA DE GANHO E FILTRADA (NEG-2) =====================
        // A pergunta precisa ser feita aqui e nao so no `QuadroAsync`: esta e a chamada que a
        // paginacao do cliente usa direto, e se ela nao filtrasse, rolar a coluna traria de volta
        // os cards que o cabecalho ja nao conta.
        //
        // Uma consulta a mais por pagina, contra a PK de `etapas_funil`.
        // ===============================================================================
        var eGanho = await db.EtapasFunil.AsNoTracking()
            .AnyAsync(e => e.Id == etapaId && e.EGanho, ct);

        var q = db.Negociacoes.AsNoTracking()
            .Where(RegrasNegociacao.NoQuadro)
            .Where(n => n.EtapaId == etapaId);

        // O recorte por coluna: ganho mostra o que fechou e ainda não concluiu; as outras, o que
        // está em negociação. É a tradução de `RegrasContato.ComVendaEmAberto`.
        q = eGanho
            ? q.Where(n => n.Status == StatusNegociacao.Ganha)
            : q.Where(n => n.Status == StatusNegociacao.Aberta);

        // CURSOR POR VALOR, no par exato da ordenação. Offset não serve aqui: esta é literalmente
        // a tela onde o vendedor arrasta cards, e entre duas páginas a coluna pode ter sido
        // reordenada.
        //
        // O desempate é o id da NEGOCIAÇÃO, que é o que o cliente devolve como cursor desde o
        // E4c/2 — e o mesmo par do `ix_negociacoes_kanban`.
        if (cursorOrdem is { } co)
        {
            var cid = cursorId ?? long.MinValue;
            q = q.Where(n => n.OrdemKanban > co || (n.OrdemKanban == co && n.Id > cid));
        }

        var linhas = await q
            .OrderBy(n => n.OrdemKanban).ThenBy(n => n.Id)
            .Take(tamanho + 1)   // +1 sonda se há próxima página
            .Select(n => new
            {
                n.Id,
                n.ContatoId,
                n.Contato.Nome,
                n.Contato.Telefone,
                n.OrdemKanban,
                n.Valor,
                // O `xmin` DA NEGOCIAÇÃO: é a linha dela que o arrasto atualiza, e é ela que
                // o UPDATE precisa proteger.
                n.Versao,
                n.ResponsavelId,
                ResponsavelNome = n.Responsavel == null ? null : n.Responsavel.Nome,
                // `uq_conversas_contato` e unico por contato, entao continua sendo um lookup de
                // indice por card; o nome do canal sai de uma tabela de dezenas de linhas.
                Conversa = db.Conversas
                    .Where(v => v.ContatoId == n.ContatoId)
                    .Select(v => new
                    {
                        v.Id, v.AguardandoDesde, v.NaoLidas, v.UltimaMensagemEm,
                        CanalDoCiclo = v.CanalCiclo == null ? null : v.CanalCiclo.Nome
                    })
                    .FirstOrDefault(),
                // Colecao materializada, ao contrario das duas acima — aqui os NOMES sao o dado,
                // nao a contagem. O EF resolve numa segunda consulta por PAGINA, nao uma por card.
                //
                // ⚠️ As etiquetas continuam vindo do CONTATO: "Revendedor" e "VIP" sao da pessoa
                // e valem em qualquer negocio dela. As do NEGOCIO ("Urgente") sao a outra metade,
                // e ainda nao existem — `negociacoes_etiquetas` e outro bloco.
                Etiquetas = n.Contato.Etiquetas
                    .OrderBy(x => x.Etiqueta.Nome)
                    .Select(x => new EtiquetaDto(x.Etiqueta.Id, x.Etiqueta.Nome, x.Etiqueta.Cor))
                    .ToList()
            })
            .ToListAsync(ct);

        var temMais = linhas.Count > tamanho;

        var cards = linhas.Take(tamanho).Select(c => new CardFunil(
            c.Id, c.ContatoId, c.Nome, c.Telefone, c.OrdemKanban, c.Valor,
            c.ResponsavelId, c.ResponsavelNome,
            c.Conversa?.Id, c.Conversa?.AguardandoDesde, c.Conversa?.NaoLidas ?? 0,
            c.Conversa?.UltimaMensagemEm, c.Conversa?.CanalDoCiclo, c.Versao,
            c.Etiquetas)).ToList();

        return new PaginaCursor<CardFunil>(cards, temMais);
    }

    // ==================================================================== mover
    public async Task<decimal> MoverAsync(
        long negociacaoId, MoverContato destino, CancellationToken ct)
    {
        var negociacao = await db.Negociacoes
            .Include(n => n.Contato)
            .FirstOrDefaultAsync(n => n.Id == negociacaoId, ct)
            ?? throw new RegraDeNegocioException("Negócio não encontrado.");

        var contato = negociacao.Contato;

        if (contato.AnonimizadoEm is not null)
            throw new RegraDeNegocioException(
                "Este contato foi anonimizado e não aparece mais no funil.", conflito: true);

        // ===================== SÓ A NEGOCIAÇÃO ABERTA SE MOVE (E4c/2) =====================
        // Antes a recusa era "este contato está perdido"; ela virou esta, e cobre mais:
        //
        //   Perdida    o negócio acabou — reabrir é o caminho, como antes
        //   Concluída  o pedido acabou; a etapa vira registro de ONDE fechou
        //   Ganha      ⚠️ ESTE É O CASO NOVO, e antes ele passava
        //
        // Arrastar um card da coluna de ganho para uma coluna comum deixava `ganho_em` carimbado
        // com o card fora da etapa de ganho — o estado divergente que a "porta única do ganho"
        // existe para impedir, entrando pela porta de trás. Agora a posição da negociação ganha é
        // o registro de onde ela fechou, e ela não se move.
        // ==============================================================================
        if (negociacao.Status != StatusNegociacao.Aberta)
            throw new RegraDeNegocioException(
                negociacao.Status == StatusNegociacao.Perdida
                    ? "Este negócio está marcado como perdido. Reabra antes de movê-lo."
                    : "Este negócio já foi fechado e não se move mais no quadro.",
                conflito: true);

        // Etapa DESTA empresa. O query filter protege a leitura; um id vindo do cliente precisa
        // de checagem explícita — sem isso, um id de outro tenant passaria e o card sairia do
        // funil da própria empresa.
        var etapa = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.Id == destino.EtapaId)
            .Select(e => new { e.Id, e.EGanho, e.PipelineId })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Etapa não encontrada.");

        // ===== A RECUSA QUE SUSTENTA A PORTA ÚNICA DO GANHO =====
        // Se `mover` aceitasse a etapa de ganho, existiria negociação na coluna Venda com status
        // aberta e sem valor fechado — e o faturamento, que soma `ganha` e `concluida`, não a
        // veria. O card estaria na tela e a venda não existiria no relatório.
        if (etapa.EGanho)
            throw new RegraDeNegocioException(
                "Para mover para a etapa de venda, registre a venda com o valor fechado.",
                conflito: true);

        // Se veio card de referência, ele tem que estar na etapa de destino — senão o "meio"
        // seria calculado entre vizinhos de colunas diferentes, produzindo uma ordem sem sentido.
        if (destino.AposNegociacaoId is { } apos)
        {
            if (apos == negociacaoId)
                throw new RegraDeNegocioException(
                    "Um negócio não pode ser posicionado depois de si mesmo.");

            if (!await db.Negociacoes.Where(RegrasNegociacao.NoQuadro).AnyAsync(
                    n => n.Id == apos && n.EtapaId == destino.EtapaId, ct))
                throw new RegraDeNegocioException(
                    "A posição de destino não existe mais. Recarregue o quadro.", conflito: true);
        }

        var nova = await CalcularOrdemAsync(negociacaoId, destino, ct);

        // Se o intervalo acabou, renormaliza a coluna e recalcula sobre valores frescos (que
        // passam a ter distância 1 entre si).
        if (nova is null)
        {
            await RenormalizarAsync(destino.EtapaId, ct);
            nova = await CalcularOrdemAsync(negociacaoId, destino, ct)
                ?? throw new InvalidOperationException(
                    "Ordem do kanban sem intervalo mesmo após renormalizar — isto não deveria acontecer.");
        }

        var etapaAnterior = negociacao.EtapaId;

        // ===================== A TELA NÃO MOSTRA NOME DE COLUNA (AUD-1) =====================
        // O interceptor sozinho gravaria `etapaId: 4 → 3`, que não diz nada a quem lê. Os NOMES
        // são conhecidos aqui — o serviço acabou de ler a etapa de destino —, então ele os
        // declara, e a linha do tempo sai "moveu de Negociação para Proposta".
        //
        // Continua declarada sobre o CONTATO: a trilha é a história da pessoa, e é nela que o
        // vendedor procura. `EntidadeAuditada` ainda não tem membro para negociação.
        // ====================================================================================
        var nomes = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.Id == etapaAnterior || e.Id == destino.EtapaId)
            .ToDictionaryAsync(e => e.Id, e => e.Nome, ct);

        // ⚠️ `etapaId` PASSOU A SER DECLARADO AQUI (E4e/4), e sem ele um relatorio inteiro
        // some. Ele vinha do interceptor, que o extraia do diff de `contatos.etapa_id`; a coluna
        // caiu, o diff nao tem mais o campo, e `RelatorioFunilNoPeriodo` — que conta entradas por
        // etapa lendo `alteracoes->'etapaId'` — passou a devolver zero em TODA etapa.
        //
        // Nao houve erro nenhum: a consulta e valida, a chave so nunca aparece. Encontrado porque
        // `FUNIL_NO_PERIODO_conta_entradas_por_ARRASTO_e_por_REGISTRO_DE_VENDA` reprovou com
        // "esperado 1, veio 0" — e esse teste existe exatamente para este tipo de silencio.
        //
        // O `etapa` (com NOMES) continua, porque e o que a linha do tempo mostra a quem le. Os
        // dois convivem: um e para gente, o outro e para o relatorio.
        trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Moveu,
            new Dictionary<string, AlteracaoValor>
            {
                ["etapa"] = new(
                    nomes.GetValueOrDefault(etapaAnterior),
                    nomes.GetValueOrDefault(destino.EtapaId)),
                ["etapaId"] = new(etapaAnterior, destino.EtapaId)
            });

        // UMA LINHA SE MOVE, e so uma (E4e/4). Ate aqui a posicao era escrita tambem em
        // `contatos`, porque o dashboard, a caixa e `ProximaOrdemAsync` ainda liam de la — e essa
        // segunda escrita precisava escolher QUAL negociacao representava o contato. A coluna
        // caiu, a escolha sumiu junto, e com ela a pergunta "e se a pessoa tiver dois negocios?".
        negociacao.EtapaId = destino.EtapaId;
        negociacao.OrdemKanban = nova.Value;
        negociacao.PipelineId = etapa.PipelineId;

        // ===================== CONCORRÊNCIA OTIMISTA =====================
        // Se o cliente mandou a versão que ele viu, ela entra no `WHERE` do UPDATE. Outro
        // vendedor que tenha mexido no card entre a leitura e o arrasto muda o `xmin`, o UPDATE
        // afeta zero linhas e o EF lança — vira 409, e a tela recarrega a coluna.
        //
        // ⚠️ A versão é a da NEGOCIAÇÃO desde o E4c/2, porque é a linha dela que se move.
        //
        // E continua OPCIONAL: `MarcarGanhoAsync` também move o card e não vem de um arrasto,
        // então não tem versão para mandar. Exigir sempre quebraria a porta única do ganho.
        if (destino.Versao is { } versaoDoCliente)
        {
            // ⚠️ A COMPARAÇÃO EXPLÍCITA VEM ANTES, E ELA É NECESSÁRIA.
            //
            // Pôr a versão só no `OriginalValue` deixa a proteção dependendo de o EF EMITIR um
            // UPDATE — e ele só emite se alguma propriedade mudou de valor. Dois vendedores
            // soltando o card no MESMO lugar não mudam nada: nenhum UPDATE, nenhuma verificação,
            // e o segundo recebe sucesso com a tela desatualizada.
            //
            // Comparar aqui não depende de o valor ter mudado.
            if (negociacao.Versao != versaoDoCliente)
                throw new RegraDeNegocioException(
                    "Outra pessoa moveu este negócio enquanto você arrastava. A coluna foi recarregada.",
                    conflito: true);

            // E o `OriginalValue` continua, para a corrida entre esta leitura e o `SaveChanges`.
            db.Entry(negociacao).Property(n => n.Versao).OriginalValue = versaoDoCliente;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // `conflito: true` é o que o middleware traduz para 409 — o mesmo código que o
            // kanban já trata recarregando a coluna.
            throw new RegraDeNegocioException(
                "Outra pessoa moveu este negócio enquanto você arrastava. A coluna foi recarregada.",
                conflito: true);
        }

        // SÓ quando a etapa mudou de verdade. Reordenar o card DENTRO da mesma coluna também passa
        // por aqui, e não é `lead.movido`: para o sistema do cliente, a posição dentro da coluna
        // não significa nada — mandar um evento por arrasto encheria a fila de ruído.
        if (etapaAnterior != destino.EtapaId)
            await eventos.PublicarContatoAsync(EventoWebhook.LeadMovido, contato, etapaAnterior, ct);

        return nova.Value;
    }

    /// <summary>O ponto médio, com os três casos de borda. NULL = o intervalo acabou e a coluna
    /// precisa ser renormalizada antes.</summary>
    private async Task<decimal?> CalcularOrdemAsync(
        long negociacaoId, MoverContato destino, CancellationToken ct)
    {
        // O próprio card sai da conta: mover dentro da mesma coluna não pode considerar a posição
        // antiga dele como vizinha, senão o "meio" é calculado contra ele mesmo.
        var coluna = db.Negociacoes.AsNoTracking()
            .Where(RegrasNegociacao.NoQuadro)
            .Where(n => n.EtapaId == destino.EtapaId && n.Id != negociacaoId);

        if (destino.AposNegociacaoId is not { } aposId)
        {
            // TOPO da coluna (ou coluna vazia).
            var primeira = await coluna.MinAsync(n => (decimal?)n.OrdemKanban, ct);
            return primeira is null ? 0m : primeira.Value - 1m;
        }

        var anterior = await coluna
            .Where(n => n.Id == aposId)
            .Select(n => (decimal?)n.OrdemKanban)
            .FirstOrDefaultAsync(ct);

        if (anterior is null)
            throw new RegraDeNegocioException(
                "A posição de destino não existe mais. Recarregue o quadro.", conflito: true);

        // O vizinho de baixo: o menor que ainda é maior que o de cima. Desempate por id, para
        // acompanhar a ordenação (ordem_kanban, id) da leitura.
        var posterior = await coluna
            .Where(n => n.OrdemKanban > anterior.Value
                     || (n.OrdemKanban == anterior.Value && n.Id > aposId))
            .OrderBy(n => n.OrdemKanban).ThenBy(n => n.Id)
            .Select(n => (decimal?)n.OrdemKanban)
            .FirstOrDefaultAsync(ct);

        // FIM da coluna.
        if (posterior is null) return anterior.Value + 1m;

        // Intervalo esgotado — quem chama renormaliza.
        if (posterior.Value - anterior.Value < LimiarRenormalizacao) return null;

        return (anterior.Value + posterior.Value) / 2m;
    }

    /// <summary>Reescreve a coluna como 1, 2, 3… PRESERVANDO a ordem relativa.
    ///
    /// A ordenação da releitura é a MESMA da leitura do quadro — (ordem_kanban, id) — para o
    /// vendedor não ver os cards trocarem de lugar depois de arrastar um deles. Um `Id` a menos
    /// no desempate e dois cards com a mesma ordem sairiam invertidos.
    ///
    /// Vale a coluna inteira porque é raro: ver o comentário do LimiarRenormalizacao.</summary>
    private async Task RenormalizarAsync(long etapaId, CancellationToken ct)
    {
        var cards = await db.Negociacoes
            .Where(RegrasNegociacao.NoQuadro)
            .Where(n => n.EtapaId == etapaId)
            .OrderBy(n => n.OrdemKanban).ThenBy(n => n.Id)
            .ToListAsync(ct);

        for (var i = 0; i < cards.Count; i++)
            cards[i].OrdemKanban = i + 1;

        // ⚠️ `contatos.ordem_kanban` vai junto: `ServicoContatos.ProximaOrdemAsync` ainda lê de
        // lá para pôr o lead novo no fim da coluna. Deixar as duas fora de sincronia faria o
        // próximo contato nascer no meio do quadro. Some no E4e.
        // ⚠️ O ESPELHO DA POSICAO NO CONTATO SUMIU AQUI (E4e/4), e com ele um `GroupBy` que so
        // existia para desviar de um 500: duas negociacoes do mesmo contato na mesma coluna
        // derrubavam o `ToDictionary` com "an item with the same key has already been added".
        //
        // A pessoa com dois negocios e o ponto da tabela `negociacoes` — o codigo que precisava
        // escolher um deles para representa-la e que estava errado.

        // Uma transação implícita do SaveChanges: ou a coluna inteira é renumerada, ou nada é.
        // Renumerar pela metade deixaria cards com ordem antiga e nova misturadas.
        await db.SaveChangesAsync(ct);
    }
}
