using System.Text.Json;

namespace Nexora.Infra.Evolution;

/// <summary>===================== NENHUMA MENSAGEM CHEGA INVISÍVEL (REC-2) =====================
///
/// O modelo tipado (`ConteudoMensagem`) conhece seis formatos: texto simples, texto com contexto e
/// as quatro mídias. O WhatsApp manda MUITO mais que isso, e o que ele mandava de diferente caía
/// num buraco silencioso — linha gravada sem texto, sem mídia e sem erro, virando balão branco.
///
/// Foi assim que uma mensagem de template do contato (83) 95278-7173 desapareceu: ela tinha texto
/// legível e um botão, e nada disso chegou à tela.
///
/// ⚠️ ISTO LÊ O JSON CRU, e não o modelo tipado, DE PROPÓSITO. Mapear cada formato novo em classe
/// seria trabalho recorrente para sempre — e a cada formato esquecido o sintoma volta. Aqui o
/// desconhecido tem uma saída: vira rótulo com o nome do tipo, e alguém descobre pelo log.
///
/// A ordem importa: o primeiro que casar ganha. Formatos aninhados (template com imagem dentro)
/// são tratados antes dos genéricos.
/// ==============================================================================</summary>
public static class ConteudoLegivel
{
    /// <summary>O que grava quando não dá para ler nada. O tipo entra no texto porque é o que
    /// permite investigar sem abrir o `payload_raw` — e o que diz ao vendedor que houve mensagem,
    /// mesmo que ele precise do celular para vê-la.</summary>
    public static string Desconhecido(string tipo) => $"[mensagem não suportada: {tipo}]";

    /// <summary>Tipos que NÃO SÃO CONTEÚDO e não devem virar linha nenhuma.
    ///
    /// ⚠️ `reactionMessage` está aqui por causa do SEMÁFORO. `AtualizarConversaAsync` acende
    /// `aguardando_desde` e soma `nao_lidas` em toda entrada — uma reação virando linha faria um
    /// 👍 aparecer como "cliente esperando resposta". Alarme falso numa tela cuja utilidade
    /// inteira depende de o alerta significar alguma coisa. Um emoji não pede ação.
    ///
    /// Os outros são protocolo: revogação, edição e distribuição de chave. Nunca foram conteúdo.</summary>
    private static readonly HashSet<string> NaoSaoConteudo = new(StringComparer.OrdinalIgnoreCase)
    {
        "reactionMessage",
        "protocolMessage",
        "senderKeyDistributionMessage",
        "messageContextInfo"
    };

    public static bool EhRuido(string? tipo) => tipo is not null && NaoSaoConteudo.Contains(tipo);

