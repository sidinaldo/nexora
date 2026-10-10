using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Seguranca;
using Nexora.Core.Webhooks;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;
using Nexora.Infra.Whatsapp;

namespace Nexora.Infra.CloudApi;

/// <summary>===================== UMA MUDANCA DA META, ABERTA (INT-XX) =====================
///
/// Mensagem recebida vira `MensagemEntrante` e segue pela MESMA `RecepcaoMensagem` da Evolution:
/// contato, conversa, anexo, nota, semaforo, anuncio, avisos — nada disso sabe de onde veio.
///
/// Status de mensagem nossa (`sent`, `delivered`, `read`, `failed`) vira o `ack` que ja existe.
///
/// RODA SEM TENANT, na rodada da fila: a empresa vem da conexao da linha.
/// ===============================================================================</summary>
public class ProcessadorWebhookCloudApi(
    NexoraDbContext db,
    IArmazenamentoMidia armazenamento,
    INotificadorPainel painel,
    IPublicadorEventos eventos,
    IPublicadorConversoes conversoes,
    ILeituraDaResposta leituraNps,
    IClienteCloudApi cloud,
    CifraSegredos cifra,
    TimeProvider relogio,
    ILogger<ProcessadorWebhookCloudApi> log)
{
    private RecepcaoMensagem? recepcao;

    private RecepcaoMensagem Recepcao =>
        recepcao ??= new RecepcaoMensagem(
            db, armazenamento, painel, eventos, conversoes, leituraNps, relogio, log);

    public async Task ProcessarAsync(WebhookMetaRecebido linha, CancellationToken ct)
    {
        var conexao = await db.Conexoes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == linha.ConexaoId && c.EmpresaId == linha.EmpresaId, ct);
        if (conexao == null) return;

        if (linha.Campo == "message_template_status_update")
        {
            await AtualizarModeloAsync(linha, ct);
            return;
        }

        if (linha.Campo != "messages")
        {
            log.LogDebug("Mudanca {Campo} da Meta sem tratamento.", linha.Campo);
            return;
        }

        var evento = LeitorEventoCloudApi.Ler(linha.Payload);

        foreach (var mensagem in evento.Mensagens)
            await ReceberAsync(conexao, mensagem, evento.Nomes, ct);

        foreach (var status in evento.Status)
            await AtualizarStatusAsync(conexao, status, ct);
    }

    // ==================================================================== mensagem recebida
    private async Task ReceberAsync(
        Conexao conexao, MensagemCloud m, IReadOnlyDictionary<string, string> nomes, CancellationToken ct)
    {
        // REC-2: reacao nao e mensagem. Virar linha acenderia o semaforo por causa de um 👍.
        if (m.Tipo == "reaction")
        {
            log.LogDebug("Reacao {Id} ignorada: nao e conteudo de conversa.", m.Id);
            return;
        }

        var telefone = CanonicalizadorTelefone.Canonicalizar(m.De);
        if (telefone.Length == 0) return;

        string? nome = null;
        if (nomes.TryGetValue(m.De, out var achado)) nome = achado;

        Func<CancellationToken, Task<MidiaDoProvedor>>? baixarMidia = null;
        if (m.MidiaId != null)
        {
            var mediaId = m.MidiaId;
            var nomeArquivo = m.NomeArquivo;
            baixarMidia = c => BaixarAsync(conexao, mediaId, nomeArquivo, c);
        }

        // Sem `timestamp` (nao deveria acontecer), vale a hora em que chegou.
        var quando = m.Quando;
        if (quando == DateTime.UnixEpoch) quando = relogio.GetUtcNow().UtcDateTime;

        var entrante = new MensagemEntrante(
            WaMessageId: m.Id,
            Telefone: telefone,
            WaId: m.De,
            // A Cloud API nao ecoa o que NOS mandamos: toda mensagem daqui e do cliente.
            Entrada: true,
            Quando: quando,
            Texto: m.Texto,
            NomePerfil: nome,
            CitadaWaId: m.CitadaId,
            TipoParaRotulo: m.Tipo,
            // A mensagem crua: e nela que o `referral` do clique em anuncio mora.
            PayloadRaw: m.Json,
            BaixarMidia: baixarMidia);

        await Recepcao.ReceberAsync(conexao, entrante, ct);
    }

    private async Task<MidiaDoProvedor> BaixarAsync(
        Conexao conexao, string mediaId, string? nomeArquivo, CancellationToken ct)
    {
        try
        {
            var token = cifra.Decifrar(conexao.AccessTokenCifrado!, FinalidadeSegredo.AccessToken);
            var midia = await cloud.BaixarMidiaAsync(mediaId, token, ct);
            return new MidiaDoProvedor(midia with { FileName = nomeArquivo }, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Nao foi possivel baixar a midia {Id} da Meta.", mediaId);
            return new MidiaDoProvedor(null, $"falha ao baixar da Meta: {ex.Message}");
        }
    }

    // ==================================================================== revisao de template
    /// <summary>A Meta decidiu sobre um template: aprovado, recusado, pausado. Achado pelo id dela
    /// DENTRO DA EMPRESA da conexao que recebeu.</summary>
    private async Task AtualizarModeloAsync(WebhookMetaRecebido linha, CancellationToken ct)
    {
        var evento = LeitorEventoCloudApi.LerEventoDeModelo(linha.Payload);
        if (evento == null) return;

        var modelo = await db.ModelosMensagem.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.EmpresaId == linha.EmpresaId && m.IdMeta == evento.IdMeta, ct);
        if (modelo == null)
        {
            // Template criado fora do Nexora, direto no painel da Meta: nao e daqui.
            log.LogInformation("Revisao do template {IdMeta} da Meta, que nao e do Nexora — ignorada.", evento.IdMeta);
            return;
        }

        if (RevisaoModelo.Aplicar(modelo, evento.Evento, evento.Motivo))
            await db.SaveChangesAsync(ct);
    }

    // ==================================================================== status
    /// <summary>O `ack` que ja existe, e que SO AVANCA — com uma excecao: `failed` depois de
    /// `sent` e a Meta dizendo que nao conseguiu entregar, e isso precisa aparecer. Depois de
    /// `delivered` ele nao volta atras: a mensagem chegou.</summary>
    private async Task AtualizarStatusAsync(Conexao conexao, StatusCloud s, CancellationToken ct)
    {
        short ack;
        if (s.Status == "sent") ack = 2;
        else if (s.Status == "delivered") ack = 3;
        else if (s.Status == "read") ack = 4;
        else if (s.Status == "failed") ack = 0;
        else return;

        var agora = relogio.GetUtcNow().UtcDateTime;
        int afetadas;

        if (ack == 0)
        {
            var erro = MotivoDaFalha(s.Erro);
            afetadas = await db.Database.ExecuteSqlRawAsync("""
                UPDATE mensagens
                   SET ack = 0, ack_em = {2}, erro = {3}
                 WHERE empresa_id = {0} AND wa_message_id = {1}
                   AND (ack IS NULL OR ack < 3)
                """, [conexao.EmpresaId, s.Id, agora, erro], ct);
        }
        else
        {
            afetadas = await db.Database.ExecuteSqlRawAsync("""
                UPDATE mensagens
                   SET ack = {2}, ack_em = {3}
                 WHERE empresa_id = {0} AND wa_message_id = {1}
                   AND (ack IS NULL OR ack < {2})
                """, [conexao.EmpresaId, s.Id, ack, agora], ct);
        }

        // Status repetido, fora de ordem, ou de mensagem que nao e nossa: nada a avisar.
        if (afetadas == 0) return;

        var mensagemId = await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.EmpresaId == conexao.EmpresaId && m.WaMessageId == s.Id)
            .Select(m => m.Id)
            .FirstOrDefaultAsync(ct);

        if (mensagemId != 0)
            await painel.StatusMensagemAsync(conexao.EmpresaId, mensagemId, ack, ct);
    }

    /// <summary>O status `failed` em portugues (BUG-XX). O leitor entrega "codigo titulo", com o
    /// titulo em ingles ("131049 This message was not delivered..."): o codigo conhecido vira a mesma
    /// frase do envio, e o desconhecido sai com o numero.</summary>
    private static string MotivoDaFalha(string? erro)
    {
        int? codigo = null;
        var primeiro = erro?.Split(' ', 2)[0];
        if (int.TryParse(primeiro, out var c)) codigo = c;

        if (ClienteCloudApi.PorCodigo(codigo) is { } conhecido) return conhecido;
        if (codigo != null) return $"A Meta não entregou esta mensagem (código {codigo}).";
        return "A Meta não entregou esta mensagem.";
    }
}
