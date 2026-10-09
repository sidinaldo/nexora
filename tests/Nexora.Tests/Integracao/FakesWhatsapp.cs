using System.Security.Cryptography;
using System.Text;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.CloudApi;

namespace Nexora.Tests.Integracao;

/// <summary>A Graph API da Meta, sem rede (INT-XX). Por padrao responde que o numero existe e esta
/// na WABA; cada teste muda so o que precisa.</summary>
public sealed class ClienteCloudApiFalso : IClienteCloudApi
{
    public NumeroCloud Numero { get; set; } = new("5584912345678", "Loja Teste", "GREEN", "APPROVED", "VERIFIED");
    public bool NaWaba { get; set; } = true;
    public string Estado { get; set; } = "open";

    /// <summary>Preenchida, a leitura do numero falha com esta mensagem — o "a Meta recusou".</summary>
    public string? Recusa { get; set; }

    public List<string> TokensUsados { get; } = [];
    public List<string> WabasAssinadas { get; } = [];

    public Task<NumeroCloud> LerNumeroAsync(string phoneNumberId, string token, CancellationToken ct)
    {
        TokensUsados.Add(token);
        if (Recusa != null) throw new IntegracaoWhatsAppException(Recusa);
        return Task.FromResult(Numero);
    }

    public Task<bool> NumeroEstaNaWabaAsync(string wabaId, string phoneNumberId, string token, CancellationToken ct)
    {
        TokensUsados.Add(token);
        return Task.FromResult(NaWaba);
    }

    public Task AssinarWebhooksAsync(string wabaId, string token, CancellationToken ct)
    {
        WabasAssinadas.Add(wabaId);
        return Task.CompletedTask;
    }

    public Task<string> EstadoAsync(string phoneNumberId, string token, CancellationToken ct)
    {
        TokensUsados.Add(token);
        return Task.FromResult(Estado);
    }

    /// <summary>O que saiu, na ordem: para quem, de que tipo, e o texto ou a legenda.</summary>
    public List<(string Para, string Tipo, string? Texto)> Enviadas { get; } = [];

    public Task<string> EnviarTextoAsync(
        string phoneNumberId, string token, string para, string texto, CancellationToken ct)
    {
        Enviadas.Add((para, "text", texto));
        return Task.FromResult($"wamid.TESTE{Enviadas.Count}");
    }

    public Task<string> EnviarMidiaAsync(
        string phoneNumberId, string token, string para, byte[] conteudo, string mime, string tipo,
        string? nomeArquivo, string? legenda, CancellationToken ct)
    {
        Enviadas.Add((para, tipo, legenda));
        return Task.FromResult($"wamid.TESTE{Enviadas.Count}");
    }

    /// <summary>O anexo que a "Meta" devolve ao baixar; nulo = a Meta falha.</summary>
    public MidiaRecebida? MidiaParaDevolver { get; set; }
    public List<string> MidiasBaixadas { get; } = [];

    public Task<MidiaRecebida> BaixarMidiaAsync(string mediaId, string token, CancellationToken ct)
    {
        MidiasBaixadas.Add(mediaId);
        TokensUsados.Add(token);
        if (MidiaParaDevolver == null) throw new IntegracaoWhatsAppException("A Meta não entregou o anexo.");
        return Task.FromResult(MidiaParaDevolver);
    }
}

