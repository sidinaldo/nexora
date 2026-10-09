using System.Globalization;
using System.Text.Json;

namespace Nexora.Infra.CloudApi;

/// <summary>Uma mudanca da entrega (`entry[].changes[]`): o campo, o numero ou a conta de onde
/// veio, e o JSON inteiro dela para a fila.</summary>
public sealed record MudancaCloud(string Campo, string? PhoneNumberId, string? WabaId, string Json);

/// <summary>Uma mensagem recebida, ja com o texto legivel. `MidiaId` preenchido = tem anexo, que
/// e baixado da Meta pelo id. `Json` e a mensagem crua — e nela que mora o `referral` do anuncio.</summary>
public sealed record MensagemCloud(
    string Id, string De, DateTime Quando, string Tipo, string? Texto,
    string? MidiaId, string? NomeArquivo, string? CitadaId, string Json);

/// <summary>Um status de mensagem NOSSA: `sent`, `delivered`, `read` ou `failed`.</summary>
public sealed record StatusCloud(string Id, string Status, string? Erro);

/// <summary>O que uma mudanca `messages` traz: mensagens, status, e o nome de perfil por `wa_id`.</summary>
public sealed record EventoCloud(
    IReadOnlyList<MensagemCloud> Mensagens,
    IReadOnlyList<StatusCloud> Status,
    IReadOnlyDictionary<string, string> Nomes);

/// <summary>A Meta decidiu sobre um template (`message_template_status_update`): o id dela, o
/// evento (`APPROVED`, `REJECTED`, `PAUSED`…) e o motivo.</summary>
public sealed record EventoModeloCloud(string IdMeta, string Evento, string? Motivo);

