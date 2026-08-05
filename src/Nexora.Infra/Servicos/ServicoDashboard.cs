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

        var ganhosDoMes = contatos.Where(c => c.GanhoEm >= inicioDoMes);
        var vendas = await ganhosDoMes.CountAsync(ct);
        // SUM no banco; `?? 0` porque SUM sobre conjunto vazio devolve NULL no SQL.
        var faturamento = await ganhosDoMes.SumAsync(c => (decimal?)c.Valor, ct) ?? 0m;

        // Conversão do MÊS: ganhos ÷ (ganhos + perdidos). Contatos ainda em negociação não
        // entram — incluí-los faria a taxa despencar sempre que entrasse lead novo, que é o
        // oposto do que a métrica deve mostrar.
        var perdidosDoMes = await contatos.CountAsync(c => c.PerdidoEm >= inicioDoMes, ct);
        var fechados = vendas + perdidosDoMes;
        var conversao = fechados > 0 ? (double)vendas / fechados : 0d;

        // Funil: um GROUP BY no SQL, não uma varredura por etapa. Perdidos ficam de fora — eles
        // não aparecem no quadro (mesma regra do índice parcial ix_contatos_kanban).
        var funil = await db.EtapasFunil.AsNoTracking()
            .OrderBy(e => e.Ordem)
            .Select(e => new EtapaFunilDto(
                e.Id, e.Nome, e.Ordem, e.Cor,
                db.Contatos.Count(c => c.EtapaId == e.Id && c.PerdidoEm == null),
                db.Contatos.Where(c => c.EtapaId == e.Id && c.PerdidoEm == null)
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
        var origens = await db.Contatos.AsNoTracking()
            .Where(c => c.AnonimizadoEm == null)
            .GroupBy(c => c.Origem)
            .Select(g => new { Origem = g.Key, Leads = g.Count() })
            .OrderByDescending(x => x.Leads)
            .ToListAsync(ct);

        return new DashboardDto(
            leadsHoje, aguardando, followUps, vendas, faturamento, conversao, funil,
            // O `.ToString().ToLower()` fica em memória sobre o conjunto JÁ agregado (no máximo 9
            // linhas, uma por origem): traduzir enum para texto não tem tradução em SQL, e
            // agregar é o que precisava acontecer no banco — e aconteceu.
            [.. origens.Select(o => new OrigemDto(o.Origem.ToString().ToLowerInvariant(), o.Leads))]);
    }
}
