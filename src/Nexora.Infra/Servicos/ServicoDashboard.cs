using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Os quatro números. TODA agregação acontece no SQL — o ServicoInbox do Recupera
/// materializa linhas antes de contar, e é justamente o que não se repete aqui.</summary>
public class ServicoDashboard(NexoraDbContext db, TimeProvider relogio) : IServicoDashboard
{
    public async Task<DashboardDto> DashboardAsync(CancellationToken ct)
    {
        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new { e.FusoHorario }).FirstOrDefaultAsync(ct);

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

        var leadsHoje = await contatos.CountAsync(c => c.CriadoEm >= inicioDoDia, ct);

        var aguardando = await db.Conversas.AsNoTracking()
            .CountAsync(c => c.Status == StatusConversa.Aberta && c.AguardandoDesde != null, ct);

        var followUps = await db.Lembretes.AsNoTracking()
            .CountAsync(l => l.Status == StatusLembrete.Pendente && l.DataAlvo <= hoje, ct);

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
        var doMes = db.Vendas.AsNoTracking()
            .Where(v => v.Status != StatusVenda.Cancelada && v.FechadaEm >= inicioDoMes);

        var vendas = await doMes.CountAsync(ct);
        // SUM no banco; `?? 0` porque SUM sobre conjunto vazio devolve NULL no SQL.
        var faturamento = await doMes.SumAsync(v => (decimal?)v.Valor, ct) ?? 0m;

        // Conversão do MÊS: ganhos ÷ (ganhos + perdidos). Contatos ainda em negociação não
        // entram — incluí-los faria a taxa despencar sempre que entrasse lead novo, que é o
        // oposto do que a métrica deve mostrar.
        var perdidosDoMes = await contatos.CountAsync(c => c.PerdidoEm >= inicioDoMes, ct);
        var fechados = vendas + perdidosDoMes;
        var conversao = fechados > 0 ? (double)vendas / fechados : 0d;

        // Funil: um GROUP BY no SQL, não uma varredura por etapa.
        //
        // O predicado vem de `RegrasContato.NoQuadro`, o MESMO que o `ServicoFunil` usa. Antes
        // estava escrito por extenso aqui, filtrando só `perdido_em` — e o quadro filtrava
        // também `anonimizado_em`. O cliente via 72 no dashboard e contava 69 cards.
        //
        // ===================== A ETAPA DE GANHO CONTA SO O QUE ESTA EM ABERTO (NEG-2) =====
        // Ela acumulava para sempre e virava a maior barra POR DEFINICAO, achatando as outras
        // quatro — o grafico deixava de informar qualquer coisa depois de um ano.
        //
        // `ComVendaEmAberto` e a MESMA expressao que o kanban usa (RegrasContato), pela mesma
        // razao que `NoQuadro` existe: escrita por extenso em dois lugares, ela ja divergiu.
        // ==================================================================================
        var funil = await db.EtapasFunil.AsNoTracking()
            .OrderBy(e => e.Ordem)
            .Select(e => new EtapaFunilDto(
                e.Id, e.Nome, e.Ordem, e.Cor,
                db.Contatos.Where(RegrasContato.NoQuadro)
                    .Where(c => !e.EGanho || db.Vendas.Any(
                        v => v.ContatoId == c.Id && v.Status == StatusVenda.Fechada))
                    .Count(c => c.EtapaId == e.Id),
                db.Contatos.Where(RegrasContato.NoQuadro)
                    .Where(c => !e.EGanho || db.Vendas.Any(
                        v => v.ContatoId == c.Id && v.Status == StatusVenda.Fechada))
                    .Where(c => c.EtapaId == e.Id)
                    .Sum(c => (decimal?)c.Valor) ?? 0m))
            .ToListAsync(ct);

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
        var origens = await db.Contatos.AsNoTracking()
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
        var brutos = await db.Vendas.AsNoTracking()
            .Where(v => v.CanalId != null
                     && v.Status != StatusVenda.Cancelada
                     && v.FechadaEm >= inicioDoMes)
            .GroupBy(v => v.CanalId!.Value)
            .Select(g => new { CanalId = g.Key, Vendas = g.Count(), Valor = g.Sum(v => v.Valor) })
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

        return new DashboardDto(
            leadsHoje, aguardando, followUps, vendas, faturamento, conversao, funil,
            // O `.ToString().ToLower()` fica em memória sobre o conjunto JÁ agregado (no máximo 9
            // linhas, uma por origem): traduzir enum para texto não tem tradução em SQL, e
            // agregar é o que precisava acontecer no banco — e aconteceu.
            [.. origens.Select(o => new OrigemDto(
                o.Origem.ToString().ToLowerInvariant(), o.Leads, o.Campanha))],
            campanhas);
    }
}
