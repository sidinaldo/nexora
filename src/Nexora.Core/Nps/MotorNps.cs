using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;
using Nexora.Core.FollowUp;
using Nexora.Core.Tempo;
using Nexora.Core.Texto;
using Nexora.Core.Whatsapp;

namespace Nexora.Core.Nps;

public record ResultadoNps(int Agendadas, int Enviadas, int Adiadas, int Canceladas, int Expiradas, int Falhas)
{
    public static readonly ResultadoNps Zero = new(0, 0, 0, 0, 0, 0);

    public ResultadoNps Mais(ResultadoNps o) => new(
        Agendadas + o.Agendadas, Enviadas + o.Enviadas, Adiadas + o.Adiadas,
        Canceladas + o.Canceladas, Expiradas + o.Expiradas, Falhas + o.Falhas);
}

/// <summary>===================== A RODADA DA PESQUISA POS-VENDA =====================
///
/// Quatro trabalhos por empresa, nesta ordem, e a ordem importa:
///
///   1. AGENDAR    — venda concluida sem pesquisa ganha uma
///   2. CANCELAR   — venda desfeita, ou adiamento estourado
///   3. DISPARAR   — a pergunta sai, ou a data anda
///   4. EXPIRAR    — quem nao respondeu no prazo para de esperar
///
/// ⚠️ CANCELAR ANTES DE DISPARAR: a venda cancelada ontem nao pode receber a pergunta hoje. Na
/// ordem inversa, o cliente que desistiu da compra recebe "quanto voce nos recomendaria?".
///
/// ⚠️ EXPIRAR DEPOIS DE DISPARAR: expirar antes mataria no mesmo instante uma pesquisa cujo prazo
/// venceu — e ela nunca teria tido chance, porque o envio ainda nao aconteceu nesta rodada.
///
/// Mora no `Core` e nao conhece Postgres: o acesso e por `IDadosNps`, o envio por
/// `EnviadorMensagem`. Mesmo arranjo do `MotorFollowUp`, e as duas rodadas sao chamadas do mesmo
/// `AgendadorFollowUp` — um `BackgroundService` proprio teria de reimplementar as protecoes que
/// aquele ja tem (o catch que nao deixa excecao subir, o log protegido, o fuso de negocio).
/// ==========================================================================</summary>
public class MotorNps(
    IDadosNps dados,
    IDadosFollowUp comuns,
    EnviadorMensagem enviador,
    TimeProvider relogio,
    ILogger<MotorNps> log)
{
    public async Task<ResultadoNps> ExecutarAsync(CancellationToken ct = default)
    {
        var total = ResultadoNps.Zero;

        foreach (var empresa in await comuns.EmpresasAtivasAsync(ct))
        {
            try
            {
                // ⚠️ DESLIGADA NAO AGENDA NEM DISPARA — agendar acumularia fila silenciosa, e ligar o
                // botao um mes depois dispararia um mes de perguntas de uma vez.
                //
                // ⚠️ MAS O QUE JA SAIU AINDA EXPIRA. A checagem pulava a rodada INTEIRA, inclusive a
                // expiracao, e a pesquisa enviada antes de desligar ficava aberta para sempre — com
                // a leitura no webhook continuando a aceitar nota para ela, semanas depois.
                if (empresa.NpsAtivo)
                    total = total.Mais(await ExecutarParaEmpresaAsync(empresa, ct));
                else
                    total = total with { Expiradas = total.Expiradas + await ExpirarAsync(empresa, ct) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ISOLAMENTO POR EMPRESA, igual ao follow-up: uma empresa com dado ruim nao pode
                // derrubar a rodada das outras.
                log.LogError(ex, "Rodada de NPS falhou para a empresa {Id}.", empresa.Id);
            }
        }

        log.LogInformation(
            "Rodada de NPS: {Agendadas} agendadas, {Enviadas} enviadas, {Adiadas} adiadas, " +
            "{Canceladas} canceladas, {Expiradas} expiradas, {Falhas} falhas.",
            total.Agendadas, total.Enviadas, total.Adiadas, total.Canceladas,
            total.Expiradas, total.Falhas);

        return total;
    }

    private async Task<ResultadoNps> ExecutarParaEmpresaAsync(Empresa empresa, CancellationToken ct)
    {
        // TUDO no fuso de NEGOCIO, como no follow-up: a data civil "hoje" e a hora da janela saem
        // da MESMA base, e e isso que evita o off-by-one entre UTC e local.
        var fuso = FusoDeNegocio.Resolver(empresa.FusoHorario);
        var agora = FusoDeNegocio.AgoraNo(relogio, fuso);
        var hoje = DateOnly.FromDateTime(agora);

        var r = ResultadoNps.Zero;

        // ---- 1. Agendar o que a conclusao deixou para tras ------------------------------
        r = r with { Agendadas = await dados.AgendarPendentesAsync(empresa, hoje, ct) };

        // ---- 2. Cancelar o que nao deve mais sair ---------------------------------------
        var desfeitas = await dados.CancelarDeVendaDesfeitaAsync(empresa.Id, ct);
        var estouradas = await dados.CancelarAdiadasDemaisAsync(empresa.Id, hoje, ct);
        r = r with { Canceladas = desfeitas + estouradas };

        // ---- 3. Disparar -----------------------------------------------------------------
        r = r.Mais(await DispararAsync(empresa, fuso, hoje, ct));

        // ---- 4. Expirar o que nao foi respondido ----------------------------------------
        r = r with { Expiradas = await ExpirarAsync(empresa, ct) };

        return r;
    }

    /// <summary>O prazo conta de `data_envio`, que e quando a pergunta de fato saiu — nao de quando
    /// foi agendada. Uma pesquisa adiada tres dias teria expirado antes de ser lida. Num metodo so
    /// porque roda tambem com a pesquisa DESLIGADA (ver `ExecutarAsync`).</summary>
    private Task<int> ExpirarAsync(Empresa empresa, CancellationToken ct)
    {
        var limite = relogio.GetUtcNow().UtcDateTime.AddDays(-empresa.NpsDiasExpiracao);
        return dados.ExpirarAsync(empresa.Id, limite, ct);
    }

    private async Task<ResultadoNps> DispararAsync(
        Empresa empresa, TimeZoneInfo fuso, DateOnly hoje, CancellationToken ct)
    {
        var aDisparar = await dados.ADispararAsync(empresa.Id, hoje, ct);

        if (aDisparar.Count == 0) return ResultadoNps.Zero;

        var conexoes = await comuns.ConexoesAsync(empresa.Id, ct);

        if (conexoes.Count == 0)
        {
            log.LogInformation("Empresa {Id} nao tem conexao — NPS pulado.", empresa.Id);
            return ResultadoNps.Zero;
        }

        var agora = FusoDeNegocio.AgoraNo(relogio, fuso);

        // Feriados de hoje ate 14 dias a frente: o suficiente para o deslize achar o proximo dia
        // util mesmo numa emenda longa. Mesmo numero do `MotorFollowUp`, e pela mesma razao.
        var feriados = await comuns.FeriadosAsync(empresa.Id, hoje, hoje.AddDays(14), ct);
        var janela = new JanelaAtendimento(
            empresa.JanelaHoraInicio, empresa.JanelaHoraFim, empresa.JanelaDiasSemana);

        var janelaAberta = janela.Contem(agora, feriados);
        var proximoDia = CalendarioAtendimento.ProximaDataPermitida(
            hoje.AddDays(1), janela.DiasSemana, feriados);

        // FREIO POR CONEXAO, uma checagem por instancia por rodada — nao uma por mensagem, que
        // seria um GET na Evolution por disparo. Copiado do `MotorFollowUp` (ARQ-2).
        var noAr = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in conexoes)
        {
            noAr[c.InstanceName] = await enviador.InstanciaConectadaAsync(c.InstanceName, ct);
        }

        int enviadas = 0, adiadas = 0, falhas = 0;

        foreach (var p in aDisparar)
        {
            // Instancia desconhecida conta como CAIDA: adiar e recuperavel, postar as cegas nao.
            var conectada = noAr.TryGetValue(p.InstanceName, out var ok) && ok;

            if (!janelaAberta || !conectada)
            {
                await dados.AdiarAsync(p.PesquisaId, proximoDia, ct);
                adiadas++;
                continue;
            }

            // ⚠️ A PERGUNTA DO CONTATO VEM DEPOIS DA JANELA, e nao antes: ela e uma consulta por
            // pesquisa, e adiar por janela fechada nao precisa dela. Com a ordem invertida, uma
            // rodada fora do horario faria uma consulta por pesquisa para jogar o resultado fora.
            if (!await dados.PodeReceberHojeAsync(empresa.Id, p.ConversaId, hoje, ct))
            {
                await dados.AdiarAsync(p.PesquisaId, proximoDia, ct);
                adiadas++;
                continue;
            }

            var reserva = new Mensagem
            {
                EmpresaId = empresa.Id,
                ConversaId = p.ConversaId,
                ContatoId = p.ContatoId,
                ConexaoId = p.ConexaoId,
                InstanceName = p.InstanceName,
                Direcao = DirecaoMensagem.Saida,
                Texto = Preencher(empresa.NpsTexto, p.NomeDoContato, empresa.Nome),
                NegociacaoId = p.NegociacaoId,
                DataDisparo = hoje
            };

            var resultado = await enviador.EnviarNpsAsync(reserva, p.Telefone, ct);

            if (resultado == ResultadoEnvio.Enviada)
            {
                enviadas++;
                await dados.MarcarEnviadaAsync(
                    p.PesquisaId, reserva.Id, relogio.GetUtcNow().UtcDateTime, ct);

                // ESPACAMENTO: mandar em lote pela mesma instancia e o jeito classico de ter o
                // numero banido — e o Nexora roda em rota nao-oficial.
                await enviador.EspacarAsync(ct);
            }
            else if (resultado == ResultadoEnvio.Barrada)
            {
                // `uq_msg_nps` barrou E A LINHA QUE EXISTE JA SAIU — o enviador conferiu (ver
                // `EnviarNpsAsync`). Marca com o id e a HORA REAIS dela: o relogio da expiracao
                // conta de quando a pergunta chegou, e a hora desta rodada daria dias a mais.
                //
                // `Id` zero e `EnviadaEm` nulo so no caso em que a linha sumiu entre o INSERT e a
                // leitura; o vinculo fica nulo, e por isso a leitura da resposta nao pode DEPENDER
                // dele — ela casa por contato, e a citacao e um reforco, nao a chave.
                enviadas++;
                await dados.MarcarEnviadaAsync(
                    p.PesquisaId,
                    reserva.Id == 0 ? null : reserva.Id,
                    reserva.EnviadaEm ?? relogio.GetUtcNow().UtcDateTime, ct);
            }
            else if (resultado == ResultadoEnvio.Descartada)
            {
                // INT-XX: API oficial, janela de 24h fechada e sem template aprovado para a
                // pesquisa. Nada foi reservado: ela fica agendada para o proximo dia — se o
                // cliente escrever ate la, ou um template for escolhido, ela sai. Se nao, expira
                // pela `data_limite`, como sempre.
                await dados.AdiarAsync(p.PesquisaId, proximoDia, ct);
                adiadas++;
            }
            else
            {
                // FALHOU: a linha da mensagem fica com o erro gravado e a pesquisa fica `agendada`.
                // A PROXIMA RODADA REENVIA A MESMA LINHA — `EnviarNpsAsync` acha a reserva barrada,
                // ve que ela nao saiu e posta de novo. ⚠️ NAO E A DRENAGEM DO FOLLOW-UP quem faz
                // isso: ela so pega linha com `lembrete_id`, e um comentario antigo aqui dizia o
                // contrario. Era por esse engano que a pergunta que falhava nunca era reenviada.
                falhas++;
                await enviador.EspacarAsync(ct);
            }
        }

        if (!janelaAberta)
            log.LogInformation(
                "Empresa {Id}: NPS adiado para {Dia} (janela fechada).", empresa.Id, proximoDia);

        return ResultadoNps.Zero with { Enviadas = enviadas, Adiadas = adiadas, Falhas = falhas };
    }

    /// <summary>===================== AS VARIAVEIS DO TEXTO =====================
    ///
    /// ⚠️ `{{saudacao}}` EXISTE PORQUE `{{nome}}` TEM UMA ARMADILHA, e este projeto ja caiu nela.
    /// `NomeDePessoa` conta a historia: quando o WhatsApp nao manda `pushName`, o
    /// `CanonicalizadorTelefone` vira o NOME do contato, e `Primeiro` devolve NULO — "(84)" nao e
    /// nome. Com `"Oi, {{nome}}!"`, isso sai como "Oi, ! Aqui é da...".
    ///
    /// `Saudacao` decide a PONTUACAO junto com o nome, e por isso o texto PADRAO usa ela.
    /// `{{nome}}` fica disponivel para quem escrever o proprio texto, resolvendo para vazio — a
    /// escolha e do dono, e o padrao nao entrega o defeito de brinde.
    /// ====================================================================</summary>
    private static string Preencher(string texto, string nomeDoContato, string nomeDaEmpresa)
    {
        return texto
            .Replace("{{saudacao}}", NomeDePessoa.Saudacao("Oi", nomeDoContato))
            .Replace("{{nome}}", NomeDePessoa.Primeiro(nomeDoContato) ?? "")
            .Replace("{{empresa}}", nomeDaEmpresa);
    }
}
