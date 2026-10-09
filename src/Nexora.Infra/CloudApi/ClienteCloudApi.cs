using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;

namespace Nexora.Infra.CloudApi;

/// <summary>O numero como a Meta o descreve. `Numero` ja vem so com digitos.</summary>
public record NumeroCloud(
    string Numero, string? NomeVerificado, string? Qualidade, string? StatusDoNome,
    string? StatusDaVerificacao);

/// <summary>Um template como a Meta o descreve: o id dela, o status (`PENDING`, `APPROVED`,
/// `REJECTED`…) e o motivo, quando recusou.</summary>
public record ModeloNaMeta(string Id, string Status, string? MotivoRejeicao);

/// <summary>A Graph API da Meta, na parte do WhatsApp (INT-XX). Interface para os testes
/// trocarem a rede por um duble.</summary>
public interface IClienteCloudApi
{
    /// <summary>Le o numero. LANCA `IntegracaoWhatsAppException` com a causa em portugues.</summary>
    Task<NumeroCloud> LerNumeroAsync(string phoneNumberId, string token, CancellationToken ct);

    /// <summary>O numero pertence a esta WABA? E o que impede cadastrar o numero de outra conta
    /// com um token que por acaso o enxerga.</summary>
    Task<bool> NumeroEstaNaWabaAsync(string wabaId, string phoneNumberId, string token, CancellationToken ct);

    /// <summary>Inscreve o app nos webhooks da WABA. Sem isso a Meta nao manda as mensagens
    /// recebidas, mesmo com a URL configurada. Idempotente.</summary>
    Task AssinarWebhooksAsync(string wabaId, string token, CancellationToken ct);

    /// <summary>`open` | `close` | `offline`, o mesmo vocabulario da Evolution — o verificador de
    /// 5 minutos, o banner e o freio de envio funcionam sem saber o canal. NUNCA LANCA.</summary>
    Task<string> EstadoAsync(string phoneNumberId, string token, CancellationToken ct);

    /// <summary>Texto livre. Devolve o id da mensagem na Meta (`wamid...`). So vale com a janela
    /// de 24h aberta — fora dela a Meta recusa (131047).</summary>
    Task<string> EnviarTextoAsync(
        string phoneNumberId, string token, string para, string texto, CancellationToken ct);

    /// <summary>Anexo: sobe o arquivo para a Meta e envia pelo id. `tipo` e `image`, `document`,
    /// `audio` ou `video`. Devolve o id da mensagem.</summary>
    Task<string> EnviarMidiaAsync(
        string phoneNumberId, string token, string para, byte[] conteudo, string mime, string tipo,
        string? nomeArquivo, string? legenda, CancellationToken ct);

    /// <summary>Baixa um anexo RECEBIDO pelo id que veio no webhook. LANCA
    /// `IntegracaoWhatsAppException` com a causa, que vai para `mensagens.erro`.</summary>
    Task<MidiaRecebida> BaixarMidiaAsync(string mediaId, string token, CancellationToken ct);

    /// <summary>Manda um template para a revisao da Meta. `corpo` JA NUMERADO (`{{1}}`), com um
    /// exemplo por variavel. Devolve o id e o status — `utility` as vezes sai aprovado na hora.</summary>
    Task<ModeloNaMeta> CriarModeloAsync(
        string wabaId, string token, string nome, string categoria, string idioma, string corpo,
        IReadOnlyList<string> exemplos, CancellationToken ct);

    /// <summary>Como esta a revisao de um template, agora.</summary>
    Task<ModeloNaMeta> LerModeloAsync(string idMeta, string token, CancellationToken ct);

    /// <summary>Envia um template aprovado, com os valores das variaveis na ordem. Devolve o wamid.</summary>
    Task<string> EnviarModeloAsync(
        string phoneNumberId, string token, string para, string nome, string idioma,
        IReadOnlyList<string> parametros, CancellationToken ct);
}