/// <summary>Uma `CifraSegredos` com chave sorteada — a de producao vem da configuracao.</summary>
public static class CifraDeTeste
{
    public static CifraSegredos Nova() =>
        new(new OpcoesCifra { ChaveCifra = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
}

/// <summary>Cliente de WhatsApp falso. O teste do webhook nao pode falar com a Evolution de
/// verdade — o que se prova aqui e o que o PROCESSADOR faz com o payload, nao o HTTP.</summary>
public sealed class ClienteWhatsAppFalso : IClienteWhatsApp
{
    public MidiaRecebida? MidiaParaDevolver { get; set; }
    public DetalhesInstancia? DetalhesParaDevolver { get; set; }
    public string EstadoParaDevolver { get; set; } = "open";

    /// <summary>Estado por INSTÂNCIA, quando o teste precisa de uma caída e outra no ar.
    ///
    /// Existe por causa do multi-número: com uma conexão só, `EstadoParaDevolver` bastava e o
    /// teste não tinha como distinguir "a empresa está fora" de "este número está fora" — que é
    /// exatamente a diferença que o motor passou a fazer.</summary>
    public Dictionary<string, string> EstadoPorInstancia { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instâncias cuja consulta de estado EXPLODE — o dado ruim de um número só, para
    /// provar que ele não segura a conferência dos outros.</summary>
    public HashSet<string> InstanciasQueQuebram { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Instancia, string Telefone, string Texto)> TextosEnviados { get; } = [];

    /// <summary>Erro a lancar no proximo envio — simula a Evolution fora do ar ou respondendo
    /// erro. Null = envia normal.</summary>
    public Exception? ErroParaLancar { get; set; }

    /// <summary>O que devolver como wa_message_id. String VAZIA simula o caso real do 2xx sem
    /// key.id, que o cliente trata sem lancar.</summary>
    public string? IdParaDevolver { get; set; }

    /// <summary>Executado DENTRO da chamada de envio, antes de devolver.
    ///
    /// E o que permite provar o protocolo: o gancho consulta o banco no exato momento em que a
    /// Evolution estaria sendo chamada, e a linha da mensagem TEM que estar la. Sem isso, "grava
    /// antes de disparar" seria so uma afirmacao no comentario.</summary>
    public Func<Task>? AoEnviar { get; set; }

    public async Task<string> EnviarTextoAsync(string instanceName, string telefone, string texto, CancellationToken ct)
    {
        TextosEnviados.Add((instanceName, telefone, texto));

        if (AoEnviar is not null) await AoEnviar();
        if (ErroParaLancar is not null) throw ErroParaLancar;

        return IdParaDevolver ?? $"WA-FAKE-{TextosEnviados.Count}";
    }

    /// <summary>O que foi POSTADO como midia — INCLUSIVE os bytes.
    ///
    /// O `Base64` existe por causa do bloco 13: o teste do audio precisa afirmar que o que saiu
    /// para a Evolution e OGG, e nao o WebM que o navegador gravou. Sem os bytes, so daria para
    /// conferir o que ficou no banco — e o banco pode estar certo com o POST errado.</summary>
    public List<(string Instancia, string Telefone, string Base64,
                 string Mediatype, string Mime, string Nome, string? Legenda)>
        MidiasEnviadas { get; } = [];

    /// <summary>MESMO comportamento do texto, de proposito (MID-1).
    ///
    /// Antes devolvia "WA-FAKE-MIDIA" fixo e ignorava `ErroParaLancar` e `IdParaDevolver` — o
    /// que fazia todo teste de FALHA de envio de midia passar por engano, porque o fake nunca
    /// falhava. Um dublê que so sabe dar certo nao prova protocolo nenhum.</summary>
    public async Task<string> EnviarMidiaAsync(string instanceName, string telefone, string base64,
        string mediatype, string mimeType, string fileName, string? legenda, CancellationToken ct)
    {
        MidiasEnviadas.Add((instanceName, telefone, base64, mediatype, mimeType, fileName, legenda));

        if (AoEnviar is not null) await AoEnviar();
        if (ErroParaLancar is not null) throw ErroParaLancar;

        return IdParaDevolver ?? $"WA-FAKE-MIDIA-{MidiasEnviadas.Count}";
    }

    /// <summary>O `mensagemJson` que o processador mandou. O teste afirma sobre ele: mandar so
    /// a chave e o que fazia a Evolution responder "Message not found" e toda midia recebida
    /// entrar sem anexo.</summary>
    public string? UltimaMensagemJson { get; private set; }

    public Task<MidiaRecebida?> ObterMidiaAsync(
        string instanceName, string waMessageId, string mensagemJson, CancellationToken ct)
    {
        UltimaMensagemJson = mensagemJson;
        return Task.FromResult(MidiaParaDevolver);
    }

    /// <summary>Nota de voz: rota PROPRIA. O teste distingue isto de `EnviarMidiaAsync`, porque
    /// as duas devolvem 2xx e produzem coisas diferentes no celular do cliente.</summary>
    public List<(string Instancia, string Telefone, string Base64)> AudiosEnviados { get; } = [];

    public async Task<string> EnviarAudioAsync(
        string instanceName, string telefone, string base64, CancellationToken ct)
    {
        AudiosEnviados.Add((instanceName, telefone, base64));

        if (AoEnviar is not null) await AoEnviar();
        if (ErroParaLancar is not null) throw ErroParaLancar;

        return IdParaDevolver ?? $"WA-FAKE-VOZ-{AudiosEnviados.Count}";
    }

    public Task<string> StatusInstanciaAsync(string instanceName, CancellationToken ct) =>
        InstanciasQueQuebram.Contains(instanceName)
            ? throw new InvalidOperationException($"instância {instanceName} corrompida")
            : Task.FromResult(EstadoPorInstancia.TryGetValue(instanceName, out var e) ? e : EstadoParaDevolver);

    public Task<RespostaQr> ConectarInstanciaAsync(string instanceName, string? numeroPareamento, CancellationToken ct) =>
        Task.FromResult(new RespostaQr("base64-do-qr", "codigo", numeroPareamento is null ? null : "PAIR-1234", "connecting"));

    public Task<DetalhesInstancia?> ObterDetalhesInstanciaAsync(string instanceName, CancellationToken ct) =>
        Task.FromResult(DetalhesParaDevolver);

    public Task DesconectarInstanciaAsync(string instanceName, CancellationToken ct) => Task.CompletedTask;

    /// <summary>As instancias que o servico mandou apagar. O teste confere que remover a conexao
    /// no banco tambem apagou do outro lado — sem isso a instancia ficaria viva e pareada.</summary>
    public List<string> InstanciasRemovidas { get; } = [];

    public Task RemoverInstanciaAsync(string instanceName, CancellationToken ct)
    {
        InstanciasRemovidas.Add(instanceName);
        return Task.CompletedTask;
    }
}

/// <summary>Armazenamento em memoria. Guarda o que foi gravado para o teste conferir a CHAVE —
/// que precisa ser deterministica pelo wa_message_id, senao cada reentrega do webhook deixa um
/// objeto orfao.</summary>
public sealed class ArmazenamentoFalso : IArmazenamentoMidia
{
    public Dictionary<string, byte[]> Objetos { get; } = [];

    /// <summary>Quantas VEZES SalvarAsync foi chamado — distingue "sobrescreveu a mesma chave"
    /// de "gravou duas vezes".</summary>
    public int Gravacoes { get; private set; }

    public Task SalvarAsync(byte[] conteudo, string chave, CancellationToken ct)
    {
        Objetos[chave] = conteudo;
        Gravacoes++;
        return Task.CompletedTask;
    }

    public Task<Stream?> AbrirAsync(string chave, CancellationToken ct) =>
        Task.FromResult<Stream?>(Objetos.TryGetValue(chave, out var b) ? new MemoryStream(b) : null);
}

/// <summary>Registra os eventos empurrados ao painel, para o teste conferir QUE evento saiu e
/// quantas vezes — reentrega de webhook nao pode notificar de novo.</summary>
public sealed class NotificadorFalso : INotificadorPainel
{
    public List<MensagemPainel> Mensagens { get; } = [];
    public List<ConversaPainel> Conversas { get; } = [];
    public List<ContatoPainel> Contatos { get; } = [];
    /// <summary>`Ack` nulo = a mensagem foi editada (ver `INotificadorPainel.StatusMensagemAsync`).</summary>
    public List<(long MensagemId, short? Ack)> Acks { get; } = [];
    public List<ConexaoPainel> Conexoes { get; } = [];

    public Task MensagemRecebidaAsync(long empresaId, MensagemPainel m, CancellationToken ct)
    { Mensagens.Add(m); return Task.CompletedTask; }

    public Task ConversaAbertaAsync(long empresaId, ConversaPainel c, CancellationToken ct)
    { Conversas.Add(c); return Task.CompletedTask; }

    public Task ContatoCriadoAsync(long empresaId, ContatoPainel c, CancellationToken ct)
    { Contatos.Add(c); return Task.CompletedTask; }

    public Task StatusMensagemAsync(long empresaId, long mensagemId, short? ack, CancellationToken ct)
    { Acks.Add((mensagemId, ack)); return Task.CompletedTask; }

    public Task ConexaoMudouAsync(long empresaId, ConexaoPainel c, CancellationToken ct)
    { Conexoes.Add(c); return Task.CompletedTask; }
}

/// <summary>Monta os payloads da Evolution como ela realmente manda. Ter isso num lugar so evita
/// que cada teste invente um formato ligeiramente diferente e o conjunto pare de provar que o
/// parse funciona.</summary>
public static class PayloadEvolution
{
    /// <param name="citando">O `wa_message_id` da mensagem CITADA. ⚠️ O caminho e
    /// `data.contextInfo.stanzaId`, e nao `data.message.contextInfo` — medido contra os payloads
    /// reais do `nexora_dev`, onde as tres entradas com citacao resolvem so por ele.</param>
    public static string Mensagem(
        string instancia, string remoteJid, string waId, string? texto,
        bool fromMe = false, string? pushName = null, long? timestamp = null,
        string messageType = "conversation", string? citando = null) => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{instancia}}",
          "data": {
            "key": { "id": "{{waId}}", "remoteJid": "{{remoteJid}}", "fromMe": {{(fromMe ? "true" : "false")}} },
            "pushName": {{(pushName is null ? "null" : $"\"{pushName}\"")}},
            "messageType": "{{messageType}}",
            "message": { "conversation": {{(texto is null ? "null" : $"\"{texto}\"")}} },
            {{(citando is null ? "" : $"\"contextInfo\": {{ \"stanzaId\": \"{citando}\" }},")}}
            "messageTimestamp": {{timestamp ?? 1780000000}}
          }
        }
        """;

    public static string Midia(
        string instancia, string remoteJid, string waId, string mimetype,
        string? legenda = null, string messageType = "imageMessage") => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{instancia}}",
          "data": {
            "key": { "id": "{{waId}}", "remoteJid": "{{remoteJid}}", "fromMe": false },
            "messageType": "{{messageType}}",
            "message": {
              "imageMessage": {
                "mimetype": "{{mimetype}}",
                "fileName": "foto.jpg",
                "caption": {{(legenda is null ? "null" : $"\"{legenda}\"")}}
              }
            },
            "messageTimestamp": 1780000000
          }
        }
        """;

    /// <summary>Um payload com o nó `message` ESCRITO À MÃO (REC-2).
    ///
    /// Os outros helpers montam os dois formatos que a Nexora já sabia ler. Este existe para os
    /// tipos que ela NÃO sabia — template, figurinha, localização, reação — e para o tipo
    /// inventado que prova que o desconhecido não some.</summary>
    public static string Bruta(
        string instancia, string remoteJid, string waId, string messageType, string messageJson,
        bool fromMe = false) => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{instancia}}",
          "data": {
            "key": { "id": "{{waId}}", "remoteJid": "{{remoteJid}}", "fromMe": {{(fromMe ? "true" : "false")}} },
            "messageType": "{{messageType}}",
            "message": {{messageJson}},
            "messageTimestamp": 1780000000
          }
        }
        """;

    /// <summary>O template REAL que o contato (83) 95278-7173 mandou, reduzido ao que importa.
    ///
    /// O payload de verdade tem 48 KB — a chave `mediaKey` sozinha é um objeto com 32 campos
    /// numerados. O que decide o comportamento é a forma: `templateMessage.hydratedTemplate` com
    /// `hydratedContentText` e `hydratedButtons`.</summary>
    public const string TemplateDoSorteio = """
        {
          "templateMessage": {
            "templateId": "1755223438925433",
            "hydratedTemplate": {
              "templateId": "1755223438925433",
              "imageMessage": { "mimetype": "image/jpeg", "width": 1086, "height": 1448 },
              "hydratedContentText": "Chegou uma nova atualização sobre o sorteio de R$ 2 milhões acontece hoje, às 21h\n\nClique para ver mais detalhes 👇🏻",
              "hydratedButtons": [
                { "index": 0, "urlButton": { "displayText": "Mais informações", "url": "https://w.meta.me/s/21NW8gsIBSIylxC" } }
              ]
            }
          },
          "messageContextInfo": { "deviceListMetadataVersion": 2 }
        }
        """;

    /// <summary>A reação REAL colhida do banco: o cliente reagiu a uma mensagem NOSSA com 😘.</summary>
    public const string ReacaoBeijo = """
        {
          "reactionMessage": {
            "key": { "id": "AC748DC5DCC3182D5E37880CB8E0D671", "fromMe": true, "remoteJid": "233727858876576@lid" },
            "text": "😘"
          },
          "messageContextInfo": { "deviceListMetadataVersion": 2 }
        }
        """;

    /// <summary>A EDIÇÃO REAL colhida do banco: o contato (84) 9425-9023 corrigiu "Falr" para
    /// "Fale". Reduzida à forma que decide o comportamento — `encIv` e `encPayload` são objetos
    /// de bytes numerados, e o original tem 131 deles.
    ///
    /// ⚠️ O `targetMessageKey` vem do ponto de vista de QUEM EDITOU: `fromMe: true` e o
    /// `remoteJid` é o LID do NOSSO número. Só o `id` serve para achar a original.</summary>
    public static string Edicao(
        string instancia, string remoteJid, string waId, string alvo,
        bool fromMe = false, long timestamp = 1780000060) => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{instancia}}",
          "data": {
            "key": { "id": "{{waId}}", "remoteJid": "{{remoteJid}}", "fromMe": {{(fromMe ? "true" : "false")}} },
            "messageType": "secretEncryptedMessage",
            "message": {
              "secretEncryptedMessage": {
                "encIv": { "0": 197, "1": 48, "2": 95 },
                "encPayload": { "0": 49, "1": 145, "2": 212 },
                "secretEncType": 2,
                "targetMessageKey": { "id": "{{alvo}}", "fromMe": true, "remoteJid": "229381888831529@lid" }
              },
              "messageContextInfo": { "deviceListMetadataVersion": 2 }
            },
            "messageTimestamp": {{timestamp}}
          }
        }
        """;

    // ---- A EDIÇÃO REAL, CIFRADA ----
    /// <summary>Os bytes de verdade do "Falr" → "Fale": o `messageSecret` da original e o conteúdo
    /// cifrado da edição, colhidos do `nexora_dev`. Abrem com `LidQueAbre` — o LID do contato, que
    /// a Evolution só entrega na confirmação de entrega — e com mais nenhum JID.
    ///
    /// É o que prova que a decifragem bate com o formato do WhatsApp, e não só consigo mesma.</summary>
    public const string IdQueFoiEditada = "ACE22E79700B26B96DC1560B732014FD";
    public const string LidQueAbre = "180586530504742@lid";
    public const string SegredoReal = """{ "0": 115, "1": 147, "2": 213, "3": 185, "4": 77, "5": 82, "6": 253, "7": 119, "8": 46, "9": 38, "10": 168, "11": 98, "12": 178, "13": 180, "14": 89, "15": 188, "16": 124, "17": 94, "18": 172, "19": 31, "20": 227, "21": 7, "22": 66, "23": 251, "24": 201, "25": 63, "26": 178, "27": 1, "28": 42, "29": 81, "30": 124, "31": 87 }""";
    public const string IvReal = """{ "0": 197, "1": 48, "2": 95, "3": 12, "4": 10, "5": 126, "6": 86, "7": 249, "8": 50, "9": 171, "10": 39, "11": 174 }""";
    public const string CifradoReal = """{ "0": 49, "1": 145, "2": 212, "3": 96, "4": 183, "5": 16, "6": 140, "7": 160, "8": 15, "9": 34, "10": 135, "11": 205, "12": 77, "13": 220, "14": 106, "15": 199, "16": 218, "17": 104, "18": 200, "19": 44, "20": 73, "21": 99, "22": 11, "23": 49, "24": 183, "25": 102, "26": 202, "27": 180, "28": 224, "29": 124, "30": 247, "31": 195, "32": 41, "33": 30, "34": 141, "35": 56, "36": 149, "37": 169, "38": 85, "39": 223, "40": 115, "41": 8, "42": 229, "43": 177, "44": 37, "45": 109, "46": 79, "47": 33, "48": 88, "49": 161, "50": 156, "51": 36, "52": 45, "53": 222, "54": 106, "55": 4, "56": 129, "57": 42, "58": 95, "59": 116, "60": 73, "61": 86, "62": 62, "63": 92, "64": 45, "65": 78, "66": 207, "67": 127, "68": 90, "69": 192, "70": 138, "71": 141, "72": 18, "73": 156, "74": 19, "75": 77, "76": 196, "77": 70, "78": 63, "79": 44, "80": 5, "81": 6, "82": 207, "83": 8, "84": 45, "85": 87, "86": 103, "87": 27, "88": 12, "89": 62, "90": 199, "91": 225, "92": 89, "93": 149, "94": 198, "95": 113, "96": 4, "97": 72, "98": 190, "99": 63, "100": 155, "101": 0, "102": 67, "103": 13, "104": 68, "105": 48, "106": 183, "107": 156, "108": 35, "109": 172, "110": 45, "111": 160, "112": 60, "113": 131, "114": 75, "115": 61, "116": 147, "117": 57, "118": 69, "119": 41, "120": 173, "121": 209, "122": 103, "123": 0, "124": 199, "125": 241, "126": 187, "127": 247, "128": 231, "129": 194, "130": 31 }""";

    /// <summary>A original, com o `messageSecret` dentro de `messageContextInfo` — onde ele vem.</summary>
    public static string OriginalComSegredo(
        string instancia, string remoteJid, string texto, long timestamp = 1780000000) => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{instancia}}",
          "data": {
            "key": { "id": "{{IdQueFoiEditada}}", "remoteJid": "{{remoteJid}}", "fromMe": false },
            "messageType": "conversation",
            "message": {
              "conversation": "{{texto}}",
              "messageContextInfo": { "messageSecret": {{SegredoReal}} }
            },
            "messageTimestamp": {{timestamp}}
          }
        }
        """;

    public static string EdicaoReal(
        string instancia, string remoteJid, string waId, long timestamp = 1780000060) => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{instancia}}",
          "data": {
            "key": { "id": "{{waId}}", "remoteJid": "{{remoteJid}}", "fromMe": false },
            "messageType": "secretEncryptedMessage",
            "message": {
              "secretEncryptedMessage": {
                "encIv": {{IvReal}},
                "encPayload": {{CifradoReal}},
                "secretEncType": 2,
                "targetMessageKey": { "id": "{{IdQueFoiEditada}}", "fromMe": true, "remoteJid": "229381888831529@lid" }
              }
            },
            "messageTimestamp": {{timestamp}}
          }
        }
        """;

    /// <summary>A confirmação de entrega como a Evolution 2.3.7 MANDA: plana, com `keyId` e o
    /// `remoteJid` cru. Medida em 2026-10-09 — o `Ack` acima é a forma antiga, que o Nexora lia.</summary>
    public static string AckPlano(string instancia, string keyId, string remoteJid, string status) => $$"""
        {
          "event": "messages.update",
          "instance": "{{instancia}}",
          "data": { "keyId": "{{keyId}}", "remoteJid": "{{remoteJid}}", "fromMe": true, "status": "{{status}}" }
        }
        """;

    /// <summary>O SEGUNDO formato de template, colhido do banco: um `templateMessage` cujo
    /// conteúdo mora em `interactiveMessageTemplate.body.text`, e não em `hydratedTemplate`.
    ///
    /// A primeira versão do `ConteudoLegivel` só conhecia a forma `hydratedTemplate` — esta caía
    /// no rótulo de "não suportada" mesmo com o texto inteiro à vista.</summary>
    public const string TemplateInterativo = """
        {
          "templateMessage": {
            "templateId": "9876543210",
            "interactiveMessageTemplate": {
              "body": { "text": "Oi, Sidinaldo. Pague o valor total pela seção “Empréstimos”: https://mpago.li/1d4Y3tK" },
              "header": { "title": "Linha de Crédito" },
              "nativeFlowMessage": { "buttons": [] }
            }
          }
        }
        """;

    public static string Ack(string instancia, string waId, string status) => $$"""
        {
          "event": "messages.update",
          "instance": "{{instancia}}",
          "data": { "key": { "id": "{{waId}}" }, "status": "{{status}}" }
        }
        """;

    public static string Conexao(string instancia, string state) => $$"""
        {
          "event": "connection.update",
          "instance": "{{instancia}}",
          "data": { "state": "{{state}}" }
        }
        """;
}

