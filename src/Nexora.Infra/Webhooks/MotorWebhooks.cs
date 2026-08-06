using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;
using Nexora.Core.Webhooks;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Webhooks;

public record ResultadoRodadaWebhook(int Tentadas, int Entregues, int Reagendadas, int Desistidas)
{
    public static readonly ResultadoRodadaWebhook Zero = new(0, 0, 0, 0);
}

/// <summary>A DRENAGEM: pega o que venceu, posta, e decide o que fazer com o resultado.
///
/// ===================== A TABELA É A FILA =====================
/// Nada de broker, nada de fila distribuída — a mesma disciplina do envio de mensagem. A linha
/// nasce pendente com `proxima_tentativa_em`, a rodada busca o que venceu, e o resultado volta
/// para a própria linha. Uma peça a menos de infraestrutura para operar, e o histórico e a fila
/// são a mesma coisa.
///
/// ⚠️ LIMITE CONHECIDO, o mesmo do `AgendadorFollowUp`: não há lock distribuído. Com DUAS
/// instâncias, as duas drenam e o receptor pode receber a mesma entrega duas vezes. É por isso que
/// o `id` do evento vai no corpo — o receptor deduplica por ele, e essa é a defesa que funciona
/// mesmo com o job rodando duas vezes. Quando o Nexora escalar horizontal, um advisory lock do
/// Postgres resolve em poucas linhas.
/// =============================================================
///
/// Roda SEM tenant no contexto (é job): todo acesso usa `IgnoreQueryFilters`, como o
/// `DadosFollowUp`. Sem isso a fila volta vazia e nada é entregue, em silêncio.</summary>
public class MotorWebhooks(
    NexoraDbContext db,
    IClienteWebhook cliente,
    IResolvedorDns dns,
    TimeProvider relogio,
    ILogger<MotorWebhooks> log)
{
    /// <summary>Teto por rodada. Um receptor que voltou depois de um dia fora tem uma fila
    /// acumulada; drenar tudo de uma vez seguraria a rodada por minutos e atrasaria os eventos
    /// novos, que são os que importam. O resto sai na passada seguinte.</summary>
    private const int MaximoPorRodada = 200;

    public async Task<ResultadoRodadaWebhook> ExecutarAsync(CancellationToken ct = default)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;

        var pendentes = await db.EntregasWebhook.IgnoreQueryFilters()
            .Where(e => e.Status == StatusEntregaWebhook.Pendente
                     && e.ProximaTentativaEm != null && e.ProximaTentativaEm <= agora)
            .OrderBy(e => e.ProximaTentativaEm).ThenBy(e => e.Id)
            .Take(MaximoPorRodada)
            .ToListAsync(ct);

        if (pendentes.Count == 0) return ResultadoRodadaWebhook.Zero;

        // Os segredos, numa leitura só. Buscar por entrega daria N consultas para o mesmo punhado
        // de empresas.
        var empresas = pendentes.Select(e => e.EmpresaId).Distinct().ToList();
        var webhooks = await db.WebhooksSaida.IgnoreQueryFilters().AsNoTracking()
            .Where(w => empresas.Contains(w.EmpresaId))
            .ToDictionaryAsync(w => w.EmpresaId, ct);

        int entregues = 0, reagendadas = 0, desistidas = 0;

        foreach (var entrega in pendentes)
        {
            if (ct.IsCancellationRequested) break;

            var resultado = await TentarAsync(entrega, webhooks.GetValueOrDefault(entrega.EmpresaId), ct);

            switch (Aplicar(entrega, resultado))
            {
                case StatusEntregaWebhook.Entregue: entregues++; break;
                case StatusEntregaWebhook.Falhou: desistidas++; break;
                default: reagendadas++; break;
            }
        }

        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Rodada de webhooks: {Tentadas} tentadas, {Entregues} entregues, "
          + "{Reagendadas} reagendadas, {Desistidas} desistidas.",
            pendentes.Count, entregues, reagendadas, desistidas);

        return new ResultadoRodadaWebhook(pendentes.Count, entregues, reagendadas, desistidas);
    }

    /// <summary>Uma tentativa: revalida a URL e posta.</summary>
    private async Task<ResultadoEntrega> TentarAsync(
        EntregaWebhook entrega, WebhookSaida? webhook, CancellationToken ct)
    {
        // O webhook foi apagado ou desativado depois de o evento entrar na fila. Não é erro de
        // rede — é o cliente tendo mudado de ideia, e insistir seria mandar para um destino que
        // ele desligou.
        if (webhook is null || !webhook.Ativo)
            return new ResultadoEntrega(false, null,
                "O webhook foi desativado ou removido depois que este evento entrou na fila.");

        // ===================== A SEGUNDA VALIDAÇÃO, E POR QUE ELA EXISTE =====================
        // A URL já passou no cadastro. Isso não basta: `webhook.cliente.com` pode ter resolvido
        // para um IP público naquele dia e apontar para `127.0.0.1` agora — quem controla a zona
        // é o cliente, e ele muda sem tocar no Nexora.
        //
        // Validar só na entrada é validar um valor que o outro lado pode trocar depois. Esta
        // checagem custa uma consulta de DNS por tentativa e é o que fecha o buraco.
        // =====================================================================================
        var url = await ValidadorUrlWebhook.ValidarAsync(entrega.Url, dns, ct);
        if (!url.Ok)
        {
            log.LogWarning("Entrega {Id} recusada na validação de URL: {Motivo}", entrega.Id, url.Motivo);
            return new ResultadoEntrega(false, null, url.Motivo);
        }

        return await cliente.EntregarAsync(
            entrega.Url, webhook.Segredo, entrega.Payload,
            entrega.Evento.ParaApi(), entrega.EventoId, ct);
    }

    /// <summary>Escreve o resultado na linha e decide o próximo passo. Devolve o status final.</summary>
    private StatusEntregaWebhook Aplicar(EntregaWebhook entrega, ResultadoEntrega resultado)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;

        entrega.Tentativas += 1;
        entrega.CodigoResposta = resultado.Codigo;

        if (resultado.Aceitou)
        {
            entrega.Status = StatusEntregaWebhook.Entregue;
            entrega.EntregueEm = agora;
            entrega.ProximaTentativaEm = null;
            entrega.Erro = null;
            return StatusEntregaWebhook.Entregue;
        }

        entrega.Erro = Cortar(resultado.Erro);

        if (PoliticaEntrega.EsperaApos(entrega.Tentativas) is { } espera)
        {
            entrega.ProximaTentativaEm = agora.Add(espera);
            return StatusEntregaWebhook.Pendente;
        }

        // Esgotou. NÃO volta sozinha — só por reenvio manual do dono. Repetir para sempre
        // transformaria um receptor quebrado numa fila que só cresce.
        entrega.Status = StatusEntregaWebhook.Falhou;
        entrega.ProximaTentativaEm = null;
        return StatusEntregaWebhook.Falhou;
    }

    /// <summary>O erro vai para a tela. Um servidor que devolve uma página de erro inteira na
    /// mensagem de exceção não pode encher a coluna nem a tabela do dono.</summary>
    private static string? Cortar(string? erro) =>
        erro is null ? null : erro.Length <= 500 ? erro : erro[..500];

    /// <summary>Apaga o registro velho. Chamado na RODADA DIÁRIA.
    ///
    /// Sem expurgo a tabela cresce para sempre, e a de maior volume aqui é `mensagem.recebida` —
    /// uma empresa ativa gera dezenas por dia. Trinta dias é o suficiente para responder "o
    /// cliente diz que não recebeu", que é a única pergunta que este registro existe para
    /// responder.</summary>
    public async Task<int> ExpurgarAntigasAsync(CancellationToken ct = default)
    {
        var limite = relogio.GetUtcNow().UtcDateTime.AddDays(-PoliticaEntrega.DiasDeRetencao);

        var apagadas = await db.EntregasWebhook.IgnoreQueryFilters()
            .Where(e => e.CriadoEm < limite)
            .ExecuteDeleteAsync(ct);

        if (apagadas > 0)
            log.LogInformation("Expurgo de webhooks: {Apagadas} entregas com mais de {Dias} dias.",
                apagadas, PoliticaEntrega.DiasDeRetencao);

        return apagadas;
    }
}