    /// <summary>O texto legível do nó `data.message`, ou `null` se este não é um formato que a
    /// função conhece — aí quem chama decide entre o rótulo do desconhecido e deixar o modelo
    /// tipado responder.
    ///
    /// NUNCA LANÇA: o webhook precisa responder 2xx, e um payload torto não pode derrubar o
    /// recebimento da mensagem seguinte.</summary>
    public static string? Extrair(string? payloadCru)
    {
        if (string.IsNullOrWhiteSpace(payloadCru)) return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadCru);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty("message", out var msg)) return null;

            return DoNo(msg);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A leitura em si, sobre o nó `message` já aberto.</summary>
    private static string? DoNo(JsonElement msg)
    {
        // ---- template: o caso que originou o bloco ----
        // O conteúdo vive em `hydratedTemplate`, e o BOTÃO é parte da mensagem: sem ele o texto
        // termina em "clique para ver mais detalhes 👇🏻" e não há nada abaixo.
        if (msg.TryGetProperty("templateMessage", out var tpl))
        {
            // ⚠️ DUAS FORMAS PARA O MESMO `messageType`, e as duas apareceram no mesmo dia:
            // `hydratedTemplate` (a do sorteio) e `interactiveMessageTemplate` (a do Mercado
            // Pago), esta com a estrutura header/body/footer do `interactiveMessage`.
            //
            // Cobrir so a primeira deixava a segunda no rotulo de "nao suportada" — com o texto
            // inteiro a vista dentro do payload.
            if (tpl.TryGetProperty("interactiveMessageTemplate", out var interTpl))
                return DoInterativo(interTpl);

            var h = tpl.TryGetProperty("hydratedTemplate", out var ht) ? ht
                  : tpl.TryGetProperty("hydratedFourRowTemplate", out var h4) ? h4
                  : tpl;

            var partes = new List<string>();
            Junta(partes, Texto(h, "hydratedTitleText"));
            Junta(partes, Texto(h, "hydratedContentText"));
            Junta(partes, Texto(h, "hydratedFooterText"));

            if (h.TryGetProperty("hydratedButtons", out var botoes)
                && botoes.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in botoes.EnumerateArray()) Junta(partes, Botao(b));
            }

            return Fechar(partes);
        }

        // ---- botões e listas (formato antigo e o novo `interactive`) ----
        foreach (var (chave, campos) in new[]
        {
            ("buttonsMessage", new[] { "contentText", "text", "footerText" }),
            ("listMessage", new[] { "title", "description", "footerText" }),
            ("buttonsResponseMessage", new[] { "selectedDisplayText" }),
            ("listResponseMessage", new[] { "title", "description" }),
            ("templateButtonReplyMessage", new[] { "selectedDisplayText" })
        })
        {
            if (!msg.TryGetProperty(chave, out var no)) continue;

            var partes = new List<string>();
            foreach (var c in campos) Junta(partes, Texto(no, c));
            return Fechar(partes);
        }

        // O `interactiveMessage` aninha o corpo — não é um campo plano como os de cima.
        if (msg.TryGetProperty("interactiveMessage", out var inter)) return DoInterativo(inter);

        // ---- formatos SEM texto: viram rótulo do que são ----
        // Não dá para mostrar o conteúdo na thread, mas dá para dizer o que chegou — e é a
        // diferença entre "o cliente mandou algo" e um balão branco.
        if (msg.TryGetProperty("locationMessage", out var loc)
            || msg.TryGetProperty("liveLocationMessage", out loc))
        {
            var nome = Texto(loc, "name");
            // As coordenadas viram link: é o que torna a localização útil sem abrir o celular.
            var mapa = loc.TryGetProperty("degreesLatitude", out var la)
                    && loc.TryGetProperty("degreesLongitude", out var lo)
                    && la.ValueKind == JsonValueKind.Number && lo.ValueKind == JsonValueKind.Number
                ? $"https://www.google.com/maps?q={la.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)},{lo.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : null;

            return Fechar([$"📍 Localização{(nome is null ? "" : $": {nome}")}", mapa]);
        }

        if (msg.TryGetProperty("contactMessage", out var ct))
            return $"👤 Contato: {Texto(ct, "displayName") ?? "sem nome"}";

        if (msg.TryGetProperty("contactsArrayMessage", out var cts))
        {
            var quantos = cts.TryGetProperty("contacts", out var lista)
                       && lista.ValueKind == JsonValueKind.Array
                ? lista.GetArrayLength() : 0;
            return quantos > 1 ? $"👤 {quantos} contatos" : "👤 Contato";
        }

        if (msg.TryGetProperty("pollCreationMessage", out var enq)
            || msg.TryGetProperty("pollCreationMessageV3", out enq))
            return $"📊 Enquete: {Texto(enq, "name") ?? "sem pergunta"}";

        if (msg.TryGetProperty("eventMessage", out var evt))
            return $"📅 Evento: {Texto(evt, "name") ?? "sem título"}";

        return null;
    }

    /// <summary>A estrutura header/body/footer, que aparece em DOIS lugares: no
    /// `interactiveMessage` solto e dentro de `templateMessage.interactiveMessageTemplate`.
    ///
    /// Os botões do `nativeFlowMessage` ficam de fora: eles guardam os parâmetros como JSON
    /// DENTRO de uma string (`buttonParamsJson`), e nas mensagens vistas até aqui o link já vem
    /// no corpo. Registrado como pendência em docs/REC-2.md.</summary>
    private static string? DoInterativo(JsonElement no)
    {
        var partes = new List<string>();
        if (no.TryGetProperty("header", out var cab))
        {
            Junta(partes, Texto(cab, "title") ?? Texto(cab, "subtitle"));
        }
        if (no.TryGetProperty("body", out var corpo)) Junta(partes, Texto(corpo, "text"));
        if (no.TryGetProperty("footer", out var rod)) Junta(partes, Texto(rod, "text"));
        return Fechar(partes);
    }

    /// <summary>O rótulo do botão e para onde ele leva. O texto sem o destino não serve — a
    /// mensagem inteira existia para levar o cliente àquele link.</summary>
    private static string? Botao(JsonElement b)
    {
        foreach (var tipo in new[] { "urlButton", "callButton", "quickReplyButton" })
        {
            if (!b.TryGetProperty(tipo, out var no)) continue;

            var rotulo = Texto(no, "displayText") ?? Texto(no, "text");
            var destino = Texto(no, "url") ?? Texto(no, "phoneNumber");

            if (rotulo is null && destino is null) continue;
            return destino is null ? $"[{rotulo}]" : $"[{rotulo}] {destino}";
        }

        return null;
    }

    private static string? Texto(JsonElement no, string campo) =>
        no.ValueKind == JsonValueKind.Object
        && no.TryGetProperty(campo, out var v)
        && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static void Junta(List<string> partes, string? parte)
    {
        if (parte is not null) partes.Add(parte);
    }

    /// <summary>Linha em branco entre os pedaços: título, corpo, rodapé e botão são blocos, e
    /// colá-los produziria uma parede de texto que o vendedor não lê.</summary>
    private static string? Fechar(IEnumerable<string?> partes)
    {
        var texto = string.Join("\n\n", partes.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.IsNullOrWhiteSpace(texto) ? null : texto;
    }
}