/// <summary>===================== A GRAPH API, PELA CONEXAO OFICIAL (INT-XX) =====================
///
/// ⚠️ O TOKEN VAI NO CABECALHO `Authorization: Bearer`, NUNCA NA QUERY STRING. Query string aparece
/// em log de proxy, de balanceador e de erro — o mesmo cuidado do `ClienteMeta` das conversoes.
///
/// ⚠️ A VERSAO E FIXA (`Versao`). A Meta aposenta cada versao uns dois anos depois de lancar; trocar e
/// uma decisao, nao um efeito colateral de atualizar pacote.
///
/// Os ids entram no caminho ESCAPADOS: vem do formulario do cliente, e um `/` ali mudaria o recurso.
/// ===========================================================================================</summary>
public class ClienteCloudApi(HttpClient http, ILogger<ClienteCloudApi> log) : IClienteCloudApi
{
    /// <summary>Graph API v25.0, de fevereiro de 2026.</summary>
    public const string Versao = "v25.0";

    public async Task<NumeroCloud> LerNumeroAsync(string phoneNumberId, string token, CancellationToken ct)
    {
        var caminho = $"{Versao}/{Uri.EscapeDataString(phoneNumberId)}"
                    + "?fields=display_phone_number,verified_name,quality_rating,name_status,code_verification_status";

        using var resposta = await EnviarAsync(HttpMethod.Get, caminho, token, ct);
        var corpo = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDaMeta(corpo));

        using var doc = JsonDocument.Parse(corpo);
        var raiz = doc.RootElement;
        var exibido = Texto(raiz, "display_phone_number") ?? "";