/// <summary>Monta as entregas do webhook da Cloud API no formato da documentacao da Meta (INT-XX).
///
/// ⚠️ NENHUM DESTES FORMATOS FOI VISTO NUMA ENTREGA REAL: ate o primeiro numero oficial em producao,
/// nunca houve uma. Quando houver, os exemplos daqui sao trocados pelos verdadeiros.
///
/// Os espacos entre chaves seguidas sao de proposito: `}}` fecha interpolacao no `$$"""`.</summary>
public static class PayloadCloudApi
{
    /// <summary>A entrega inteira, em BYTES: e sobre eles que a Meta assina.</summary>
    public static byte[] Entrega(params string[] contas) => Encoding.UTF8.GetBytes(
        $$"""{"object":"whatsapp_business_account","entry":[{{string.Join(",", contas)}}]}""");

    /// <summary>Uma `entry`: a WABA e as mudancas dela.</summary>
    public static string Conta(string wabaId, params string[] mudancas) =>
        $$"""{"id":"{{wabaId}}","changes":[{{string.Join(",", mudancas)}}]}""";

    /// <summary>Uma mudanca `messages` do numero `pnid`, com mensagens que `de` mandou.</summary>
    public static string Recebidas(string pnid, string de, string nome, params string[] mensagens) => $$"""
        {"field":"messages","value":{"messaging_product":"whatsapp",
         "metadata":{"display_phone_number":"15550001111","phone_number_id":"{{pnid}}"},
         "contacts":[{"profile":{"name":"{{nome}}"},"wa_id":"{{de}}"}],
         "messages":[{{string.Join(",", mensagens)}}] } }
        """;

