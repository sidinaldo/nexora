using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexora.Infra.Evolution;

/// <summary>===================== A EDICAO NAO E MENSAGEM NOVA =====================
///
/// Quando alguem edita uma mensagem no WhatsApp, o celular manda um `secretEncryptedMessage` com
/// `secretEncType = 2` (MESSAGE_EDIT) e a chave da mensagem ORIGINAL em `targetMessageKey`. O
/// texto novo vem cifrado em `encPayload`.
///
/// Encontrado com o contato (84) 9425-9023: "Falr" corrigido para "Fale". O celular mostrava
/// "Fale · Editada"; o painel mostrava "Falr" e, embaixo, um balao
/// "[mensagem nao suportada: secretEncryptedMessage]".
///
/// ===================== COMO O TEXTO NOVO E ABERTO =====================
/// A chave sai de um HKDF-SHA256 sobre o `messageSecret` da ORIGINAL (que esta no `payload_raw`
/// dela), com `info` = id da original + JID de quem a mandou + JID de quem editou + "Message
/// Edit". O conteudo e AES-GCM, e dentro esta um `Message` protobuf com
/// `protocolMessage.editedMessage`.
///
/// ⚠️ O JID QUE ABRE E O LID, NAO O TELEFONE. A Evolution 2.3.7 troca o LID pelo telefone em
/// `messages.upsert` antes de mandar o webhook, e a API dela devolve `"lid": "lid"`. Quem traz o
/// LID e a CONFIRMACAO DE ENTREGA (`messages.update`), que passa o `remoteJid` cru — e e de la
/// que ele vai para `contatos.lid`. Sem confirmacao ainda, nao ha LID, e a edicao fica so
/// marcada.
///
/// Errar o JID nao corrompe nada: o GCM recusa a chave errada, e o candidato seguinte e tentado.
/// ==============================================================================
///
/// Outros `secretEncType` (1 = edicao de evento) nao passam por aqui: continuam no rotulo de "nao
/// suportada", que e a rede de seguranca do REC-2.</summary>
public static class EdicaoMensagem
{
    /// <summary>O `messageType` da edicao. Quem chama confere ele antes de pedir o `Ler`, que
    /// abre o JSON inteiro.</summary>
    public const string Tipo = "secretEncryptedMessage";

    private const int MessageEdit = 2;

    /// <summary>O que se aproveita da edicao: o `wa_message_id` da original e o conteudo
    /// cifrado. `Iv`/`Cifrado` nulos so impedem de abrir — a edicao continua sendo marcada.</summary>
    public sealed record Edicao(string Alvo, byte[]? Iv, byte[]? Cifrado);