        return new NumeroCloud(
            new string(exibido.Where(char.IsAsciiDigit).ToArray()),
            Texto(raiz, "verified_name"),
            Texto(raiz, "quality_rating"),
            Texto(raiz, "name_status"),
            Texto(raiz, "code_verification_status"));
    }

    public async Task<bool> NumeroEstaNaWabaAsync(
        string wabaId, string phoneNumberId, string token, CancellationToken ct)
    {
        var caminho = $"{Versao}/{Uri.EscapeDataString(wabaId)}/phone_numbers?fields=id&limit=100";

        using var resposta = await EnviarAsync(HttpMethod.Get, caminho, token, ct);
        var corpo = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDaMeta(corpo));

        using var doc = JsonDocument.Parse(corpo);
        if (!doc.RootElement.TryGetProperty("data", out var dados) || dados.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in dados.EnumerateArray())
        {
            if (Texto(item, "id") == phoneNumberId) return true;
        }
        return false;
    }

    public async Task AssinarWebhooksAsync(string wabaId, string token, CancellationToken ct)
    {
        var caminho = $"{Versao}/{Uri.EscapeDataString(wabaId)}/subscribed_apps";

        using var resposta = await EnviarAsync(HttpMethod.Post, caminho, token, ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDaMeta(await resposta.Content.ReadAsStringAsync(ct)));
    }

    public async Task<string> EstadoAsync(string phoneNumberId, string token, CancellationToken ct)
    {
        try
        {
            var caminho = $"{Versao}/{Uri.EscapeDataString(phoneNumberId)}?fields=id";
            using var resposta = await EnviarAsync(HttpMethod.Get, caminho, token, ct);

            if (resposta.IsSuccessStatusCode) return "open";

            // 5xx e limite de taxa sao problema da Meta, nao do numero: `offline`, como a Evolution
            // fora do ar. O resto (token vencido, numero removido) e o numero que caiu.
            var codigo = (int)resposta.StatusCode;
            if (codigo >= 500 || resposta.StatusCode == HttpStatusCode.TooManyRequests) return "offline";
            return "close";
        }
        catch (IntegracaoWhatsAppException)
        {
            return "offline";
        }
    }

    // ==================================================================== envio
    public async Task<string> EnviarTextoAsync(
        string phoneNumberId, string token, string para, string texto, CancellationToken ct)
    {
        // `preview_url` desligado: o vendedor nao escolheu mostrar a pre-visualizacao do link, e
        // ela muda o tamanho do balao no celular do cliente.
        var corpo = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = para,
            type = "text",
            text = new { body = texto, preview_url = false }
        };
        return await PostarMensagemAsync(phoneNumberId, token, corpo, ct);
    }

    public async Task<string> EnviarMidiaAsync(
        string phoneNumberId, string token, string para, byte[] conteudo, string mime, string tipo,
        string? nomeArquivo, string? legenda, CancellationToken ct)
    {
        var mediaId = await SubirMidiaAsync(phoneNumberId, token, conteudo, mime, nomeArquivo, ct);

        // A Meta nao aceita legenda em audio, e o nome do arquivo so tem efeito em documento.
        var anexo = new Dictionary<string, object> { ["id"] = mediaId };
        if (tipo != "audio" && !string.IsNullOrWhiteSpace(legenda)) anexo["caption"] = legenda;
        if (tipo == "document" && !string.IsNullOrWhiteSpace(nomeArquivo)) anexo["filename"] = nomeArquivo;

        var corpo = new Dictionary<string, object>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = para,
            ["type"] = tipo,
            [tipo] = anexo
        };
        return await PostarMensagemAsync(phoneNumberId, token, corpo, ct);
    }

    /// <summary>O arquivo vai PRIMEIRO para a Meta, que devolve um id; a mensagem cita o id. Por
    /// link exigiria o arquivo publico na internet — e o anexo de um cliente nao e publico.</summary>
    private async Task<string> SubirMidiaAsync(
        string phoneNumberId, string token, byte[] conteudo, string mime, string? nomeArquivo,
        CancellationToken ct)
    {
        using var formulario = new MultipartFormDataContent();
        formulario.Add(new StringContent("whatsapp"), "messaging_product");
        formulario.Add(new StringContent(mime), "type");
        var arquivo = new ByteArrayContent(conteudo);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue(mime);
        formulario.Add(arquivo, "file", string.IsNullOrWhiteSpace(nomeArquivo) ? "arquivo" : nomeArquivo);

        var caminho = $"{Versao}/{Uri.EscapeDataString(phoneNumberId)}/media";
        using var resposta = await EnviarAsync(HttpMethod.Post, caminho, token, ct, formulario);
        var texto = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDaMeta(texto));

        using var doc = JsonDocument.Parse(texto);
        var id = Texto(doc.RootElement, "id");
        if (id == null) throw new IntegracaoWhatsAppException("A Meta aceitou o arquivo mas não devolveu o id dele.");
        return id;
    }

    private async Task<string> PostarMensagemAsync(
        string phoneNumberId, string token, object corpo, CancellationToken ct)
    {
        var caminho = $"{Versao}/{Uri.EscapeDataString(phoneNumberId)}/messages";
        using var resposta = await EnviarAsync(HttpMethod.Post, caminho, token, ct, JsonContent.Create(corpo));
        var texto = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDaMeta(texto));

        // `messages[0].id` e o wamid: e por ele que os status (entregue, lido) voltam pelo webhook.
        using var doc = JsonDocument.Parse(texto);
        if (doc.RootElement.TryGetProperty("messages", out var mensagens)
            && mensagens.ValueKind == JsonValueKind.Array
            && mensagens.GetArrayLength() > 0)
        {
            var id = Texto(mensagens[0], "id");
            if (id != null) return id;
        }
        throw new IntegracaoWhatsAppException("A Meta aceitou a mensagem mas não devolveu o id dela.");
    }

    // ==================================================================== templates
    public async Task<ModeloNaMeta> CriarModeloAsync(
        string wabaId, string token, string nome, string categoria, string idioma, string corpo,
        IReadOnlyList<string> exemplos, CancellationToken ct)
    {
        // A Meta exige um exemplo por variavel na revisao; sem variavel, o `example` nao vai.
        var componente = new Dictionary<string, object> { ["type"] = "BODY", ["text"] = corpo };
        if (exemplos.Count > 0)
            componente["example"] = new { body_text = new[] { exemplos } };

        var pedido = new
        {
            name = nome,
            language = idioma,
            category = categoria.ToUpperInvariant(),
            components = new[] { componente }
        };

        var caminho = $"{Versao}/{Uri.EscapeDataString(wabaId)}/message_templates";
        using var resposta = await EnviarAsync(HttpMethod.Post, caminho, token, ct, JsonContent.Create(pedido));
        var texto = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDoModelo(texto));

        using var doc = JsonDocument.Parse(texto);
        var id = Texto(doc.RootElement, "id")
            ?? throw new IntegracaoWhatsAppException("A Meta aceitou o template mas não devolveu o id dele.");
        return new ModeloNaMeta(id, Texto(doc.RootElement, "status") ?? "PENDING", null);
    }

    public async Task<ModeloNaMeta> LerModeloAsync(string idMeta, string token, CancellationToken ct)
    {
        var caminho = $"{Versao}/{Uri.EscapeDataString(idMeta)}?fields=id,status,rejected_reason";
        using var resposta = await EnviarAsync(HttpMethod.Get, caminho, token, ct);
        var texto = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDoModelo(texto));

        using var doc = JsonDocument.Parse(texto);
        return new ModeloNaMeta(
            Texto(doc.RootElement, "id") ?? idMeta,
            Texto(doc.RootElement, "status") ?? "PENDING",
            Texto(doc.RootElement, "rejected_reason"));
    }

    public async Task<string> EnviarModeloAsync(
        string phoneNumberId, string token, string para, string nome, string idioma,
        IReadOnlyList<string> parametros, CancellationToken ct)
    {
        var modelo = new Dictionary<string, object>
        {
            ["name"] = nome,
            ["language"] = new { code = idioma }
        };
        if (parametros.Count > 0)
        {
            modelo["components"] = new[]
            {
                new
                {
                    type = "body",
                    parameters = parametros.Select(p => new { type = "text", text = p }).ToArray()
                }
            };
        }

        var corpo = new Dictionary<string, object>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = para,
            ["type"] = "template",
            ["template"] = modelo
        };
        return await PostarMensagemAsync(phoneNumberId, token, corpo, ct);
    }

    // ==================================================================== midia recebida
    public async Task<MidiaRecebida> BaixarMidiaAsync(string mediaId, string token, CancellationToken ct)
    {
        // 1. O id vira uma URL temporaria (expira em minutos), com o tipo e o tamanho.
        using var resposta = await EnviarAsync(HttpMethod.Get, $"{Versao}/{Uri.EscapeDataString(mediaId)}", token, ct);
        var texto = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException(ErroDaMeta(texto));

        string? url;
        string? mime;
        long tamanho = 0;
        using (var doc = JsonDocument.Parse(texto))
        {
            url = Texto(doc.RootElement, "url");
            mime = Texto(doc.RootElement, "mime_type");
            if (doc.RootElement.TryGetProperty("file_size", out var t) && t.ValueKind == JsonValueKind.Number)
                tamanho = t.GetInt64();
        }

        // Antes de baixar: um video de 100 MB nao entra, e baixa-lo para recusar depois so gasta banda.
        if (tamanho > ValidadorMidia.TamanhoMaximoBytes)
            throw new IntegracaoWhatsAppException(
                $"O anexo tem {tamanho / (1024 * 1024)} MB, acima do limite de "
              + $"{ValidadorMidia.TamanhoMaximoBytes / (1024 * 1024)} MB.");

        // ⚠️ O TOKEN SO VAI PARA A META. A URL vem na resposta; se um dia ela apontasse para fora,
        // o `Authorization` iria junto para um terceiro.
        if (url == null || !EhDaMeta(url))
            throw new IntegracaoWhatsAppException("A Meta não devolveu um endereço válido para o anexo.");

        // 2. A URL tambem exige o token.
        using var arquivo = await EnviarAsync(HttpMethod.Get, url, token, ct);
        if (!arquivo.IsSuccessStatusCode)
            throw new IntegracaoWhatsAppException($"A Meta não entregou o anexo (HTTP {(int)arquivo.StatusCode}).");

        var bytes = await arquivo.Content.ReadAsByteArrayAsync(ct);
        return new MidiaRecebida(Convert.ToBase64String(bytes), mime, null);
    }

    private static bool EhDaMeta(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;
        return host.EndsWith(".fbsbx.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".whatsapp.net", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".fbcdn.net", StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================== apoio
    private async Task<HttpResponseMessage> EnviarAsync(
        HttpMethod metodo, string caminho, string token, CancellationToken ct,
        HttpContent? conteudo = null)
    {
        using var pedido = new HttpRequestMessage(metodo, caminho);
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        pedido.Content = conteudo;

        try
        {
            return await http.SendAsync(pedido, ct);
        }
        catch (HttpRequestException ex)
        {
            throw SemResposta(ex, metodo, caminho);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout do HttpClient. Cancelamento de quem chamou continua subindo como esta.
            throw SemResposta(ex, metodo, caminho);
        }
    }

    private IntegracaoWhatsAppException SemResposta(Exception ex, HttpMethod metodo, string caminho)
    {
        log.LogWarning(ex, "A Graph API da Meta nao respondeu ({Metodo} {Recurso}).",
            metodo, caminho.Split('?')[0]);
        return new IntegracaoWhatsAppException("A Meta não respondeu. Tente de novo em alguns minutos.", ex);
    }

    /// <summary>O erro da Graph API em portugues. O `message` dela vai junto quando o codigo nao e
    /// um dos conhecidos — e o que permite investigar sem abrir log.</summary>
    internal static string ErroDaMeta(string corpo)
    {
        int? codigo = null;
        string? mensagem = null;

        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.TryGetProperty("error", out var erro) && erro.ValueKind == JsonValueKind.Object)
            {
                if (erro.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
                    codigo = c.GetInt32();
                mensagem = Texto(erro, "message");
            }
        }
        catch (JsonException)
        {
            // Corpo que nao e JSON: cai na mensagem generica.
        }

        if (codigo == 190)
            return "A Meta recusou o token: ele expirou ou não vale para esta conta.";
        if (codigo == 100)
            return "A Meta não encontrou este número com este token. Confira o Phone Number ID e o WABA ID.";
        if (codigo == 10 || codigo == 200)
            return "O token não tem permissão de WhatsApp. Ele precisa de whatsapp_business_messaging e whatsapp_business_management.";
        if (codigo == 131047)
            return "A janela de 24h do WhatsApp fechou: pela API oficial, só template aprovado pode ser enviado.";
        if (codigo == 131026)
            return "A Meta não conseguiu entregar: o número pode não ter WhatsApp ou não aceitar mensagens comerciais.";
        if (codigo == 131056)
            return "Muitas mensagens seguidas para o mesmo cliente. Espere um pouco e tente de novo.";
        if (codigo == 130429 || codigo == 80007)
            return "A Meta limitou o envio deste número agora. Tente de novo em alguns minutos.";
        if (codigo == 131048)
            return "A Meta limitou este número por denúncias de spam. Confira a qualidade dele no painel da Meta.";
        if (codigo == 133010)
            return "Este número não está registrado na API oficial. Conclua o registro no painel da Meta.";
        if (codigo == 132000)
            return "O número de variáveis não bate com o template aprovado na Meta.";
        if (codigo == 132001)
            return "A Meta não achou este template aprovado neste idioma. Confira o status dele em Conexão.";
        if (codigo == 132015)
            return "A Meta pausou este template por baixa qualidade. Ele não pode ser enviado agora.";
        if (codigo == 132016)
            return "A Meta desativou este template por baixa qualidade. Crie outro.";
        if (mensagem != null)
            return "A Meta recusou: " + mensagem;
        return "A Meta recusou o pedido.";
    }

    /// <summary>O erro da revisao de template. O 100 aqui e a Meta recusando o PEDIDO (nome repetido,
    /// texto fora da regra) — e nao "numero nao encontrado", que e o que ele quer dizer no resto.</summary>
    internal static string ErroDoModelo(string corpo)
    {
        int? codigo = null;
        string? mensagem = null;
        string? detalhe = null;

        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.TryGetProperty("error", out var erro) && erro.ValueKind == JsonValueKind.Object)
            {
                if (erro.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
                    codigo = c.GetInt32();
                mensagem = Texto(erro, "message");
                detalhe = Texto(erro, "error_user_msg");
            }
        }
        catch (JsonException)
        {
            // Corpo que nao e JSON: cai no erro generico.
        }

        if (codigo == 100)
            return "A Meta recusou o template: " + (detalhe ?? mensagem ?? "pedido inválido.");
        return ErroDaMeta(corpo);
    }

    private static string? Texto(JsonElement no, string campo)
    {
        if (no.ValueKind != JsonValueKind.Object) return null;
        if (!no.TryGetProperty(campo, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return v.GetString();
    }
}
