using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;

namespace Nexora.Infra.Evolution;

public class OpcoesEvolution
{
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Header `apikey`. E a AUTHENTICATION_API_KEY do container.</summary>
    public string ApiKey { get; set; } = "";
}

/// <summary>Cliente HTTP da Evolution API. E o UNICO ponto do sistema que fala com ela.
/// Nunca lemos as tabelas dela (modelos Prisma, versionados por eles) — so REST + webhook.</summary>
public class ClienteEvolution(HttpClient http, ILogger<ClienteEvolution> log) : IClienteWhatsApp
{
    /// <summary>POST /message/sendText/{instance}. A resposta traz key.id, que e o que
    /// correlaciona este envio com o ACK que chega depois no webhook messages.update.</summary>
    public async Task<string> EnviarTextoAsync(string instanceName, string telefone, string texto, CancellationToken ct)
    {
        // ARMADILHA DO NONO DIGITO: no Brasil muita conta de WhatsApp vive SEM o nono digito,
        // mesmo o numero tendo 9 (ex.: 5584 99428-1968 -> a conta e 5584 9428-1968). Mandar com
        // o 9 para uma conta que nao o tem cai num JID fantasma: a mensagem fica PENDING e NUNCA
        // chega. O whatsappNumbers resolve para o JID real (com ou sem o 9); mandamos para ele.
        var numero = await ResolverNumeroAsync(instanceName, telefone, ct);

        HttpResponseMessage resposta;
        try
        {
            resposta = await http.PostAsJsonAsync(
                $"message/sendText/{instanceName}",
                new { number = numero, text = texto },
                ct);
        }
        catch (Exception ex)
        {
            throw new IntegracaoWhatsAppException($"Evolution API inacessivel ({ex.Message}).", ex);
        }

        var corpo = await resposta.Content.ReadAsStringAsync(ct);

        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException($"Evolution API respondeu {(int)resposta.StatusCode}: {corpo}");

        var waMessageId = ExtrairIdDaMensagem(corpo);
        if (waMessageId is null)
        {
            // Nao lancamos: a mensagem PROVAVELMENTE foi enviada (HTTP 2xx). Falhar aqui faria o
            // envio ser tentado de novo e o contato receber duas vezes. Registramos o envio sem
            // o id — o custo e nao conseguir casar o ACK depois.
            log.LogWarning("Evolution respondeu 2xx mas sem key.id. Corpo: {Corpo}", corpo);
            return "";
        }

        return waMessageId;
    }

    private static string? ExtrairIdDaMensagem(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("key", out var key) && key.TryGetProperty("id", out var id)
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>POST /chat/whatsappNumbers: pergunta a Evolution qual e o JID real do numero e
    /// devolve o numero canonico (resolve o nono digito). Lanca se o numero NAO existe no
    /// WhatsApp — falhar claro e melhor que ficar PENDING para sempre; o vendedor corrige o
    /// cadastro. Se a checagem em si nao der (rede/formato), cai no numero original — a checagem
    /// nao pode ser o motivo de um envio valido nao sair.</summary>
    private async Task<string> ResolverNumeroAsync(string instance, string numero, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync(
                $"chat/whatsappNumbers/{instance}", new { numbers = new[] { numero } }, ct);
        }
        catch (Exception ex)
        {
            throw new IntegracaoWhatsAppException($"Evolution API inacessivel ({ex.Message}).", ex);
        }

        if (!resp.IsSuccessStatusCode) return numero;   // nao deu para checar -> tenta o original

        var (existe, numeroReal) = LerResolucao(await resp.Content.ReadAsStringAsync(ct));
        if (existe == false)
            throw new IntegracaoWhatsAppException(
                $"O numero {numero} nao esta no WhatsApp — confira o cadastro do contato.");

        return numeroReal ?? numero;
    }

