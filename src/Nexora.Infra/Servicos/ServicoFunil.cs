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
            .Select(e => new { e.Id, e.Nome, e.Ordem, e.Cor, e.EGanho })
            .ToListAsync(ct);

        // Os números de cada coluna saem de `TotaisAsync` — a MESMA consulta que o arrasto e a
        // página de uma coluna usam (AUD-XX). Uma cópia só, para o quadro e a coluna relida depois
        // do arrasto não poderem discordar.
        var totais = await TotaisAsync(etapas.Select(e => e.Id).ToList(), ct);

        var colunas = new List<ColunaFunil>(etapas.Count);

        // ===================== ONDE FICA A FRONTEIRA DA VENDA (POS-1) =====================
        // A ordem da etapa de ganho, SEM consulta nova: a lista acima já traz `Ordem` e `EGanho` de
        // todas as etapas do funil. Daqui sai o `PosGanho` de cada coluna.
        //
        // `null` quando o funil não tem etapa de ganho — estado legal, e nesse caso nenhuma coluna é
        // de pós-venda, porque não há fronteira para estar depois de.
        // ==============================================================================
        var ordemDoGanho = etapas.FirstOrDefault(e => e.EGanho)?.Ordem;

        // Uma consulta por coluna. A alternativa — uma consulta só com ROW_NUMBER() particionado —
        // traria tudo de uma vez, mas o EF não expressa window function sem SQL cru, e são 5
        // consultas indexadas contra ix_contatos_kanban. Não vale o SQL cru aqui.
        foreach (var e in etapas)
        {
            var pagina = await CardsDaColunaAsync(e.Id, null, null, porColuna, ct);
            var t = TotaisDe(totais, e.Id);
            colunas.Add(new ColunaFunil(
                e.Id, e.Nome, e.Ordem, e.Cor, e.EGanho,
                ordemDoGanho is { } ganho && e.Ordem > ganho,
                t.Total, t.ValorTotal, t.Concluidas, pagina.Itens, pagina.TemMais));
        }

        return new QuadroFunil(colunas);
    }

    public async Task<PaginaColuna> ColunaAsync(
        long etapaId, decimal? cursorOrdem, long? cursorId, int tamanho, CancellationToken ct)
    {
        var pagina = await CardsDaColunaAsync(etapaId, cursorOrdem, cursorId, tamanho, ct);
        var totais = await TotaisAsync(new List<long> { etapaId }, ct);
        var t = TotaisDe(totais, etapaId);

        return new PaginaColuna(pagina.Itens, pagina.TemMais, t.Total, t.ValorTotal, t.Concluidas);
    }

    // ==================================================================== totais
    /// <summary>Os números do cabeçalho das colunas pedidas, numa consulta só (AUD-XX).
    ///
    /// Etapa de outra empresa não volta: o filtro de empresa de `EtapasFunil` a tira da consulta,
    /// e quem pede trata a ausência como coluna vazia (<see cref="TotaisDe"/>).</summary>
    private async Task<Dictionary<long, TotaisColuna>> TotaisAsync(
        List<long> etapaIds, CancellationToken ct)
    {
        var linhas = await db.EtapasFunil.AsNoTracking()
            .Where(e => etapaIds.Contains(e.Id))
            .Select(e => new
            {
                e.Id,
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
                // ===================== O RECORTE FICOU MENOR (POS-1) =====================
                // Era um caso triplo: ganho mostra `ganha`, as outras mostram `aberta`. Com etapas
                // DEPOIS da de ganho isso fica errado por omissao — um negocio `ganha` numa etapa
                // de pos-venda (que nao e `EGanho`) nao casava com nenhum dos dois ramos, e o card
                // DESAPARECIA do quadro depois de um arrasto que deu 200.
                //
                // Agora: a coluna de ganho mostra so o que esta ganho; as outras mostram o que o
                // `NoQuadro` admitir. Menos codigo, e de graca conserta o card invisivel do funil
                // SEM etapa de ganho (ver `MarcarGanhoAsync`, que ali deixa o card onde esta).
                //
                // E mantem visivel o card `Aberta` que por acaso esteja numa pos-venda, para o
                // vendedor poder tira-lo de la: as regras olham o DESTINO, nao a origem, entao o
                // estado se cura sozinho.
                // ======================================================================
                Total = db.Negociacoes
                    .Where(RegrasNegociacao.NoQuadro)
                    .Where(n => !e.EGanho || n.Status == StatusNegociacao.Ganha)
                    .Count(n => n.EtapaId == e.Id),
                ValorTotal = db.Negociacoes
                    .Where(RegrasNegociacao.NoQuadro)
                    .Where(n => !e.EGanho || n.Status == StatusNegociacao.Ganha)
                    .Where(n => n.EtapaId == e.Id)
                    .Sum(n => (decimal?)n.Valor),
                // O QUE JA FOI CONCLUIDO, agregado no SQL. SEM `CardVigente`: é histórico de
                // pedido, e contato com três concluídas conta três — exatamente como contava
                // sobre `vendas`.
                Concluidas = db.Negociacoes.Count(
                    n => n.Status == StatusNegociacao.Concluida && n.EtapaId == e.Id)
            })
            .ToListAsync(ct);

        var totais = new Dictionary<long, TotaisColuna>();
        foreach (var linha in linhas)
        {
            var valor = 0m;
            if (linha.ValorTotal != null)
            {
                valor = linha.ValorTotal.Value;
            }

            totais[linha.Id] = new TotaisColuna(linha.Id, linha.Total, valor, linha.Concluidas);
        }

        return totais;
    }

    /// <summary>Os números de uma coluna, ou zeros quando a consulta não a trouxe — etapa que não
    /// existe, ou que é de outra empresa.</summary>
    private static TotaisColuna TotaisDe(Dictionary<long, TotaisColuna> totais, long etapaId)
    {
        if (totais.TryGetValue(etapaId, out var encontrados))
        {
            return encontrados;
        }

        return new TotaisColuna(etapaId, 0, 0m, 0);
    }

    /// <summary>Uma página de cards de uma coluna, sem os totais. O quadro chama isto uma vez por
    /// coluna e pega os totais de todas numa consulta só.</summary>
    private async Task<PaginaCursor<CardFunil>> CardsDaColunaAsync(
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

        // O recorte por coluna, igual ao do `QuadroAsync` (POS-1): SO a coluna de ganho filtra. As
        // outras mostram o que o `NoQuadro` admitir — inclusive o negocio `ganha` que avancou para
        // uma etapa de pos-venda, que e o ponto do bloco.
        //
        // ⚠️ AS DUAS COPIAS TEM DE DIZER A MESMA COISA. Reverter so esta deixa o cabecalho contando
        // 1 e a lista de cards vazia — e e o `QuadroAsync` que o cliente carrega primeiro, entao o
        // sintoma aparece ao rolar a coluna, nao ao abrir.
        if (eGanho)
            q = q.Where(n => n.Status == StatusNegociacao.Ganha);

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
                // POS-1: de graca na projecao, e e o que diz se `Valor` e estimativa ou dinheiro.
                Ganha = n.Status == StatusNegociacao.Ganha,
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
                        CanalDoCiclo = v.CanalCiclo == null ? null : v.CanalCiclo.Nome,
                        // A janela do WhatsApp (INT-XX), com o rotulo da API.
                        Canal = v.Conexao.Canal == CanalWhatsapp.CloudApi ? "cloud_api" : "evolution",
                        v.UltimaEntradaEm
                    })
                    .FirstOrDefault(),
                // Colecao materializada, ao contrario das duas acima — aqui os NOMES sao o dado,
                // nao a contagem. O EF resolve numa segunda consulta por PAGINA, nao uma por card.
                //
                // ===================== AS ETIQUETAS SAO DO NEGOCIO, NAO DA PESSOA =====================
                // ⚠️ ERAM `n.Contato.Etiquetas`, e o comentario antigo dizia que a outra metade
                // "ainda nao existe". Ela existe agora, e o motivo de nascer foi um relato:
                // "incluí o contato Ysia em Vendas e Pós-venda e ela ficou com a mesma etiqueta em
                // pipeline diferente".
                //
                // Vinham da pessoa, entao os dois cards dela saiam identicos — e nao havia como
                // dizer "este negocio esta urgente" sem dizer o mesmo do outro.
                //
                // ⚠️ O CARD NAO MOSTRA MAIS AS DA PESSOA, e isso foi escolhido: onde se marca e
                // onde aparece, uma regra sem excecao. "VIP" continua visivel na caixa e na tela
                // do contato. Acrescentar os chips do contato aqui depois e aditivo; tira-los
                // depois seria mexer no que o vendedor ja se acostumou a ver.
                // ==================================================================================
                Etiquetas = n.Etiquetas
                    .OrderBy(x => x.Etiqueta.Nome)
                    .Select(x => new EtiquetaDto(x.Etiqueta.Id, x.Etiqueta.Nome, x.Etiqueta.Cor))
                    .ToList()
            })
            .ToListAsync(ct);

        var temMais = linhas.Count > tamanho;

        var cards = linhas.Take(tamanho).Select(c => new CardFunil(
            c.Id, c.ContatoId, c.Nome, c.Telefone, c.OrdemKanban, c.Valor, c.Ganha,
            c.ResponsavelId, c.ResponsavelNome,
            c.Conversa?.Id, c.Conversa?.AguardandoDesde, c.Conversa?.NaoLidas ?? 0,
            c.Conversa?.UltimaMensagemEm, c.Conversa?.CanalDoCiclo, c.Versao,
            c.Etiquetas, c.Conversa?.Canal, c.Conversa?.UltimaEntradaEm)).ToList();

        return new PaginaCursor<CardFunil>(cards, temMais);
    }

    // ==================================================================== mover
    public async Task<ResultadoMover> MoverAsync(
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

        // Etapa DESTA empresa. O query filter protege a leitura; um id vindo do cliente precisa
        // de checagem explícita — sem isso, um id de outro tenant passaria e o card sairia do
        // funil da própria empresa.
        //
        // `Ordem` entra na projeção que já existia (POS-1): é de graça, e é metade do que a regra
        // de direção precisa.
        var etapa = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.Id == destino.EtapaId)
            .Select(e => new { e.Id, e.EGanho, e.PipelineId, e.Ordem })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Etapa não encontrada.");

        // ===================== QUEM DECIDE É `RegrasDoQuadro` (POS-1) =====================
        // Eram DOIS `if` escritos aqui — "só a negociação aberta se move" e "a etapa de ganho não
        // recebe arrasto" —, e o primeiro é justamente o que impedia o quadro de ter etapas depois
        // da venda. As duas regras, mais as novas de direção, estão numa tabela só, com os testes
        // dela sem banco.
        //
        // ⚠️ O QUE **NÃO** MUDOU, E É O MAIS IMPORTANTE: a etapa de ganho continua recusando
        // arrasto, de qualquer status. Era o `if (etapa.EGanho)` daqui, e virou a primeira linha da
        // tabela. Sem ela existiria negociação na coluna Venda com status aberta e sem valor — na
        // tela, e invisível no faturamento, que soma `ganha` e `concluida`.
        //
        // As etapas do funil de DESTINO, numa consulta: no máximo 12 linhas
        // (`ServicoEtapas.MaximoEtapas`), pelo `uq_etapas_ordem`. Daqui saem a ordem da etapa de
        // ganho e a ordem de onde o card está.
        //
        // ⚠️ NÃO JUNTAR com a consulta de etapas de mais abaixo (a dos nomes da trilha): aquela
        // cobre a etapa de ORIGEM, que pode estar em outro funil.
        // ==============================================================================
        var etapasDoDestino = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == etapa.PipelineId)
            .Select(e => new { e.Id, e.Ordem, e.EGanho })
            .ToListAsync(ct);

        var ordemDoGanho = etapasDoDestino.FirstOrDefault(e => e.EGanho)?.Ordem;
        var ordemAtual = etapasDoDestino.FirstOrDefault(e => e.Id == negociacao.EtapaId)?.Ordem;

        if (RegrasDoQuadro.Recusa(new RegrasDoQuadro.Destino(
                negociacao.Status, ordemAtual, etapa.Ordem, etapa.EGanho, ordemDoGanho,
                TrocaDeFunil: etapa.PipelineId != negociacao.PipelineId)) is { } recusa)
            throw new RegraDeNegocioException(recusa, conflito: true);

        // ===================== A VERSÃO É CONFERIDA ANTES DE QUALQUER ESCRITA =====================
        // ⚠️ ELA JÁ FICOU LÁ EMBAIXO, JUNTO DO `SaveChanges`, E ISSO ERA UM DEFEITO. Entre aquele
        // ponto e este roda a RENORMALIZAÇÃO, que reescreve a coluna inteira — inclusive a linha
        // deste card. E `Versao` é o `xmin`, mapeado com `ValueGeneratedOnAddOrUpdate`: depois do
        // `SaveChanges` da renormalização o EF RELÊ o valor, e `negociacao.Versao` passa a ser o
        // novo. A comparação então acusava "outra pessoa moveu" contra uma versão que a PRÓPRIA
        // REQUISIÇÃO acabou de mudar, com o vendedor sozinho na tela.
        //
        // Apareceu num quadro com mil cards semeados cuja `ordem_kanban` se repetia: sem intervalo
        // entre vizinhos, a renormalização disparava no PRIMEIRO arrasto, e o kanban ficava
        // inutilizável naquela coluna.
        //
        // Aqui em cima a pergunta é a certa: "o que o cliente viu ainda vale?" — feita sobre o
        // estado lido do banco, antes de nós mesmos escrevermos qualquer coisa.
        // ====================================================================================
        if (destino.Versao is { } versaoDoCliente && negociacao.Versao != versaoDoCliente)
            throw new RegraDeNegocioException(
                "Outra pessoa moveu este negócio enquanto você arrastava. A coluna foi recarregada.",
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

        // ===================== ARRASTAR PARA UM FUNIL ONDE ELE JA ESTA =====================
        // ⚠️ ESTE CAMINHO NAO CHECAVA NADA, e e o mais silencioso dos tres: o card sai de um
        // funil e entra noutro onde a mesma pessoa ja tem um — dois cards dela na mesma coluna,
        // sem erro nenhum. So o indice `uq_negociacoes_card_por_funil` pegaria, e como erro de
        // banco: 500 no meio de um arrasto.
        //
        // Aqui a pergunta e feita ANTES, e a resposta e uma mensagem que diz em qual funil.
        // ==============================================================================
        if (etapa.PipelineId != negociacao.PipelineId)
        {
            // `Aberta` OU `Ganha`, pelo mesmo motivo do abrir: a regra e sobre CARDS, e os dois
            // aparecem no quadro.
            var jaLa = await db.Negociacoes.AsNoTracking().AnyAsync(
                n => n.ContatoId == negociacao.ContatoId
                  && n.PipelineId == etapa.PipelineId
                  && n.Id != negociacao.Id
                  && (n.Status == StatusNegociacao.Aberta || n.Status == StatusNegociacao.Ganha),
                ct);

            if (jaLa)
            {
                var nome = await db.Pipelines.AsNoTracking()
                    .Where(p => p.Id == etapa.PipelineId).Select(p => p.Nome)
                    .FirstOrDefaultAsync(ct);

                throw new RegraDeNegocioException(
                    $"Este contato já tem um negócio em {nome}. "
                    + "Um funil mostra um card por pessoa de cada vez.",
                    conflito: true);
            }
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
        // ===================== E A CORRIDA ENTRE A LEITURA E A ESCRITA =====================
        // Quem cobre essa janela é o `IsConcurrencyToken` do `xmin`: o EF põe no `WHERE` do UPDATE
        // o valor ORIGINAL que ele mesmo tem rastreado, e zero linhas afetadas vira
        // `DbUpdateConcurrencyException` logo abaixo.
        //
        // ⚠️ AQUI HAVIA UM `OriginalValue = versaoDoCliente`, E ELE TINHA DE SAIR JUNTO. Forçar o
        // valor do cliente no `WHERE` transforma a renormalização — uma escrita NOSSA, legítima,
        // feita segundos antes na mesma requisição — em conflito: o `WHERE xmin = <versão velha>`
        // não acha mais a linha. Era o mesmo defeito, pela segunda porta.
        //
        // O valor rastreado pelo EF é o certo porque acompanha o que ESTA requisição já escreveu, e
        // continua recusando o que OUTRA transação escrever no meio.
        // ===============================================================================
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

        // ===================== OS NÚMEROS DAS COLUNAS SAEM DAQUI (AUD-XX) =====================
        // A tela tirava 1 da origem e somava 1 no destino por conta própria, e a coluna de origem
        // nunca era relida: o que outro vendedor tivesse mexido nela ficava de fora do cabeçalho
        // até recarregar a página. Agora a resposta traz as duas, contadas DEPOIS da escrita.
        // ==================================================================================
        var afetadas = new List<long> { etapaAnterior };
        if (destino.EtapaId != etapaAnterior)
        {
            afetadas.Add(destino.EtapaId);
        }

        var totais = await TotaisAsync(afetadas, ct);
        var colunas = new List<TotaisColuna>();
        foreach (var etapaId in afetadas)
        {
            colunas.Add(TotaisDe(totais, etapaId));
        }

        return new ResultadoMover(nova.Value, colunas);
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
