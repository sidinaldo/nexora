using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O plano do dia. Duas consultas, zero tabela nova.</summary>
public class ServicoMeuDia(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    TimeProvider relogio) : IServicoMeuDia
{
    public async Task<MeuDia> MeuDiaAsync(CancellationToken ct)
    {
        var meuId = contexto.UsuarioId;

        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new
            {
                e.FusoHorario, e.JanelaHoraInicio, e.JanelaHoraFim, e.JanelaDiasSemana
            })
            .FirstOrDefaultAsync(ct);

        var fuso = FusoDeNegocio.Resolver(empresa?.FusoHorario);
        var agora = FusoDeNegocio.AgoraNo(relogio, fuso);
        var hoje = DateOnly.FromDateTime(agora);
        var janela = empresa is null
            ? JanelaAtendimento.Padrao
            : new JanelaAtendimento(empresa.JanelaHoraInicio, empresa.JanelaHoraFim, empresa.JanelaDiasSemana);

        // Feriados da janela: o desconto do tempo útil precisa saber quais dias no MEIO da
        // espera foram fechados. Espera mais velha que isto não é medida em número — ver
        // `EsperaAcimaDaJanela`.
        var limiteDaJanela = hoje.AddDays(-JanelaDeEspera.Dias);

        var feriados = (await db.Feriados.AsNoTracking()
            .Where(f => f.Data >= limiteDaJanela && f.Data <= hoje
                     // Global dispensado pela empresa não conta: para ela aquele dia foi útil.
                     && !db.FeriadosIgnorados.Any(i => i.FeriadoId == f.Id))
            .Select(f => f.Data).ToListAsync(ct)).ToHashSet();

        // ---- (a) conversas esperando resposta: minhas ou sem dono ----
        // Filtro e ordenação no SQL; só a conversão de fuso e o cálculo de minutos úteis (que
        // depende dos feriados) ficam em memória, sobre o conjunto JÁ recortado.
        var aguardando = await db.Conversas.AsNoTracking()
            .Where(c => c.Status == StatusConversa.Aberta
                     && c.AguardandoDesde != null
                     && (c.ResponsavelId == meuId || c.ResponsavelId == null))
            .OrderBy(c => c.AguardandoDesde)
            .Select(c => new
            {
                c.Id, c.ContatoId, Nome = c.Contato.Nome, c.Contato.Telefone, c.AguardandoDesde
            })
            .ToListAsync(ct);

        var acoesConversa = aguardando.Select(c =>
        {
            var desde = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(c.AguardandoDesde!.Value, DateTimeKind.Utc), fuso);

            // ===== ACIMA DA JANELA, NÃO SE INVENTA NÚMERO =====
            // Os feriados carregados cobrem `JanelaDeEspera.Dias`. Para uma espera mais velha,
            // `MinutosUteis` sairia SEM descontar os feriados anteriores ao recorte — maior que o
            // real, e com cara de exato. A comparação é sobre a DATA da espera, não sobre o
            // resultado do cálculo: perguntar depois já seria tarde.
            var acimaDaJanela = DateOnly.FromDateTime(desde) < limiteDaJanela;

            return new AcaoDoDia(
                TipoAcao.Responder.ToString().ToLower(), c.Id, c.ContatoId, c.Nome, c.Telefone,
                $"Responder {c.Nome}", c.Id, c.AguardandoDesde,
                acimaDaJanela ? null : TempoUtil.MinutosUteis(desde, agora, janela, feriados),
                acimaDaJanela,
                null, null, false);
        }).ToList();

        // ---- (b) lembretes pendentes vencidos ou de hoje, do responsável ----
        // `data_alvo <= hoje` inclui o atrasado: com igualdade estrita, um dia de folga do
        // vendedor faria a tarefa sumir da lista para sempre.
        var lembretes = await db.Lembretes.AsNoTracking()
            .Where(l => l.Status == StatusLembrete.Pendente
                     && l.DataAlvo <= hoje
                     && (l.ResponsavelId == meuId || l.ResponsavelId == null))
            .OrderBy(l => l.HoraAlvo == null).ThenBy(l => l.HoraAlvo).ThenBy(l => l.CriadoEm)
            .Select(l => new AcaoDoDia(
                // Literal, e não `TipoAcao.Lembrete.ToString().ToLower()`: esta projeção é
                // traduzida para SQL, e o EF não traduz ToString() sobre constante de enum.
                "lembrete", l.Id, l.ContatoId, l.Contato.Nome, l.Contato.Telefone,
                l.Titulo, l.ConversaId, null, null, false, l.HoraAlvo, l.DataAlvo,
                l.DataAlvo < hoje))
            .ToListAsync(ct);

        // Quem espera há mais tempo primeiro; depois os lembretes por hora.
        var acoes = acoesConversa
            .OrderByDescending(a => a.MinutosUteis)
            .Concat(lembretes)
            .ToList();

        return new MeuDia(acoes, acoesConversa.Count, lembretes.Count);
    }
}