    /// <summary>A edicao, ou `null` se o payload nao e uma.
    ///
    /// NUNCA LANCA, pelo mesmo motivo do `ConteudoLegivel`: o webhook precisa responder 2xx.</summary>
    public static Edicao? Ler(string? payloadCru)
    {
        if (string.IsNullOrWhiteSpace(payloadCru)) return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadCru);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("message", out var msg)
                || msg.ValueKind != JsonValueKind.Object
                || !msg.TryGetProperty(Tipo, out var sec)
                || sec.ValueKind != JsonValueKind.Object)
                return null;

            if (!EhEdicao(sec)) return null;

            if (!sec.TryGetProperty("targetMessageKey", out var alvo)
                || alvo.ValueKind != JsonValueKind.Object
                || !alvo.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString()))
                return null;

            return new Edicao(id.GetString()!,
                sec.TryGetProperty("encIv", out var iv) ? Bytes(iv) : null,
                sec.TryGetProperty("encPayload", out var cif) ? Bytes(cif) : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>O `messageSecret` da ORIGINAL, lido do `payload_raw` dela. Nulo quando ela nao
    /// veio pelo webhook (saiu do painel) ou veio sem ele.</summary>
    public static byte[]? SegredoDa(string? payloadOriginal)
    {
        if (string.IsNullOrWhiteSpace(payloadOriginal)) return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadOriginal);
            return doc.RootElement.TryGetProperty("data", out var data)
                   && data.TryGetProperty("message", out var msg)
                   && msg.ValueKind == JsonValueKind.Object
                   && msg.TryGetProperty("messageContextInfo", out var ctx)
                   && ctx.ValueKind == JsonValueKind.Object
                   && ctx.TryGetProperty("messageSecret", out var segredo)
                ? Bytes(segredo)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>O texto novo, ou `null` se nenhum dos JIDs abre a edicao — ou se o que ela traz
    /// nao e texto (legenda de midia editada, por exemplo).
    ///
    /// `remetentes` sao os candidatos a autor: a mensagem so pode ser editada por quem a mandou,
    /// entao o mesmo JID entra como remetente da original e da edicao.</summary>
    public static string? Decifrar(Edicao edicao, byte[] segredo, IEnumerable<string?> remetentes)
    {
        if (edicao.Iv is not { Length: 12 } iv || edicao.Cifrado is not { Length: > 16 } cifrado
            || segredo.Length == 0)
            return null;

        var tamanho = cifrado.Length - 16;

        foreach (var jid in remetentes.Where(j => !string.IsNullOrWhiteSpace(j)).Distinct())
        {
            var info = Encoding.UTF8.GetBytes(edicao.Alvo + jid + jid + "Message Edit");
            var chave = HKDF.DeriveKey(HashAlgorithmName.SHA256, segredo, 32, salt: [], info: info);
            var aberto = new byte[tamanho];

            try
            {
                using var gcm = new AesGcm(chave, 16);
                gcm.Decrypt(iv, cifrado.AsSpan(0, tamanho), cifrado.AsSpan(tamanho), aberto);
            }
            catch (CryptographicException)
            {
                continue;   // a chave nao era desta pessoa: tenta o proximo candidato
            }

            return TextoEditado(aberto);
        }

        return null;
    }

    /// <summary>O LID sem o aparelho: a confirmacao chega como `181286291378345:9@lid` quando vem
    /// de um aparelho especifico, e a chave usa o JID da PESSOA. `null` se nao e LID.</summary>
    public static string? LidDe(string? remoteJid)
    {
        if (remoteJid is null || !remoteJid.EndsWith("@lid", StringComparison.Ordinal)) return null;

        var usuario = remoteJid[..^"@lid".Length].Split(':')[0];
        return usuario.Length > 0 && usuario.All(char.IsAsciiDigit) ? $"{usuario}@lid" : null;
    }

    /// <summary>O tipo chega como NUMERO no payload medido. O nome do enum fica aceito tambem: a
    /// conversao de protobuf para JSON da Evolution ja mudou de forma entre versoes.</summary>
    private static bool EhEdicao(JsonElement sec)
    {
        if (!sec.TryGetProperty("secretEncType", out var tipo)) return false;

        return tipo.ValueKind switch
        {
            JsonValueKind.Number => tipo.TryGetInt32(out var n) && n == MessageEdit,
            JsonValueKind.String => string.Equals(tipo.GetString(), "MESSAGE_EDIT", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>Bytes no JSON da Evolution: o `Uint8Array` vira objeto de chaves numeradas
    /// (`{"0":115,"1":147,...}`), que e a forma medida. Lista e base64 ficam aceitos.</summary>
    private static byte[]? Bytes(JsonElement no)
    {
        try
        {
            switch (no.ValueKind)
            {
                case JsonValueKind.Object:
                    var pares = no.EnumerateObject().ToList();
                    var saida = new byte[pares.Count];
                    foreach (var p in pares)
                    {
                        if (!int.TryParse(p.Name, out var i) || i < 0 || i >= saida.Length) return null;
                        saida[i] = p.Value.GetByte();
                    }
                    return saida;
                case JsonValueKind.Array:
                    return no.EnumerateArray().Select(v => v.GetByte()).ToArray();
                case JsonValueKind.String:
                    return Convert.FromBase64String(no.GetString()!);
                default:
                    return null;
            }
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    // ==================================================================== protobuf
    // Leitura MINIMA do `Message` aberto: so os campos do caminho do texto. Uma dependencia de
    // protobuf inteira para quatro numeros de campo nao se paga.

    /// <summary>`protocolMessage` (12) -> `editedMessage` (14) -> `conversation` (1) ou
    /// `extendedTextMessage` (6) -> `text` (1).</summary>
    private static string? TextoEditado(byte[] mensagem)
    {
        if (Campo(mensagem, 12) is not { } protocolo || Campo(protocolo, 14) is not { } editada)
            return null;

        var texto = Campo(editada, 1)
                    ?? (Campo(editada, 6) is { } estendida ? Campo(estendida, 1) : null);

        return texto is null ? null : Encoding.UTF8.GetString(texto);
    }

    /// <summary>O primeiro campo `numero` do tipo length-delimited, ou `null`. Os outros tipos
    /// sao pulados; byte malformado encerra a leitura sem lancar.</summary>
    private static byte[]? Campo(byte[] dados, int numero)
    {
        var i = 0;
        while (i < dados.Length)
        {
            if (Varint(dados, ref i) is not { } chave) return null;

            switch ((int)(chave & 7))
            {
                case 0:
                    if (Varint(dados, ref i) is null) return null;
                    break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                case 2:
                    if (Varint(dados, ref i) is not { } tamanho || tamanho > (ulong)(dados.Length - i))
                        return null;
                    if ((int)(chave >> 3) == numero) return dados[i..(i + (int)tamanho)];
                    i += (int)tamanho;
                    break;
                default:
                    return null;
            }
        }

        return null;
    }

    private static ulong? Varint(byte[] dados, ref int i)
    {
        ulong valor = 0;
        for (var deslocamento = 0; deslocamento < 64 && i < dados.Length; deslocamento += 7)
        {
            var b = dados[i++];
            valor |= (ulong)(b & 0x7F) << deslocamento;
            if ((b & 0x80) == 0) return valor;
        }

        return null;
    }
}