    /// <summary>Uma mudanca `messages` do numero `pnid`, com status de mensagens NOSSAS.</summary>
    public static string Status(string pnid, params string[] status) => $$"""
        {"field":"messages","value":{"messaging_product":"whatsapp",
         "metadata":{"display_phone_number":"15550001111","phone_number_id":"{{pnid}}"},
         "statuses":[{{string.Join(",", status)}}] } }
        """;

    /// <summary>`extra` entra no fim do objeto, ja com a virgula: `context`, `referral`.</summary>
    public static string Texto(string de, string id, string texto, string extra = "") => $$"""
        {"from":"{{de}}","id":"{{id}}","timestamp":"{{Agora()}}","type":"text",
         "text":{"body":"{{texto}}"} {{extra}} }
        """;

    public static string Midia(
        string de, string id, string tipo, string mediaId, string mime,
        string? legenda = null, string? arquivo = null)
    {
        var campos = "\"id\":\"" + mediaId + "\",\"mime_type\":\"" + mime + "\"";
        if (legenda != null) campos += ",\"caption\":\"" + legenda + "\"";
        if (arquivo != null) campos += ",\"filename\":\"" + arquivo + "\"";
        return DoTipo(de, id, tipo, "{" + campos + "}");
    }

    /// <summary>Uma mensagem de tipo qualquer, com o objeto do tipo como veio.</summary>
    public static string DoTipo(string de, string id, string tipo, string corpoDoTipo) => $$"""
        {"from":"{{de}}","id":"{{id}}","timestamp":"{{Agora()}}","type":"{{tipo}}",
         "{{tipo}}": {{corpoDoTipo}} }
        """;

    public static string UmStatus(
        string id, string status, string para, int? codigoErro = null, string? tituloErro = null)
    {
        var erros = "";
        if (codigoErro != null)
            erros = $$""","errors":[{"code":{{codigoErro}},"title":"{{tituloErro}}"}]""";
        return $$"""
            {"id":"{{id}}","status":"{{status}}","timestamp":"{{Agora()}}","recipient_id":"{{para}}" {{erros}} }
            """;
    }

    /// <summary>O clique num anuncio "Clique para WhatsApp", como a Cloud API o descreve.</summary>
    public static string Anuncio(string ctwaClid) => $$"""
        ,"referral":{"source_url":"https://fb.me/abc?x=1","source_id":"120210000000000999",
         "source_type":"ad","headline":"Promoção de outubro","body":"Fale com a gente",
         "media_type":"image","ctwa_clid":"{{ctwaClid}}"}
        """;

    private static long Agora() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