/// <summary>===================== O JSON DA CLOUD API, TRADUZIDO (INT-XX) =====================
///
/// Funcao pura: nada de banco, nada de rede. E aqui que mora o conhecimento do formato da Meta — o
/// resto do sistema recebe `MensagemEntrante`, igual a da Evolution.
///
/// ⚠️ OS NOMES DOS CAMPOS VEM DA DOCUMENTACAO DA META, nao de mensagem real: ate o primeiro numero
/// oficial em producao, nunca houve uma neste banco. O formato desconhecido nao some — vira o
/// rotulo de "nao suportada" com o tipo, e e por ele que se descobre o que falta (o mesmo que o
/// REC-2 fez com a Evolution).
///
/// NUNCA LANCA: entrega torta vira lista vazia, e o webhook responde 200 mesmo assim.
/// ==============================================================================</summary>
public static class LeitorEventoCloudApi
{
    /// <summary>As mudancas da entrega, sem confiar nela ainda: a assinatura e conferida depois,
    /// por numero.</summary>
    public static IReadOnlyList<MudancaCloud> Mudancas(byte[] corpo)
    {
        var saida = new List<MudancaCloud>();
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var raiz = doc.RootElement;
            if (Texto(raiz, "object") != "whatsapp_business_account") return saida;
            if (!raiz.TryGetProperty("entry", out var entradas) || entradas.ValueKind != JsonValueKind.Array)
                return saida;

            foreach (var entrada in entradas.EnumerateArray())
            {
                var wabaId = Texto(entrada, "id");
                if (!entrada.TryGetProperty("changes", out var mudancas) || mudancas.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var mudanca in mudancas.EnumerateArray())
                {
                    var campo = Texto(mudanca, "field");
                    if (campo == null) continue;

                    string? phoneNumberId = null;
                    if (mudanca.TryGetProperty("value", out var valor)
                        && valor.ValueKind == JsonValueKind.Object
                        && valor.TryGetProperty("metadata", out var metadata))
                        phoneNumberId = Texto(metadata, "phone_number_id");

                    saida.Add(new MudancaCloud(campo, phoneNumberId, wabaId, mudanca.GetRawText()));
                }
            }
        }
        catch (JsonException)
        {
            saida.Clear();
        }
        return saida;
    }

    /// <summary>Uma mudanca `messages`, aberta.</summary>
    public static EventoCloud Ler(string mudancaJson)
    {
        var mensagens = new List<MensagemCloud>();
        var status = new List<StatusCloud>();
        var nomes = new Dictionary<string, string>();

        try
        {
            using var doc = JsonDocument.Parse(mudancaJson);
            if (!doc.RootElement.TryGetProperty("value", out var valor) || valor.ValueKind != JsonValueKind.Object)
                return new EventoCloud(mensagens, status, nomes);

            if (valor.TryGetProperty("contacts", out var contatos) && contatos.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in contatos.EnumerateArray())
                {
                    var waId = Texto(c, "wa_id");
                    string? nome = null;
                    if (c.TryGetProperty("profile", out var perfil)) nome = Texto(perfil, "name");
                    if (waId != null && !string.IsNullOrWhiteSpace(nome)) nomes[waId] = nome!;
                }
            }

            if (valor.TryGetProperty("messages", out var lista) && lista.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in lista.EnumerateArray())
                {
                    var lida = Mensagem(m);
                    if (lida != null) mensagens.Add(lida);
                }
            }

            if (valor.TryGetProperty("statuses", out var estados) && estados.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in estados.EnumerateArray())
                {
                    var id = Texto(s, "id");
                    var qual = Texto(s, "status");
                    if (id == null || qual == null) continue;
                    status.Add(new StatusCloud(id, qual, ErroDoStatus(s)));
                }
            }
        }
        catch (JsonException)
        {
            // Mudanca torta: devolve o que leu ate aqui.
        }

        return new EventoCloud(mensagens, status, nomes);
    }

    /// <summary>Uma mudanca `message_template_status_update`, aberta. Nulo quando falta o id ou o
    /// evento. O id vem como NUMERO no JSON da Meta — e aqui vira texto, como e guardado.</summary>
    public static EventoModeloCloud? LerEventoDeModelo(string mudancaJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(mudancaJson);
            if (!doc.RootElement.TryGetProperty("value", out var valor) || valor.ValueKind != JsonValueKind.Object)
                return null;

            string? id = null;
            if (valor.TryGetProperty("message_template_id", out var idMeta))
            {
                if (idMeta.ValueKind == JsonValueKind.Number) id = idMeta.GetRawText();
                else if (idMeta.ValueKind == JsonValueKind.String) id = idMeta.GetString();
            }

            var evento = Texto(valor, "event");
            if (id == null || evento == null) return null;
            return new EventoModeloCloud(id, evento, Texto(valor, "reason"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MensagemCloud? Mensagem(JsonElement m)
    {
        var id = Texto(m, "id");
        var de = Texto(m, "from");
        var tipo = Texto(m, "type");
        if (id == null || de == null || tipo == null) return null;

        var quando = DateTime.UnixEpoch;
        var ts = Texto(m, "timestamp");
        if (ts != null && long.TryParse(ts, NumberStyles.Integer, CultureInfo.InvariantCulture, out var segundos))
            quando = DateTimeOffset.FromUnixTimeSeconds(segundos).UtcDateTime;

        string? citada = null;
        if (m.TryGetProperty("context", out var contexto)) citada = Texto(contexto, "id");

        string? texto = null;
        string? midiaId = null;
        string? nomeArquivo = null;

        if (tipo == "text")
        {
            if (m.TryGetProperty("text", out var t)) texto = Texto(t, "body");
        }
        else if (tipo == "image" || tipo == "video" || tipo == "document" || tipo == "audio" || tipo == "sticker")
        {
            if (m.TryGetProperty(tipo, out var anexo))
            {
                midiaId = Texto(anexo, "id");
                texto = Texto(anexo, "caption");
                nomeArquivo = Texto(anexo, "filename");
            }
        }
        else if (tipo == "location")
        {
            if (m.TryGetProperty("location", out var local)) texto = Localizacao(local);
        }
        else if (tipo == "contacts")
        {
            if (m.TryGetProperty("contacts", out var cartoes)) texto = Contatos(cartoes);
        }
        else if (tipo == "interactive")
        {
            if (m.TryGetProperty("interactive", out var interativo)) texto = RespostaInterativa(interativo);
        }
        else if (tipo == "button")
        {
            // A resposta a um botao de TEMPLATE nosso: o texto do botao e o que o cliente "disse".
            if (m.TryGetProperty("button", out var botao)) texto = Texto(botao, "text");
        }

        return new MensagemCloud(id, de, quando, tipo, Limpo(texto), midiaId, nomeArquivo, citada, m.GetRawText());
    }

    /// <summary>O mesmo desenho do `ConteudoLegivel` da Evolution: o nome do lugar e um link do mapa,
    /// que e o que torna a localizacao util sem abrir o celular.</summary>
    private static string? Localizacao(JsonElement local)
    {
        var nome = Texto(local, "name") ?? Texto(local, "address");
        string? mapa = null;
        if (local.TryGetProperty("latitude", out var la) && la.ValueKind == JsonValueKind.Number
            && local.TryGetProperty("longitude", out var lo) && lo.ValueKind == JsonValueKind.Number)
        {
            mapa = "https://www.google.com/maps?q="
                 + la.GetDouble().ToString(CultureInfo.InvariantCulture) + ","
                 + lo.GetDouble().ToString(CultureInfo.InvariantCulture);
        }

        var titulo = nome == null ? "📍 Localização" : "📍 Localização: " + nome;
        if (mapa == null) return titulo;
        return titulo + "\n\n" + mapa;
    }

    private static string? Contatos(JsonElement cartoes)
    {
        if (cartoes.ValueKind != JsonValueKind.Array || cartoes.GetArrayLength() == 0) return "👤 Contato";
        if (cartoes.GetArrayLength() > 1) return "👤 " + cartoes.GetArrayLength() + " contatos";

        string? nome = null;
        if (cartoes[0].TryGetProperty("name", out var n)) nome = Texto(n, "formatted_name");
        return nome == null ? "👤 Contato" : "👤 Contato: " + nome;
    }

    private static string? RespostaInterativa(JsonElement interativo)
    {
        if (interativo.TryGetProperty("button_reply", out var botao)) return Texto(botao, "title");
        if (interativo.TryGetProperty("list_reply", out var item))
        {
            var titulo = Texto(item, "title");
            var descricao = Texto(item, "description");
            if (titulo != null && descricao != null) return titulo + "\n\n" + descricao;
            return titulo ?? descricao;
        }
        return null;
    }

    /// <summary>O motivo de um `failed`, como a Meta o descreve: o codigo e o titulo.</summary>
    private static string? ErroDoStatus(JsonElement s)
    {
        if (!s.TryGetProperty("errors", out var erros) || erros.ValueKind != JsonValueKind.Array
            || erros.GetArrayLength() == 0)
            return null;

        var erro = erros[0];
        string? codigo = null;
        if (erro.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
            codigo = c.GetInt32().ToString(CultureInfo.InvariantCulture);
        var titulo = Texto(erro, "title") ?? Texto(erro, "message");

        if (codigo != null && titulo != null) return codigo + " " + titulo;
        return titulo ?? codigo;
    }

    private static string? Limpo(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        return texto.Trim();
    }

    private static string? Texto(JsonElement no, string campo)
    {
        if (no.ValueKind != JsonValueKind.Object) return null;
        if (!no.TryGetProperty(campo, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return v.GetString();
    }
}
