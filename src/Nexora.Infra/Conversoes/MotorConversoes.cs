using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Conversoes;

public record ResultadoRodadaConversao(
    int Tentadas, int Entregues, int Reagendadas, int Desistidas, int Expiradas)
{
    public static readonly ResultadoRodadaConversao Zero = new(0, 0, 0, 0, 0);
}

/// <summary>A DRENAGEM da fila de conversões (INT-4).
///
/// Mesma disciplina do `MotorWebhooks`: a tabela é a fila, a linha nasce pendente com
/// `proxima_tentativa_em`, e o resultado volta para a própria linha.
///
/// ===================== O QUE ELE FAZ E O DE WEBHOOK NÃO =====================
/// **Marca `expirado` ANTES de tocar a rede.** A Meta recusa a requisição inteira por causa de um
/// evento com mais de 7 dias, e reenviar um desses nunca vai funcionar: gastar uma chamada nele é
/// gastar por nada, e deixá-lo como `falhou` faria a tela oferecer um botão de reenvio que só pode
/// fracassar.
///
/// **Desativa a credencial** quando a Meta recusa o token. Insistir 3× com token morto são três
/// linhas idênticas e zero informação — e, pior, a credencial ficaria "ativa" na tela enquanto nada
/// sai, que é o estado mais confuso possível para quem está olhando.
/// ============================================================================
///
/// Roda SEM tenant no contexto (é job): todo acesso usa `IgnoreQueryFilters`.
///
/// ⚠️ LIMITE CONHECIDO, o mesmo dos outros agendadores: sem lock distribuído, duas instâncias
/// drenam em paralelo. A defesa que funciona nesse caso é o `event_id` — a Meta deduplica por
/// `event_name` + `event_id`, que é exatamente o par que cada linha carrega.</summary>
public class MotorConversoes(
    NexoraDbContext db,
    IClienteMeta cliente,
    TimeProvider relogio,
    ILogger<MotorConversoes> log)
{
    public async Task<ResultadoRodadaConversao> ExecutarAsync(CancellationToken ct = default)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;

        // ===================== OS EXPIRADOS SAEM PRIMEIRO, E SEM REDE =====================
        // Um comando só, antes de qualquer coisa. Se ficassem na fila, cada um consumiria um slot da
        // rodada para receber um erro que já se sabe qual é.
        // =================================================================================
        var expiradas = await db.EventosConversao.IgnoreQueryFilters()
            .Where(e => e.Status == StatusConversao.Pendente && e.ExpiraEm <= agora)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, StatusConversao.Expirado)
                .SetProperty(e => e.ProximaTentativaEm, (DateTime?)null)
                .SetProperty(e => e.Erro, "A Meta só aceita eventos com até 7 dias."), ct);

        var pendentes = await db.EventosConversao.IgnoreQueryFilters()
            .Where(e => e.Status == StatusConversao.Pendente
                     && e.ProximaTentativaEm != null && e.ProximaTentativaEm <= agora)
            .OrderBy(e => e.ProximaTentativaEm).ThenBy(e => e.Id)
            .Take(PoliticaConversao.MaximoPorRodada)
            .ToListAsync(ct);

        if (pendentes.Count == 0)
            return expiradas == 0
                ? ResultadoRodadaConversao.Zero
                : ResultadoRodadaConversao.Zero with { Expiradas = expiradas };

        // As credenciais, numa leitura só. Buscar por evento daria N consultas para o mesmo punhado
        // de empresas. RASTREADAS de propósito: a desativação escreve nelas.
        var empresas = pendentes.Select(e => e.EmpresaId).Distinct().ToList();
        var credenciais = await db.CredenciaisConversao.IgnoreQueryFilters()
            .Where(c => empresas.Contains(c.EmpresaId) && c.Plataforma == PlataformaConversao.Meta)
            .ToDictionaryAsync(c => c.EmpresaId, ct);

        int entregues = 0, reagendadas = 0, desistidas = 0;

        foreach (var evento in pendentes)
        {
            if (ct.IsCancellationRequested) break;

            var credencial = credenciais.GetValueOrDefault(evento.EmpresaId);
            var resultado = await TentarAsync(evento, credencial, ct);

            switch (Aplicar(evento, credencial, resultado))
            {
                case StatusConversao.Entregue: entregues++; break;
                case StatusConversao.Falhou: desistidas++; break;
                default: reagendadas++; break;
            }
        }

        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Rodada de conversões: {Tentadas} tentadas, {Entregues} entregues, "
          + "{Reagendadas} reagendadas, {Desistidas} desistidas, {Expiradas} expiradas.",
            pendentes.Count, entregues, reagendadas, desistidas, expiradas);

        return new ResultadoRodadaConversao(
            pendentes.Count, entregues, reagendadas, desistidas, expiradas);
    }

    private async Task<ResultadoEnvioMeta> TentarAsync(
        EventoConversao evento, CredencialConversao? credencial, CancellationToken ct)
    {
        // A credencial foi removida, desligada, ou o consentimento foi retirado depois de o evento
        // entrar na fila. Não é erro de rede: é o cliente tendo mudado de ideia.
        //
        // ⚠️ `PodeEnviar` aqui TAMBÉM, e não só no publicador: entre enfileirar e drenar passam
        // minutos, e é exatamente nessa janela que alguém desliga o interruptor.
        if (credencial?.PodeEnviar(evento.Tipo) != true)
            return new ResultadoEnvioMeta(false, null, null, null,
                "O envio de conversões foi desligado depois que este evento entrou na fila.");

        return await cliente.EnviarAsync(
            credencial.Identificador, credencial.Token!, evento.Payload, credencial.CodigoTeste, ct);
    }

    /// <summary>Escreve o resultado na linha, desativa a credencial se for o caso, e decide o
    /// próximo passo. Devolve o status final.</summary>
    private StatusConversao Aplicar(
        EventoConversao evento, CredencialConversao? credencial, ResultadoEnvioMeta resultado)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;

        evento.Tentativas += 1;
        evento.CodigoResposta = resultado.Codigo;
        evento.CodigoMeta = resultado.CodigoMeta;
        evento.FbtraceId = Cortar(resultado.FbtraceId, 100);

        if (resultado.Aceitou)
        {
            evento.Status = StatusConversao.Entregue;
            evento.EntregueEm = agora;
            evento.ProximaTentativaEm = null;
            evento.Erro = null;
            return StatusConversao.Entregue;
        }

        evento.Erro = Cortar(resultado.Erro, 500);

        var decisao = PoliticaConversao.Classificar(resultado.CodigoMeta);

        // ===================== A DESATIVAÇÃO, E POR QUE ELA É AQUI =====================
        // Não é `credencial.Ativo = false`: aquele é o interruptor da PESSOA, e sobrescrevê-lo faria
        // "religar" virar adivinhação. `DesativadaEm` é o do sistema, e o motivo vai para a tela em
        // português, porque sem ele os eventos param em silêncio.
        // ==============================================================================
        if (decisao.DesativarCredencial && credencial is not null && credencial.DesativadaEm is null)
        {
            credencial.DesativadaEm = agora;
            credencial.DesativadaMotivo = decisao.Motivo;

            log.LogWarning(
                "Credencial de conversão da empresa {Empresa} desativada: {Motivo} (código {Codigo})",
                evento.EmpresaId, decisao.Motivo, resultado.CodigoMeta);
        }

        if (decisao.TentarDeNovo && PoliticaConversao.EsperaApos(evento.Tentativas) is { } espera)
        {
            evento.ProximaTentativaEm = agora.Add(espera);
            return StatusConversao.Pendente;
        }

        // Esgotou, ou é permanente. NÃO volta sozinha — só por reenvio manual.
        evento.Status = StatusConversao.Falhou;
        evento.ProximaTentativaEm = null;
        return StatusConversao.Falhou;
    }

    /// <summary>Apaga o registro velho. Chamado na RODADA DIÁRIA, ao lado do expurgo de webhooks.
    ///
    /// ⚠️ O RASTRO **NÃO** EXPIRA EM 30 DIAS, e a diferença é deliberada: a venda pode fechar em
    /// três meses, e o `Purchase` precisa do `fbc` do clique original. O rastro morre com a
    /// anonimização do contato; o que sai daqui é só o registro do que já foi enviado.</summary>
    public async Task<int> ExpurgarAntigosAsync(CancellationToken ct = default)
    {
        var limite = relogio.GetUtcNow().UtcDateTime.AddDays(-PoliticaConversao.DiasDeRetencao);

        var apagados = await db.EventosConversao.IgnoreQueryFilters()
            .Where(e => e.CriadoEm < limite)
            .ExecuteDeleteAsync(ct);

        if (apagados > 0)
            log.LogInformation("Expurgo de conversões: {Apagados} eventos com mais de {Dias} dias.",
                apagados, PoliticaConversao.DiasDeRetencao);

        return apagados;
    }

    private static string? Cortar(string? texto, int teto) =>
        texto is null ? null : texto.Length <= teto ? texto : texto[..teto];
}
