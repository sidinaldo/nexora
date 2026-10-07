using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Texto;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Ver `ILeituraDaResposta` para o porque de cada decisao. Aqui mora o banco.</summary>
public class LeituraDaResposta(
    NexoraDbContext db, IAcoesDaNota acoes, TimeProvider relogio) : ILeituraDaResposta
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
        var pesquisa = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
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
            // ===================== UPDATE CONDICIONAL, E NAO ENTIDADE RASTREADA =====================
            // Duas mensagens do mesmo contato podem ser processadas em paralelo — o webhook nao
            // serializa por contato —, e as duas leriam a pesquisa como `enviada`. Com entidade
            // rastreada, as duas gravariam e as duas chamariam a acao: o cliente receberia DOIS
            // agradecimentos, e o detrator geraria lembrete em dobro.
            //
            // Aqui a segunda afeta ZERO linhas e nao faz nada. Mesma disciplina do
            // `ServicoVendas.ConcluirAsync`, que poe `Status == Ganha` no WHERE em vez de checar
            // antes.
            //
            // ⚠️ E A SUITE NAO ALCANCA ESTE `WHERE`, e esta escrito aqui para ninguem achar que
            // alcanca: sabotei-o e NADA CAIU. No caso SEQUENCIAL — que e o que um teste de
            // integracao consegue montar, porque tudo roda numa transacao so — a consulta ali em
            // cima JA filtra pelos dois estados abertos, entao a segunda passada nao acha pesquisa
            // nenhuma e sai antes de chegar aqui.
            //
            // O que este predicado cobre e a concorrencia DE VERDADE: as duas leituras acontecendo
            // antes de qualquer escrita. Ele fica porque e correto e custa nada, e porque o
            // `AcoesDaNotaDbTests` tem um teste que documenta que `AcoesDaNota` NAO e idempotente
            // por conta propria — a guarda mora aqui, de proposito.
            // ====================================================================================
            var mudou = await db.PesquisasNps.IgnoreQueryFilters()
                .Where(p => p.Id == pesquisa.Id
                         && (p.Status == StatusPesquisaNps.Enviada
                          || p.Status == StatusPesquisaNps.PossivelNota))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(p => p.Status, StatusPesquisaNps.Respondida)
                    .SetProperty(p => p.Nota, (short)lida.Nota!.Value)
                    .SetProperty(p => p.Comentario, lida.Comentario)
                    .SetProperty(p => p.DataResposta, (DateTime?)agora)
                    .SetProperty(p => p.MensagemRespostaId, (long?)mensagemId), ct);

            // ⚠️ `ConfirmadaPorUsuarioId` FICA NULO, e e a distincao que permite medir o leitor
            // depois: muita confirmacao manual quer dizer que as regras dele estao apertadas.

            if (mudou == 0) return RespostaDaPesquisa.Nenhuma;

            await MarcarTratadaAsync(mensagemId, ct);

            // DEPOIS do UPDATE, e so se ele pegou: a acao manda mensagem e cria lembrete, e
            // nenhuma das duas tem como ser desfeita.
            await acoes.ExecutarAsync(pesquisa.Id, ct);

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
        var virouDuvida = await db.PesquisasNps.IgnoreQueryFilters()
            .Where(p => p.Id == pesquisa.Id && p.Status == StatusPesquisaNps.Enviada)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, StatusPesquisaNps.PossivelNota)
                .SetProperty(p => p.Nota, (short?)lida.Nota!.Value)
                .SetProperty(p => p.MensagemRespostaId, (long?)mensagemId), ct);

        // ⚠️ SO DE `Enviada`, e nao dos dois estados: uma pesquisa que JA ESTA em `PossivelNota`
        // nao pode ter a suspeita reescrita por uma mensagem seguinte. O vendedor esta olhando a
        // primeira, e trocar o numero embaixo dele faria o botao "Confirmar nota 2" confirmar outra.
        if (virouDuvida == 0) return RespostaDaPesquisa.Nenhuma;

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
