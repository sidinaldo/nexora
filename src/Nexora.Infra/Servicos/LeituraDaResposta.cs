using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Texto;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Ver `ILeituraDaResposta` para o porque de cada decisao. Aqui mora o banco.</summary>
public class LeituraDaResposta(NexoraDbContext db, TimeProvider relogio) : ILeituraDaResposta
{
    public async Task<RespostaDaPesquisa> LerAsync(
        long empresaId, long contatoId, long mensagemId, string? texto, string? stanzaIdCitado,
        CancellationToken ct)
    {
        // ===================== A PERGUNTA MAIS ESTREITA PRIMEIRO =====================
        // ⚠️ TODA MENSAGEM RECEBIDA PASSA POR AQUI. `ix_pesquisas_nps_aberta` e parcial em
        // `status IN ('enviada', 'possivel_nota')`, entao a consulta toca so o que ainda espera
        // algo — e sem pesquisa aberta o metodo sai sem nem olhar o texto.
        //
        // ⚠️ `IgnoreQueryFilters` COM O `empresa_id` A MAO: o webhook roda SEM tenant no contexto.
        // ============================================================================
        var pesquisa = await db.PesquisasNps.IgnoreQueryFilters()
            .Where(p => p.EmpresaId == empresaId
                     && p.ContatoId == contatoId
                     && (p.Status == StatusPesquisaNps.Enviada
                      || p.Status == StatusPesquisaNps.PossivelNota))
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);

        if (pesquisa == null) return RespostaDaPesquisa.Nenhuma;

        // ===================== CITOU A PESQUISA, OU CITOU OUTRA COISA? =====================
        // ⚠️ MEDIDO NO `nexora_dev`: das tres entradas com `stanzaId`, DUAS citam uma saida nossa e
        // UMA cita outra entrada — o cliente citando a propria mensagem. "Houve citacao" nao basta;
        // tem de ser a citacao DESTA pergunta.
        //
        // E quando `mensagem_envio_id` e nulo (o caminho `Barrada` do motor nao sabe o id), a
        // comparacao simplesmente nao casa e cai nos outros criterios. E por isso que a leitura
        // nunca pode DEPENDER deste vinculo.
        // ==============================================================================
        var citouAPesquisa = false;

        if (!string.IsNullOrEmpty(stanzaIdCitado) && pesquisa.MensagemEnvioId != null)
        {
            citouAPesquisa = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(m => m.Id == pesquisa.MensagemEnvioId
                            && m.EmpresaId == empresaId
                            && m.WaMessageId == stanzaIdCitado, ct);
        }

        var lida = LeitorDeNota.Ler(texto, citouAPesquisa);

        if (lida.Resultado == LeituraDeNota.NaoEhNota) return RespostaDaPesquisa.Nenhuma;

        var agora = relogio.GetUtcNow().UtcDateTime;

        if (lida.Resultado == LeituraDeNota.Nota)
        {
            pesquisa.Status = StatusPesquisaNps.Respondida;
            pesquisa.Nota = (short)lida.Nota!.Value;
            pesquisa.Comentario = lida.Comentario;
            pesquisa.DataResposta = agora;
            pesquisa.MensagemRespostaId = mensagemId;

            // ⚠️ `ConfirmadaPorUsuarioId` FICA NULO, e e a distincao que permite medir o leitor
            // depois: muita confirmacao manual quer dizer que as regras dele estao apertadas.

            await MarcarTratadaAsync(mensagemId, ct);
            await db.SaveChangesAsync(ct);

            return RespostaDaPesquisa.NotaRegistrada;
        }

        // ===================== A DUVIDA VAI PARA UM HUMANO =====================
        // ⚠️ A NOTA E GRAVADA COMO SUSPEITA, e o status e o que a separa de um resultado: o
        // relatorio le `status = 'respondida'`, NUNCA `nota IS NOT NULL`. E por isso que
        // `ck_pesquisas_nps_respondida` nao cobre `possivel_nota`.
        //
        // ⚠️ E A MENSAGEM *NAO* E MARCADA COMO TRATADA. Ela pode nao ser nota nenhuma — "quero 2
        // unidades" e um pedido esperando resposta, e apagar a espera dele para perguntar "isto e
        // uma nota?" trocaria um atendimento perdido por uma duvida respondida.
        // ==================================================================
        pesquisa.Status = StatusPesquisaNps.PossivelNota;
        pesquisa.Nota = (short)lida.Nota!.Value;
        pesquisa.MensagemRespostaId = mensagemId;

        await db.SaveChangesAsync(ct);

        return RespostaDaPesquisa.DuvidaRegistrada;
    }

    /// <summary>⚠️ `ExecuteUpdate` E NAO A ENTIDADE: a mensagem acabou de ser inserida por SQL cru
    /// no `InserirMensagemAsync` e nao esta no rastreador. Carrega-la para mudar um booleano seria
    /// uma leitura a mais no caminho quente do webhook.</summary>
    private Task MarcarTratadaAsync(long mensagemId, CancellationToken ct) =>
        db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.Id == mensagemId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.TratadaPorAutomacao, true), ct);
}