    /// <summary>Le [{ "jid": "558494281968@s.whatsapp.net", "exists": true, "number": "..." }].
    /// Existe=null quando o formato foge do esperado (ai o chamador usa o numero original).</summary>
    private static (bool? Existe, string? NumeroReal) LerResolucao(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return (null, null);

            var item = doc.RootElement[0];
            bool? existe = item.TryGetProperty("exists", out var e)
                ? e.ValueKind == JsonValueKind.True
                : null;
            string? numeroReal = item.TryGetProperty("jid", out var j) && j.GetString() is { } jid
                ? jid.Split('@')[0]      // o numero real, com ou sem o nono digito
                : null;
            return (existe, numeroReal);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    // ======================= MIDIA =============================================

    /// <summary>POST /message/sendMedia. Mesma resolucao de numero (nono digito) do texto.
    /// media = base64 SEM o prefixo data:.</summary>
    public async Task<string> EnviarMidiaAsync(string instanceName, string telefone, string base64,
        string mediatype, string mimeType, string fileName, string? legenda, CancellationToken ct)
    {
        var numero = await ResolverNumeroAsync(instanceName, telefone, ct);

        HttpResponseMessage resposta;
        try
        {
            resposta = await http.PostAsJsonAsync($"message/sendMedia/{instanceName}", new
            {
                number = numero,
                mediatype,
                mimetype = mimeType,
                media = base64,
                fileName,
                caption = legenda ?? ""
            }, ct);
        }
        catch (Exception ex)
        {
            throw new IntegracaoWhatsAppException($"Evolution API inacessivel ({ex.Message}).", ex);
        }

        var corpo = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException($"Evolution API respondeu {(int)resposta.StatusCode}: {corpo}");

        return ExtrairIdDaMensagem(corpo) ?? "";
    }

    /// <summary>POST /chat/getBase64FromMediaMessage — baixa o conteudo de uma midia recebida
    /// pelo wa_message_id. Null se a Evolution nao devolver base64.</summary>
    public async Task<MidiaRecebida?> ObterMidiaAsync(
        string instanceName, string waMessageId, string mensagemJson, CancellationToken ct)
    {
        // ===================== A MENSAGEM INTEIRA, NAO SO A CHAVE =====================
        // A Evolution decodifica a midia a partir da propria mensagem (a `mediaKey` vem nela).
        // Mandando so `{key:{id}}`, ela vai procurar no banco DELA — e o compose desliga
        // `DATABASE_SAVE_DATA_NEW_MESSAGE` de proposito, para nao manter um segundo acervo de
        // conversa de cliente. Resultado: "Message not found", e toda midia recebida entrava sem
        // anexo, sem erro na tela.
        //
        // Verificado contra a Evolution v2.3.7 com uma mensagem real: com a chave, 400; com a
        // mensagem inteira, o base64 do OGG.
        // =============================================================================
        var corpo = new StringContent(
            $$"""{"message": {{mensagemJson}} }""", System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try { resp = await http.PostAsync($"chat/getBase64FromMediaMessage/{instanceName}", corpo, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Evolution inacessivel ao baixar a midia {Id}.", waMessageId);
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("Evolution recusou a midia {Id}: {Codigo} {Corpo}",
                waMessageId, (int)resp.StatusCode, json);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;

            string? Campo(params string[] nomes)
            {
                foreach (var n in nomes)
                    if (raiz.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
                return null;
            }

            var base64 = Campo("base64");
            return base64 is null ? null
                : new MidiaRecebida(base64, Campo("mimetype", "mimeType"), Campo("fileName"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>POST /message/sendWhatsAppAudio — a rota de NOTA DE VOZ.
    ///
    /// Diferente do `sendMedia` com `mediatype=audio`, que manda o arquivo como anexo comum. Os
    /// dois "funcionam" e produzem coisas diferentes no celular do cliente.</summary>
    public async Task<string> EnviarAudioAsync(
        string instanceName, string telefone, string base64, CancellationToken ct)
    {
        var numero = await ResolverNumeroAsync(instanceName, telefone, ct);

        HttpResponseMessage resposta;
        try
        {
            resposta = await http.PostAsJsonAsync($"message/sendWhatsAppAudio/{instanceName}",
                new { number = numero, audio = base64 }, ct);
        }
        catch (Exception ex)
        {
            throw new IntegracaoWhatsAppException($"Evolution API inacessivel ({ex.Message}).", ex);
        }

        var corpoResp = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(
                $"Evolution API respondeu {(int)resposta.StatusCode}: {corpoResp}");

        return ExtrairIdDaMensagem(corpoResp) ?? "";
    }

    // ======================= CONEXAO DA INSTANCIA (QR code) =====================
    // Estados possiveis devolvidos ao servico:
    //   open        -> conectada ao WhatsApp
    //   connecting  -> aguardando o QR ser lido
    //   close       -> instancia existe na Evolution mas nao conectada
    //   nao_criada  -> a instancia ainda nao foi criada na Evolution
    //   offline     -> a Evolution API nao respondeu (container fora do ar)

    /// <summary>GET /instance/connectionState/{instance}. 404 = instancia nao criada;
    /// erro de rede = Evolution fora do ar.</summary>
    public async Task<string> StatusInstanciaAsync(string instance, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await http.GetAsync($"instance/connectionState/{instance}", ct); }
        catch (Exception ex) { log.LogWarning(ex, "Evolution inacessivel ao checar status."); return "offline"; }

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return "nao_criada";
        if (!resp.IsSuccessStatusCode) return "close";

        var json = await resp.Content.ReadAsStringAsync(ct);
        return LerEstado(json) ?? "close";
    }

    /// <summary>Garante que a instancia existe (cria se preciso) e devolve o QR code — ou, se
    /// <paramref name="numeroPareamento"/> for informado, o CODIGO DE PAREAMENTO (a Evolution
    /// exige o numero para gera-lo).</summary>
    public async Task<RespostaQr> ConectarInstanciaAsync(string instance, string? numeroPareamento, CancellationToken ct)
    {
        var estado = await StatusInstanciaAsync(instance, ct);
        if (estado == "offline")
            throw new IntegracaoWhatsAppException("A Evolution API nao esta no ar. Suba o container e tente de novo.");
        if (estado == "open")
            return new RespostaQr(null, null, null, "open");   // ja conectada, nao ha QR

        if (estado == "nao_criada")
            await CriarInstanciaAsync(instance, ct);

        // Com numero -> a Evolution devolve pairingCode (codigo por numero); sem -> QR.
        var url = $"instance/connect/{instance}";
        if (!string.IsNullOrWhiteSpace(numeroPareamento))
            url += $"?number={Uri.EscapeDataString(numeroPareamento)}";

        HttpResponseMessage resp;
        try { resp = await http.GetAsync(url, ct); }
        catch (Exception ex) { throw new IntegracaoWhatsAppException($"Evolution API inacessivel ({ex.Message}).", ex); }

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException($"Evolution API respondeu {(int)resp.StatusCode}: {json}");

        return LerQr(json);
    }

    /// <summary>GET /instance/fetchInstances?instanceName= — os dados da instancia conectada
    /// (numero real via ownerJid, nome/foto do perfil, estado). Defensivo com as variacoes de
    /// formato da Evolution (array/objeto, campos no topo ou aninhados em "instance").
    /// NUNCA lanca.</summary>
    public async Task<DetalhesInstancia?> ObterDetalhesInstanciaAsync(string instance, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await http.GetAsync($"instance/fetchInstances?instanceName={Uri.EscapeDataString(instance)}", ct); }
        catch (Exception ex) { log.LogWarning(ex, "Evolution inacessivel ao buscar detalhes da instancia."); return null; }

        if (!resp.IsSuccessStatusCode) return null;

        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var raiz = doc.RootElement;
            var item = raiz.ValueKind == JsonValueKind.Array
                ? (raiz.GetArrayLength() > 0 ? raiz[0] : default)
                : raiz;
            if (item.ValueKind != JsonValueKind.Object) return null;

            // Versoes antigas aninham tudo em "instance"; as novas trazem no topo.
            var fonte = item.TryGetProperty("instance", out var inst) && inst.ValueKind == JsonValueKind.Object
                ? inst : item;

            string? Str(params string[] nomes)
            {
                foreach (var n in nomes)
                    if (fonte.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
                return null;
            }

            return new DetalhesInstancia(
                Str("ownerJid", "owner", "wuid"),
                Str("profileName"),
                Str("profilePicUrl", "profilePictureUrl"),
                Str("connectionStatus", "state") ?? "open");
        }
        catch (JsonException) { return null; }
    }

    /// <summary>DELETE /instance/logout/{instance}. Desconecta o numero (mantem a instancia).</summary>
    public async Task DesconectarInstanciaAsync(string instance, CancellationToken ct)
    {
        try { await http.DeleteAsync($"instance/logout/{instance}", ct); }
        catch (Exception ex) { throw new IntegracaoWhatsAppException($"Evolution API inacessivel ({ex.Message}).", ex); }
    }

    /// <summary>DELETE /instance/delete/{instance}, precedido de logout.
    ///
    /// O logout vem antes porque a Evolution RECUSA apagar instancia conectada (400 "Instance is
    /// connected"), e quem chama aqui ja decidiu que ela vai embora. A falha do logout e engolida
    /// de proposito: se ela ja estava desconectada, o logout devolve erro e nao ha o que tratar.
    ///
    /// 404 conta como SUCESSO: instancia que nao existe e exatamente o estado pedido. Sem isso,
    /// remover uma conexao que nunca foi pareada — o caso mais comum — falharia.</summary>
    public async Task RemoverInstanciaAsync(string instance, CancellationToken ct)
    {
        try { await http.DeleteAsync($"instance/logout/{instance}", ct); }
        catch (Exception ex) { log.LogWarning(ex, "Logout antes de apagar a instancia falhou."); }

        HttpResponseMessage resp;
        try { resp = await http.DeleteAsync($"instance/delete/{instance}", ct); }
        catch (Exception ex) { throw new IntegracaoWhatsAppException($"Evolution API inacessivel ({ex.Message}).", ex); }

        if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound) return;

        var corpo = await resp.Content.ReadAsStringAsync(ct);
        throw new IntegracaoWhatsAppException(
            $"Nao foi possivel apagar a instancia: {(int)resp.StatusCode} {corpo}");
    }

    private async Task CriarInstanciaAsync(string instance, CancellationToken ct)
    {
        // Baileys + qrcode: e o suficiente. O webhook e GLOBAL (docker-compose), nao precisa
        // ser configurado por instancia aqui.
        var resp = await http.PostAsJsonAsync("instance/create",
            new { instanceName = instance, integration = "WHATSAPP-BAILEYS", qrcode = true }, ct);

        // 403/409 = ja existe: ok, seguimos para o connect.
        if (!resp.IsSuccessStatusCode
            && resp.StatusCode is not (System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Conflict))
        {
            var corpo = await resp.Content.ReadAsStringAsync(ct);
            throw new IntegracaoWhatsAppException($"Nao foi possivel criar a instancia: {(int)resp.StatusCode} {corpo}");
        }
    }

    private static string? LerEstado(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            if (raiz.TryGetProperty("instance", out var inst) && inst.TryGetProperty("state", out var st))
                return st.GetString();
            if (raiz.TryGetProperty("state", out var st2)) return st2.GetString();
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Parse DEFENSIVO: a Evolution varia entre devolver o QR aninhado em "qrcode"
    /// ou nos campos de topo (base64/code/pairingCode). Cobrimos os dois.</summary>
    private static RespostaQr LerQr(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            var fonte = raiz.TryGetProperty("qrcode", out var q) && q.ValueKind == JsonValueKind.Object ? q : raiz;

            string? Campo(JsonElement el, string nome) =>
                el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var estado = LerEstado(json) ?? "connecting";
            return new RespostaQr(
                Campo(fonte, "base64"), Campo(fonte, "code"), Campo(fonte, "pairingCode"), estado);
        }
        catch (JsonException)
        {
            return new RespostaQr(null, null, null, "connecting");
        }
    }
}
