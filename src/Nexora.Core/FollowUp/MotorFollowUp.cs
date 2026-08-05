using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;
using Nexora.Core.Tempo;
using Nexora.Core.Whatsapp;

namespace Nexora.Core.FollowUp;

public record ResultadoRodada(int Gerados, int Enviados, int Adiados, int Barrados, int Falhas, int Expirados)
{
    public static readonly ResultadoRodada Zero = new(0, 0, 0, 0, 0, 0);

    public ResultadoRodada Mais(ResultadoRodada o) => new(
        Gerados + o.Gerados, Enviados + o.Enviados, Adiados + o.Adiados,
        Barrados + o.Barrados, Falhas + o.Falhas, Expirados + o.Expirados);
}

/// <summary>A rodada de follow-up: gera os lembretes do dia e despacha os que venceram.
///
/// Estrutura replicada do MotorReguaCobranca do Recupera. O ESQUELETO e os mecanismos de
/// proteção transferem; a ELEGIBILIDADE não — lá é vencimento de dívida com offset de dias,
/// aqui é inatividade da conversa.
///
/// O motor DECIDE quem receber. Quem ENVIA é o EnviadorMensagem (bloco 4) — e é lá que mora o
/// protocolo grava→dispara→confirma. O motor não sabe que existe Evolution API.</summary>
public class MotorFollowUp(
    IDadosFollowUp dados,
    EnviadorMensagem enviador,
    TimeProvider relogio,
    ILogger<MotorFollowUp> log)
{
    public async Task<ResultadoRodada> ExecutarAsync(CancellationToken ct = default)
    {
        var total = ResultadoRodada.Zero;

        foreach (var empresa in await dados.EmpresasAtivasAsync(ct))
        {
            try
            {
                total = total.Mais(await ExecutarParaEmpresaAsync(empresa, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ISOLAMENTO POR EMPRESA: uma empresa com dado ruim (conexão órfã, contato sem
                // telefone) não pode derrubar a rodada das outras. Sem este catch, a primeira
                // exceção interrompe o laço e ninguém depois dela recebe follow-up.
                log.LogError(ex, "Rodada de follow-up falhou para a empresa {Id}.", empresa.Id);
            }
        }

        log.LogInformation(
            "Rodada de follow-up: {Gerados} gerados, {Enviados} enviados, {Adiados} adiados, " +
            "{Barrados} barrados por invariante, {Falhas} falhas, {Expirados} expirados.",
            total.Gerados, total.Enviados, total.Adiados, total.Barrados, total.Falhas, total.Expirados);

        return total;
    }

    private async Task<ResultadoRodada> ExecutarParaEmpresaAsync(Empresa empresa, CancellationToken ct)
    {
        // TUDO no fuso de NEGÓCIO, nunca no do servidor: a data civil "hoje" e a hora da janela
        // saem da MESMA base, evitando o off-by-one entre UTC e local. Ver FusoDeNegocio.
        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);
        var agora = FusoDeNegocio.AgoraNo(relogio, fuso);
        var hoje = DateOnly.FromDateTime(agora);

        var conexao = await dados.ConexaoAsync(empresa.Id, ct);
        if (conexao is null)
        {
            log.LogInformation("Empresa {Id} ({Nome}) não tem conexão — pulando.", empresa.Id, empresa.Nome);
            return ResultadoRodada.Zero;
        }

        // Feriados de hoje até 14 dias à frente: o suficiente para o deslize do reserve-defer
        // achar o próximo dia útil mesmo numa emenda longa de feriado.
        var feriados = await dados.FeriadosAsync(empresa.Id, hoje, hoje.AddDays(14), ct);
        var janela = new JanelaAtendimento(
            empresa.JanelaHoraInicio, empresa.JanelaHoraFim, empresa.JanelaDiasSemana);

        var janelaAberta = janela.Contem(agora, feriados);

        // O dia em que o follow-up deve sair: hoje, se a empresa atende; senão desliza.
        var dataAlvo = CalendarioAtendimento.ProximaDataPermitida(hoje, janela.DiasSemana, feriados);

        // FREIO POR CONEXÃO: uma checagem por empresa por rodada. Com o número caído, postar só
        // empilha erro na coluna `erro` e atrasa a fila.
        var conectada = await enviador.InstanciaConectadaAsync(conexao.Value.InstanceName, ct);
        var podePostar = janelaAberta && conectada;

        if (!podePostar)
            log.LogInformation(
                "Empresa {Id}: reservando sem postar (janela {Janela}, conexão {Conexao}).",
                empresa.Id, janelaAberta ? "aberta" : "fechada", conectada ? "no ar" : "caída");

        var r = ResultadoRodada.Zero;

        // ---- 1. Expirar o que passou da janela de reenvio ------------------------------
        // ANTES de drenar: o que expirou não deve nem ser tentado, e a partir daqui vira número
        // próprio no endpoint de saúde em vez de sumir do radar.
        r = r with { Expirados = await enviador.ExpirarVencidasAsync(empresa.Id, ct) };

        // ---- 2. Gerar os lembretes de hoje ---------------------------------------------
        r = r.Mais(await GerarLembretesAsync(empresa, dataAlvo, ct));

        // ---- 3. Drenar o que ficou pelo caminho ----------------------------------------
        if (podePostar)
            r = r.Mais(await DrenarPendentesAsync(empresa, ct));

        // ---- 4. Despachar os lembretes vencidos ----------------------------------------
        r = r.Mais(await DispararLembretesAsync(empresa, conexao.Value, hoje, dataAlvo, podePostar, ct));

        return r;
    }

    /// <summary>=========== A REGRA DE GERAÇÃO (código novo) ===========
    ///
    /// O Recupera gera por `dias_offset` sobre o vencimento da dívida. O Nexora não tem
    /// vencimento: a elegibilidade é INATIVIDADE DA CONVERSA.
    ///
    /// A condição que mais importa é "a última mensagem foi de SAÍDA". Se a última foi de
    /// ENTRADA, o cliente está esperando resposta — e isso é o SEMÁFORO, não follow-up.
    /// Confundir os dois faz o sistema cobrar o vendedor duas vezes pela mesma coisa: uma no
    /// vermelho da caixa, outra no Meu Dia.</summary>
    private async Task<ResultadoRodada> GerarLembretesAsync(
        Empresa empresa, DateOnly dataAlvo, CancellationToken ct)
    {
        // Data-alvo calculada na APLICAÇÃO. `limite` vira um parâmetro comparado contra a
        // coluna; qualquer função sobre a coluna (CURRENT_DATE - ultima_mensagem_em) descartaria
        // o índice e a varredura viraria seq scan quando a base crescer.
        var limite = relogio.GetUtcNow().UtcDateTime.AddDays(-empresa.DiasSemRespostaFollowUp);

        var inativas = await dados.ConversasInativasAsync(empresa.Id, limite, ct);
        int gerados = 0, barrados = 0;

        foreach (var c in inativas)
        {
            var id = await dados.CriarLembreteAutomaticoAsync(
                empresa.Id, c.ContatoId, c.ConversaId, c.ResponsavelId, dataAlvo,
                $"Retomar contato com {c.ContatoNome}",
                $"Oi, {PrimeiroNome(c.ContatoNome)}! Passando para saber se você ainda tem interesse.",
                ct);

            // NULL = o teto diário barrou (uq_lembrete_teto_diario). É resultado ESPERADO, não
            // erro: significa que já existe follow-up automático para este contato hoje.
            if (id is null) barrados++;
            else gerados++;
        }

        return ResultadoRodada.Zero with { Gerados = gerados, Barrados = barrados };
    }

    /// <summary>Reservas que nunca saíram (Evolution fora, janela fechada). Reaproveitam a MESMA
    /// linha — não criam reserva nova, então a invariante segue valendo.</summary>
    private async Task<ResultadoRodada> DrenarPendentesAsync(Empresa empresa, CancellationToken ct)
    {
        int enviados = 0, falhas = 0;

        foreach (var pendente in await enviador.PendentesAsync(empresa.Id, ct))
        {
            var telefone = await dados.TelefoneDoContatoAsync(pendente.ContatoId, ct);
            if (telefone is null) { falhas++; continue; }

            if (await enviador.ReenviarAsync(pendente, telefone, ct) == ResultadoEnvio.Enviada) enviados++;
            else falhas++;

            // ESPAÇAMENTO: mandar em lote pela mesma instância é o jeito clássico de ter o
            // número banido — e o Nexora roda em rota não-oficial.
            await enviador.EspacarAsync(ct);
        }

        return ResultadoRodada.Zero with { Enviados = enviados, Falhas = falhas };
    }

    private async Task<ResultadoRodada> DispararLembretesAsync(
        Empresa empresa, (long Id, string InstanceName) conexao, DateOnly hoje, DateOnly dataAlvo,
        bool podePostar, CancellationToken ct)
    {
        int enviados = 0, adiados = 0, barrados = 0, falhas = 0;

        foreach (var l in await dados.LembretesADispararAsync(empresa.Id, hoje, ct))
        {
            var reserva = new Mensagem
            {
                EmpresaId = empresa.Id,
                ConversaId = l.ConversaId!.Value,
                ContatoId = l.ContatoId,
                ConexaoId = l.ConexaoId,
                InstanceName = l.InstanceName,
                Direcao = DirecaoMensagem.Saida,
                Texto = l.Texto,
                LembreteId = l.LembreteId,
                // RESERVE-DEFER: fora da janela, a linha é reservada carimbando o PRÓXIMO dia
                // permitido. Preserva a data-alvo sem duplicar e sem perder o envio.
                DataDisparo = podePostar ? hoje : dataAlvo
            };

            var resultado = podePostar
                ? await enviador.EnviarLembreteAsync(reserva, l.Telefone, ct)
                : await enviador.ReservarLembreteAsync(reserva, ct);

            switch (resultado)
            {
                case ResultadoEnvio.Enviada:
                    enviados++;
                    await dados.ConcluirLembreteAsync(l.LembreteId, ct);
                    await enviador.EspacarAsync(ct);
                    break;

                case ResultadoEnvio.Adiada:
                    // Reservada sem POST: NÃO conclui o lembrete e NÃO espaça (não houve envio).
                    adiados++;
                    break;

                case ResultadoEnvio.Barrada:
                    // Este lembrete já gerou mensagem (uq_msg_lembrete). Concluir mesmo assim:
                    // a mensagem existe, e deixar o lembrete pendente o faria voltar todo dia.
                    barrados++;
                    await dados.ConcluirLembreteAsync(l.LembreteId, ct);
                    break;

                case ResultadoEnvio.Falhou:
                    // A linha fica com o erro; a drenagem tenta de novo. NÃO conclui.
                    falhas++;
                    await enviador.EspacarAsync(ct);
                    break;
            }
        }

        return ResultadoRodada.Zero with
        {
            Enviados = enviados, Adiados = adiados, Barrados = barrados, Falhas = falhas
        };
    }

    private static string PrimeiroNome(string? nomeCompleto) =>
        string.IsNullOrWhiteSpace(nomeCompleto)
            ? "tudo bem"
            : nomeCompleto.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
}
