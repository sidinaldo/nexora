using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Os quatro números. TODA agregação acontece no SQL — o ServicoInbox do Recupera
/// materializa linhas antes de contar, e é justamente o que não se repete aqui.</summary>
public class ServicoDashboard(NexoraDbContext db, TimeProvider relogio, IContextoEmpresa contexto)
    : IServicoDashboard
{
    /// <summary>Quantas fatias a rosca mostra antes de juntar o resto em "Outros". Seis tons de
    /// verde é o que o olho ainda distingue; a sétima fatia seria um verde que ninguém casa com a
    /// legenda. Era uma constante da TELA, e a regra de agrupar é do servidor (AUD-XX).</summary>
    public const int MaximoDeFatias = 6;

    /// <summary>O `Origem` da fatia que junta o resto. "outros", no plural, para não colidir com a
    /// origem `outro` de verdade — a tela rotula a agrupada pelo `Agrupada`, não pelo nome.</summary>
    public const string OrigemAgrupada = "outros";

    public async Task<DashboardDto> DashboardAsync(CancellationToken ct)
    {
        // ===================== CADA UM VÊ O SEU (AUD-XX) =====================
        // `ver_numeros_da_equipe` diz: "Relatórios e atividades da equipe INTEIRA. Sem esta, cada
        // um vê só o seu." O dashboard não aplicava, e o vendedor via faturamento, conversão e
        // funil da empresa — o que Relatórios e Evolução já recortavam.
        //
        // O dono de cada número é o mesmo dos relatórios: o da NEGOCIAÇÃO para venda, perda e
        // funil; o do CONTATO para lead e origem; o da CONVERSA para quem espera resposta; e os
        // lembretes saem da mesma regra do contador do menu (`RegrasLembrete.MeusDeHoje`).
        // ===================================================================
        long? recorte = null;
        if (!contexto.Pode(Permissao.VerNumerosDaEquipe))
        {
            recorte = contexto.UsuarioId;
        }

        // `PrimeiraMensagemEm` entra numa projeção que já ia ao banco: custo zero, e é o atalho que
        // evita tocar `mensagens` no caso comum (ver `SinaisDaEmpresa`).
        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new { e.FusoHorario, e.PrimeiraMensagemEm }).FirstOrDefaultAsync(ct);

        // As datas de corte saem do fuso de NEGÓCIO e vão como PARÂMETRO. Nunca
        // `criado_em::date = current_date`: o cast é função sobre a coluna e descarta o índice
        // ix_contatos_criado.
        var fuso = FusoDeNegocio.Resolver(empresa?.FusoHorario);
        var agora = FusoDeNegocio.AgoraNo(relogio, fuso);
        var hoje = DateOnly.FromDateTime(agora);

        var inicioDoDia = TimeZoneInfo.ConvertTimeToUtc(hoje.ToDateTime(TimeOnly.MinValue), fuso);
        var inicioDoMes = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(agora.Year, agora.Month, 1, 0, 0, 0), fuso);

        var contatos = db.Contatos.AsNoTracking();
        var meusContatos = contatos.Where(c => recorte == null || c.ResponsavelId == recorte);
        var negociacoes = db.Negociacoes.AsNoTracking()
            .Where(n => recorte == null || n.ResponsavelId == recorte);

        // ⚠️ ANONIMIZADO NÃO É LEAD (AUD-XX, B9): os relatórios já tiravam, e este contador não.
        // Quem pediu para ser esquecido entrava em "leads hoje" e no gráfico de leads.
        var leadsHoje = await meusContatos.CountAsync(
            c => c.CriadoEm >= inicioDoDia && c.AnonimizadoEm == null, ct);

        var aguardando = await db.Conversas.AsNoTracking()
            .Where(RegrasConversa.EsperandoResposta(recorte))
            .CountAsync(ct);

        int followUps;
        if (recorte == null)
        {
            followUps = await db.Lembretes.AsNoTracking()
                .CountAsync(l => l.Status == StatusLembrete.Pendente && l.DataAlvo <= hoje, ct);
        }
        else
        {
            followUps = await db.Lembretes.AsNoTracking()
                .Where(RegrasLembrete.MeusDeHoje(recorte.Value, hoje))
                .CountAsync(ct);
        }

        // ===================== O FATURAMENTO VEM DE `vendas`, NÃO DA COLUNA (NEG-1) =====================
        // Contar por `contatos.ganho_em` fazia o total do mês DIMINUIR quando alguém reabria um
        // card: a coluna guarda um valor só, e reabrir a limpa. Cliente que compra duas vezes
        // aparecia uma. Um mês fechado mudava depois de fechado.
        //
        // O predicado no WHERE, e não um filtro depois: é o mesmo do índice parcial
        // `ix_vendas_periodo`, então a consulta o usa inteiro.
        //
        // Faixa SEMI-ABERTA e sem função sobre coluna: `>= inicio` casa com o índice; um
        // `date_trunc(fechada_em)` o descartaria.
        //
        // ===================== CONCLUIR NÃO TIRA DINHEIRO (NEG-2) =====================
        // O predicado passou de `cancelada_em IS NULL` para `status <> 'cancelada'`, e o que ele
        // NÃO exclui é o ponto: `concluida` continua contando. Concluir é sobre a COLUNA do
        // kanban — o pedido acabou —, não sobre o relatório. Se concluir tirasse faturamento,
        // ninguém concluiria, e a coluna voltaria a acumular.
        //
        // Cancelada sai RETROATIVAMENTE, porque aquilo não aconteceu: o mês de março corrige.
        // ================================================================================================
        // ⚠️ E4d: a fonte passou de `vendas` para `negociacoes`. O predicado é o MESMO fato dito
        // na tabela nova — `ganha_em` é o que era `fechada_em`, e aberta/perdida o têm nulo, então
        // a faixa já as exclui sem precisar listar status. É também, letra por letra, o filtro do
        // índice parcial `ix_negociacoes_ganhas`.
        var doMes = negociacoes
            .Where(n => n.Status != StatusNegociacao.Cancelada && n.GanhaEm >= inicioDoMes);

        var vendas = await doMes.CountAsync(ct);
        // SUM no banco; `?? 0` porque SUM sobre conjunto vazio devolve NULL no SQL.
        var faturamento = await doMes.SumAsync(n => (decimal?)n.Valor, ct) ?? 0m;

        // Conversão do MÊS: ganhos ÷ (ganhos + perdidos). Contatos ainda em negociação não
        // entram — incluí-los faria a taxa despencar sempre que entrasse lead novo, que é o
        // oposto do que a métrica deve mostrar.
        // ⚠️ CONTA NEGÓCIO PERDIDO, NÃO PESSOA PERDIDA (E4d), e isso CORRIGE a razão.
        //
        // O numerador sempre contou VENDAS; o denominador contava CONTATOS com `perdido_em`.
        // Unidades diferentes nos dois lados da mesma divisão: quem perdesse dois negócios com a
        // mesma pessoa entrava como um, e a conversão saía otimista.
        //
        // Nos dados de desenvolvimento os dois dão 100 — a divergência só aparece quando a mesma
        // pessoa perde mais de uma vez, que é justamente o caso que `negociacoes` passou a saber
        // representar.
        var perdidosDoMes = await negociacoes
            .CountAsync(n => n.PerdidaEm >= inicioDoMes, ct);
        var conversao = Percentual.De(vendas, vendas + perdidosDoMes);

        // Funil: um GROUP BY no SQL, não uma varredura por etapa.
        //
        // ⚠️ ESTE É O BLOCO QUE DEVOLVE A FONTE ÚNICA (E4d).
        //
        // Entre o E4c e agora, o quadro lia `negociacoes` e este gráfico lia `contatos`: duas
        // TABELAS respondendo à mesma pergunta, mantidas de acordo só pelo espelho. Uma fresta
        // exatamente do tamanho do bug que o `RegrasContato` existe para impedir — o cliente via
        // 72 no dashboard e contava 69 cards.
        //
        // O predicado agora é o MESMO de `ServicoFunil`, vindo da MESMA `Expression`
        // (`RegrasNegociacao.NoQuadro`), sobre a MESMA tabela. `FunilDbTests` compara as duas
        // leituras de verdade, etapa por etapa.
        //
        // ===================== A ETAPA DE GANHO CONTA SO O QUE ESTA EM ABERTO (NEG-2) =====
        // Ela acumulava para sempre e virava a maior barra POR DEFINICAO, achatando as outras
        // quatro — o grafico deixava de informar qualquer coisa depois de um ano.
        //
        // `ComVendaEmAberto` (o contato ter venda `fechada`) virou `Status == Ganha`: e o mesmo
        // fato, agora dito numa coluna so.
        // ==================================================================================
        // ===================== O EIXO MUDOU: FUNIL, NAO ETAPA (FUN-1) =====================
        // Ver o comentario longo de `FunilNoPainelDto`.
        //
        // ⚠️ A REGRA DA ETAPA DE GANHO TINHA QUE SOBREVIVER A TROCA DE EIXO. Por etapa ela era
        // `!e.EGanho || n.Status == Ganha`; somando o funil inteiro, passa a ser perguntada A
        // CADA NEGOCIACAO, pela etapa DELA: `!n.Etapa.EGanho || ...`. E o mesmo fato dito no eixo
        // novo. Perde-la aqui faz a coluna de ganho voltar a acumular para sempre e o painel
        // discordar do quadro — o defeito que `RegrasNegociacao` nasceu para impedir, e que ja
        // custou um cliente vendo 72 no dashboard e contando 69 cards.
        //
        // A ordem e a de `ServicoPipelines.ListarAsync` (`Ordem`, depois `Nome`): o cartao e o
        // menu lateral nao podem discordar sobre qual funil vem primeiro.
        // =================================================================================
        var porFunil = await db.Pipelines.AsNoTracking()
            .OrderBy(p => p.Ordem).ThenBy(p => p.Nome)
            .Select(p => new
            {
                p.Id,
                p.Nome,
                p.Cor,

                // AGORA: a mesma leitura do quadro.
                EmNegociacao = db.Negociacoes.Where(RegrasNegociacao.NoQuadro)
                    .Count(n => n.PipelineId == p.Id
                                && (recorte == null || n.ResponsavelId == recorte)
                                && (!n.Etapa.EGanho || n.Status == StatusNegociacao.Ganha)),
                ValorEmAberto = db.Negociacoes.Where(RegrasNegociacao.NoQuadro)
                    .Where(n => n.PipelineId == p.Id
                                && (recorte == null || n.ResponsavelId == recorte)
                                && (!n.Etapa.EGanho || n.Status == StatusNegociacao.Ganha))
                    .Sum(n => (decimal?)n.Valor) ?? 0m,

                // NO MES: os MESMOS predicados dos KPIs do topo, so que recortados por funil —
                // e por isso a soma das linhas fecha com o cartao.
                Ganhas = db.Negociacoes.Count(
                    n => n.PipelineId == p.Id
                         && (recorte == null || n.ResponsavelId == recorte)
                         && n.Status != StatusNegociacao.Cancelada && n.GanhaEm >= inicioDoMes),
                Perdidas = db.Negociacoes.Count(
                    n => n.PipelineId == p.Id
                         && (recorte == null || n.ResponsavelId == recorte)
                         && n.PerdidaEm >= inicioDoMes)
            })
            .ToListAsync(ct);

        // A divisao fica no C#: em SQL o funil sem movimento no mes seria divisao por zero. E a
        // linha "Todos" soma as linhas aqui, sobre no maximo o teto de funis — eram duas somas
        // feitas na tela (AUD-XX).
        var funil = porFunil
            .Select(f => new FunilNoPainelDto(
                f.Id, f.Nome, f.Cor, f.EmNegociacao, f.ValorEmAberto, f.Ganhas,
                Percentual.De(f.Ganhas, f.Ganhas + f.Perdidas)))
            .ToList();

        var totalEmNegociacao = 0;
        var totalValorEmAberto = 0m;
        foreach (var f in funil)
        {
            totalEmNegociacao += f.EmNegociacao;
            totalValorEmAberto += f.ValorEmAberto;
        }

        // ===================== DE ONDE VÊM OS LEADS =====================
        // Um GROUP BY no SQL, sobre TODOS os contatos não anonimizados — não só os do mês. A
        // pergunta que a rosca responde é "qual canal me traz cliente", e ela precisa de volume
        // para significar alguma coisa: recortada no mês, uma empresa pequena veria três fatias
        // de um lead cada.
        //
        // Anonimizado fica de fora: ele foi apagado a pedido do titular, e contá-lo como lead de
        // um canal seria manter o rastro que a anonimização existe para remover.
        // ⚠️ AGRUPA POR CAMPANHA TAMBEM, e nao so pelo enum (NEG-3). O nome da campanha que
        // capturou o lead ja estava gravado em `origem_detalhe` desde o INT-2 — e a rosca o
        // jogava fora, mostrando "instagram" onde o dono escreveu "Promocao de Julho". Ele criou
        // a campanha, imprimiu o QR, recebeu o lead, e o painel nao dizia o nome dela em lugar
        // nenhum. Dado gravado que a tela descarta e o mesmo que dado nao gravado.
        //
        // `origem` continua vindo junto: e ela que o cliente usa para colorir e agrupar, e o
        // contato sem campanha (a maioria) precisa de um rotulo — "WhatsApp" e a resposta certa
        // para quem simplesmente mandou mensagem.
        var origens = await meusContatos
            .Where(c => c.AnonimizadoEm == null)
            // ⚠️ `""` E NULO SAO A MESMA COISA AQUI. Hoje todo caminho de escrita normaliza
            // (`Vazio()` no servico, o nome do canal no webhook), mas se um dia um `''` entrar a
            // MESMA origem viraria DUAS fatias na rosca, com a mesma cor e sem erro nenhum. Uma
            // comparacao a mais na chave do GROUP BY custa nada e fecha a porta.
            .GroupBy(c => new
            {
                c.Origem,
                Campanha = c.OrigemDetalhe == "" ? null : c.OrigemDetalhe
            })
            .Select(g => new { g.Key.Origem, g.Key.Campanha, Leads = g.Count() })
            .OrderByDescending(x => x.Leads)
            .ToListAsync(ct);

        // ===================== O RANKING DE CAMPANHAS DO MES (NEG-3) =====================
        // GROUP BY no banco, `Take(3)` no banco: o dashboard mostra as tres primeiras, e trazer
        // todas para cortar em memoria seria a varredura que este servico evita em todo o resto.
        //
        // O recorte e `>= inicioDoMes`, SEM teto — o mesmo predicado do faturamento logo acima.
        // Um teto so aqui faria as duas caixas do dashboard discordarem no dia em que aparecesse
        // uma venda com data adiante, e discordancia entre dois numeros da mesma tela e pior que
        // os dois estarem generosos pelo mesmo criterio.
        //
        // `canal_id IS NOT NULL` — a venda sem campanha nao vira linha "Sem campanha" aqui. No
        // dashboard ela seria quase sempre a maior barra e empurraria as campanhas de verdade
        // para fora das tres. O total honesto, com a fatia sem atribuicao, esta no relatorio 3b.
        // ⚠️ AGRUPA POR `canal_id`, E NAO POR `Canal.Nome`. Agrupar pela navegacao nao traduz:
        // o EF precisaria juntar `canais_captacao` — que tem query filter de tenant — dentro da
        // chave do GROUP BY, e desiste com "could not be translated". A alternativa dele seria
        // avaliar em memoria, que e exatamente o que este servico nao faz em lugar nenhum.
        //
        // O nome vem numa segunda leitura de NO MAXIMO tres linhas, depois do Take.
        // ⚠️ E4e/2: esta consulta ficou para tras no E4d — era a ultima leitura de `vendas` no
        // servico, e passou despercebida porque o teste que a cobre nao compara com a fonte
        // antiga. `canal_ciclo_id` e o que era `vendas.canal_id`, e `ganha_em` o que era
        // `fechada_em`.
        var brutos = await negociacoes
            .Where(n => n.CanalCicloId != null
                     && n.Status != StatusNegociacao.Cancelada
                     && n.GanhaEm >= inicioDoMes)
            .GroupBy(n => n.CanalCicloId!.Value)
            .Select(g => new { CanalId = g.Key, Vendas = g.Count(), Valor = g.Sum(n => n.Valor ?? 0m) })
            .OrderByDescending(x => x.Valor)
            .Take(3)
            .ToListAsync(ct);

        var idsDeCanal = brutos.Select(x => x.CanalId).ToList();
        var nomesDeCanal = await db.CanaisCaptacao.AsNoTracking()
            .Where(c => idsDeCanal.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Nome, ct);

        // O fallback nao deveria acontecer — apagar o canal anula `vendas.canal_id` pela FK, e a
        // linha sai do WHERE acima. Mas um rotulo honesto e melhor que uma excecao no dashboard.
        var campanhas = brutos
            .Select(x => new CampanhaDto(
                nomesDeCanal.TryGetValue(x.CanalId, out var nome) ? nome : "Campanha removida",
                x.Vendas, x.Valor))
            .ToList();

        // ===================== OS SINAIS DE ESTREIA (POS-1) =====================
        // A pergunta da mensagem sai de `SinaisDaEmpresa`, que é a MESMA que o onboarding faz. Duas
        // cópias já existiram — a daqui somava os cards do quadro — e a divergência foi o defeito:
        // empresa com duas vendas concluídas aparecia como recém-criada.
        //
        // ⚠️ A ORDEM IMPORTA PARA O CUSTO: `RecebeuMensagemAsync` só toca `mensagens` quando a
        // coluna está NULL, e `TemContato` é um `AnyAsync` pelo índice de `empresa_id`. No caso
        // comum (coluna preenchida), isto é UMA consulta barata, não duas caras.
        // ======================================================================
        var recebeuMensagem = await SinaisDaEmpresa.RecebeuMensagemAsync(
            db, empresa?.PrimeiraMensagemEm, ct);

        var temContato = await contatos.AnyAsync(ct);

        // O `.ToString().ToLower()` fica em memória sobre o conjunto JÁ agregado (uma linha por
        // origem e campanha): traduzir enum para texto não tem tradução em SQL, e agregar é o que
        // precisava acontecer no banco — e aconteceu.
        var (leadsTotal, fatias) = Rosca(
            [.. origens.Select(o => (o.Origem.ToString().ToLowerInvariant(), o.Campanha, o.Leads))]);

        return new DashboardDto(
            leadsHoje, aguardando, followUps, vendas, faturamento, conversao, funil,
            totalEmNegociacao, totalValorEmAberto, leadsTotal, fatias,
            campanhas, recebeuMensagem, temContato);
    }

    /// <summary>===================== A ROSCA, MONTADA AQUI (AUD-XX) =====================
    ///
    /// Recebe as linhas que o banco agregou — uma por (origem, campanha) — e devolve as fatias que
    /// a tela desenha. Era a tela que fazia tudo isto; o painel agora só pinta.
    ///
    ///   1. soma por origem, guardando as campanhas nomeadas como sub-linhas;
    ///   2. ordena da maior para a menor (empate: pelo nome, para a ordem não mudar entre duas
    ///      cargas da página);
    ///   3. passando de `MaximoDeFatias`, as cinco maiores ficam e o resto vira uma fatia só;
    ///   4. os percentuais pelo maior resto, somando 100.
    ///
    /// Trabalha sobre no máximo algumas dezenas de linhas JÁ agregadas — não é a contagem em
    /// memória que este serviço evita, é dar forma ao que o banco contou.
    /// =================================================================================</summary>
    public static (int LeadsTotal, List<FatiaOrigemDto> Fatias) Rosca(
        IReadOnlyList<(string Origem, string? Campanha, int Leads)> linhas)
    {
        var porOrigem = new Dictionary<string, (int Leads, List<CampanhaDaOrigemDto> Campanhas)>();

        foreach (var linha in linhas)
        {
            if (!porOrigem.TryGetValue(linha.Origem, out var atual))
            {
                atual = (0, new List<CampanhaDaOrigemDto>());
            }

            if (linha.Campanha != null)
            {
                atual.Campanhas.Add(new CampanhaDaOrigemDto(linha.Campanha, linha.Leads));
            }

            porOrigem[linha.Origem] = (atual.Leads + linha.Leads, atual.Campanhas);
        }

        var ordenadas = porOrigem
            .OrderByDescending(o => o.Value.Leads)
            .ThenBy(o => o.Key, StringComparer.Ordinal)
            .ToList();

        var grupos = new List<(string Origem, bool Agrupada, int Leads, List<CampanhaDaOrigemDto> Campanhas)>();

        if (ordenadas.Count <= MaximoDeFatias)
        {
            foreach (var o in ordenadas)
            {
                grupos.Add((o.Key, false, o.Value.Leads, o.Value.Campanhas));
            }
        }
        else
        {
            foreach (var o in ordenadas.Take(MaximoDeFatias - 1))
            {
                grupos.Add((o.Key, false, o.Value.Leads, o.Value.Campanhas));
            }

            var resto = 0;
            foreach (var o in ordenadas.Skip(MaximoDeFatias - 1))
            {
                resto += o.Value.Leads;
            }

            // A agrupada não lista campanhas: seriam peças de origens diferentes numa lista só.
            grupos.Add((OrigemAgrupada, true, resto, new List<CampanhaDaOrigemDto>()));
        }

        var percentuais = Percentual.Fatias([.. grupos.Select(g => (decimal)g.Leads)]);

        var fatias = new List<FatiaOrigemDto>();
        var leadsTotal = 0;

        for (var i = 0; i < grupos.Count; i++)
        {
            var g = grupos[i];
            leadsTotal += g.Leads;

            fatias.Add(new FatiaOrigemDto(
                g.Origem, g.Agrupada, g.Leads, percentuais[i],
                [.. g.Campanhas.OrderByDescending(c => c.Leads).ThenBy(c => c.Nome, StringComparer.Ordinal)]));
        }

        return (leadsTotal, fatias);
    }
}
