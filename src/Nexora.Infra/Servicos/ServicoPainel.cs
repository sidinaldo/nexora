using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Contadores do shell. Tudo agregado no SQL — sao dois COUNT, uma leitura de conexao,
/// uma de empresa e uma faixa de feriados, justamente para caber num polling de 45s sem pesar.</summary>
public class ServicoPainel(NexoraDbContext db, TimeProvider relogio) : IServicoPainel
{
    public async Task<StatusPainel> StatusAsync(CancellationToken ct)
    {
        var abertas = db.Conversas.AsNoTracking().Where(c => c.Status == StatusConversa.Aberta);

        // TODAS as conexoes, nao a primeira. O recorte em memoria e sobre uma lista limitada por
        // `ck_empresas_limite_conexoes` (no maximo 20 linhas), e os dois recortes saem da MESMA
        // leitura — duas consultas SQL aqui custariam mais que o filtro.
        var conexoes = await db.Conexoes.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new { c.Nome, c.Status, c.Numero, c.NumeroAnterior })
            .ToListAsync(ct);

        // PAREADA e caida. `Numero != null` e o que separa "caiu" de "ainda nao foi conectada":
        // a segunda nao merece alerta vermelho no topo de todas as telas.
        var caidas = conexoes
            .Where(c => c.Numero != null && c.Status != StatusConexao.Conectado)
            .Select(c => c.Nome)
            .ToList();

        // As faixas do semaforo e a janela saem da EMPRESA, nao de constante. Quem atende das 9h
        // as 18h nao quer o mesmo limite de quem atende 24h.
        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new
            {
                e.FusoHorario,
                e.SemaforoAmareloMinutos, e.SemaforoVermelhoMinutos,
                e.JanelaHoraInicio, e.JanelaHoraFim, e.JanelaDiasSemana
            })
            .FirstOrDefaultAsync(ct);

        var fuso = FusoDeNegocio.Resolver(empresa?.FusoHorario);
        var hoje = DateOnly.FromDateTime(FusoDeNegocio.AgoraNo(relogio, fuso));

        // Faixa FECHADA de 30 dias: o desconto do tempo util so precisa dos dias no meio da
        // espera, e uma conversa parada ha mais de um mes ja esta vermelha de qualquer jeito.
        // Range scan sobre ix_feriados_data, algumas linhas — nao pesa no poll.
        var feriados = await db.Feriados.AsNoTracking()
            .Where(f => f.Data >= hoje.AddDays(-30) && f.Data <= hoje
                     // Os globais que a empresa dispensou não contam: para ela aquele dia foi
                     // de trabalho, e o desconto do semáforo tem que refletir isso.
                     && !db.FeriadosIgnorados.Any(i => i.FeriadoId == f.Id))
            .OrderBy(f => f.Data)
            .Select(f => f.Data)
            .ToListAsync(ct);

        var padrao = JanelaAtendimento.Padrao;

        return new StatusPainel(
            NaoLidas: await abertas.SumAsync(c => (int?)c.NaoLidas, ct) ?? 0,
            Aguardando: await abertas.CountAsync(c => c.AguardandoDesde != null, ct),
            // Comeca como conectado quando nao ha conexao pareada ainda: melhor nao acender o
            // banner antes de a empresa ter passado pelo pareamento.
            WhatsappConectado: caidas.Count == 0,
            ConexoesCaidas: caidas,
            // QUALQUER uma que trocou de chip. Perguntar so a primeira esconderia a troca nas
            // outras, e o aviso existe justamente para o dono conferir que o numero certo entrou.
            TrocouDeNumero: conexoes.Any(c => c.NumeroAnterior is not null),
            SemaforoAmareloMinutos: empresa?.SemaforoAmareloMinutos ?? 60,
            SemaforoVermelhoMinutos: empresa?.SemaforoVermelhoMinutos ?? 240,
            JanelaHoraInicio: empresa?.JanelaHoraInicio ?? padrao.HoraInicio,
            JanelaHoraFim: empresa?.JanelaHoraFim ?? padrao.HoraFim,
            JanelaDiasSemana: empresa?.JanelaDiasSemana ?? padrao.DiasSemana,
            FeriadosRecentes: feriados);
    }
}
