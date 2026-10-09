using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Resumo;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== OS NUMEROS DO RESUMO DIARIO (RES-XX) =====================
///
/// RODA COM TENANT, como uma tela: na rodada diaria o job assume a empresa como dono (ver
/// `ContextoDeFundo`), e o query filter protege do mesmo jeito.
///
/// ⚠️ OS NUMEROS DA TELA VEM DA TELA. Leads e vendas sao os dos Relatorios para o dia; quem espera
/// resposta e os lembretes de hoje, os do Painel inicial. Contar de novo aqui seria uma segunda
/// regra para a mesma pergunta — e o e-mail dizendo 5 leads com a tela dizendo 4 desmoraliza os dois.
///
/// So as AUTOMATICAS e as RESPOSTAS DA PESQUISA tem conta propria: nenhuma tela as mostra por dia
/// (o relatorio de NPS conta pela data do ENVIO, e o resumo quer quem respondeu ontem).
/// ==================================================================================</summary>
public class ServicoResumoDiario(
    NexoraDbContext db,
    IServicoRelatorios relatorios,
    IServicoDashboard painel) : IServicoResumoDiario
{
    public async Task<ResumoDiario> MontarAsync(DateOnly dia, CancellationToken ct)
    {
        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new { e.Nome, e.FusoHorario })
            .FirstAsync(ct);

        var filtro = new FiltroRelatorio(dia, dia);
        var vendas = await relatorios.VendasPorPeriodoAsync(filtro, ct);
        var origens = await relatorios.OrigemLeadsAsync(filtro, ct);
        var agora = await painel.DashboardAsync(ct);

        // O dia no fuso da empresa, como parametro: nunca `::date` sobre a coluna (descarta o indice).
        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);
        var inicio = TimeZoneInfo.ConvertTimeToUtc(dia.ToDateTime(TimeOnly.MinValue), fuso);
        var fim = TimeZoneInfo.ConvertTimeToUtc(dia.AddDays(1).ToDateTime(TimeOnly.MinValue), fuso);

        // ---- as automaticas do dia: follow-up e lembrete (a pesquisa tem a sua linha)
        var automaticas = await db.Mensagens.AsNoTracking()
            .Where(m => m.Origem == OrigemMensagem.Automatica
                     && m.TipoAutomacao != TipoAutomacao.Nps
                     && m.Direcao == DirecaoMensagem.Saida
                     && m.ReservadoEm >= inicio && m.ReservadoEm < fim)
            .Select(m => new { Saiu = m.EnviadaEm != null, m.Erro, Expirou = m.ExpiradaEm != null })
            .ToListAsync(ct);

        // "Nao saiu" e o que tem motivo: erro gravado, ou desistencia. A reservada que so espera a
        // janela de atendimento nao e falha nenhuma — sai na proxima rodada.
        var falhas = automaticas.Where(a => !a.Saiu && (a.Erro != null || a.Expirou)).ToList();
        var motivos = falhas
            .GroupBy(a => a.Erro ?? "Passou do prazo sem sair.")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Take(3)
            .Select(g => new MotivoDeFalha(g.Key, g.Count()))
            .ToList();

        // ---- as respostas da pesquisa no dia, com a regua do relatorio de NPS (9-10 / 0-6)
        var notas = await db.PesquisasNps.AsNoTracking()
            .Where(p => p.Status == StatusPesquisaNps.Respondida
                     && p.DataResposta >= inicio && p.DataResposta < fim
                     && p.Nota != null)
            .Select(p => p.Nota!.Value)
            .ToListAsync(ct);

        return new ResumoDiario(
            Dia: dia,
            Empresa: empresa.Nome,
            LeadsNovos: origens.Sum(o => o.Leads),
            Vendas: vendas.Totais.Vendas,
            ValorVendido: vendas.Totais.Faturamento,
            AguardandoResposta: agora.AguardandoResposta,
            LembretesDeHoje: agora.FollowUpsPendentes,
            AutomaticasEnviadas: automaticas.Count(a => a.Saiu),
            AutomaticasNaoEnviadas: falhas.Count,
            Motivos: motivos,
            RespostasPesquisa: notas.Count,
            Promotores: notas.Count(n => n >= 9),
            Detratores: notas.Count(n => n <= 6));
    }
}
