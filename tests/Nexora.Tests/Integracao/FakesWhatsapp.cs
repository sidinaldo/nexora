using Nexora.Core.Whatsapp;

namespace Nexora.Tests.Integracao;

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
        Task.FromResult(EstadoPorInstancia.TryGetValue(instanceName, out var e) ? e : EstadoParaDevolver);

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
    public List<(long MensagemId, short Ack)> Acks { get; } = [];
    public List<ConexaoPainel> Conexoes { get; } = [];

    public Task MensagemRecebidaAsync(long empresaId, MensagemPainel m, CancellationToken ct)
    { Mensagens.Add(m); return Task.CompletedTask; }

    public Task ConversaAbertaAsync(long empresaId, ConversaPainel c, CancellationToken ct)
    { Conversas.Add(c); return Task.CompletedTask; }

    public Task ContatoCriadoAsync(long empresaId, ContatoPainel c, CancellationToken ct)
    { Contatos.Add(c); return Task.CompletedTask; }

    public Task StatusMensagemAsync(long empresaId, long mensagemId, short ack, CancellationToken ct)
    { Acks.Add((mensagemId, ack)); return Task.CompletedTask; }

    public Task ConexaoMudouAsync(long empresaId, ConexaoPainel c, CancellationToken ct)
    { Conexoes.Add(c); return Task.CompletedTask; }
}

/// <summary>Monta os payloads da Evolution como ela realmente manda. Ter isso num lugar so evita
/// que cada teste invente um formato ligeiramente diferente e o conjunto pare de provar que o
/// parse funciona.</summary>
public static class PayloadEvolution
{
    public static string Mensagem(
        string instancia, string remoteJid, string waId, string? texto,
        bool fromMe = false, string? pushName = null, long? timestamp = null,
        string messageType = "conversation") => $$"""
        {
          "event": "messages.upsert",
          "instance": "{{instancia}}",
          "data": {
            "key": { "id": "{{waId}}", "remoteJid": "{{remoteJid}}", "fromMe": {{(fromMe ? "true" : "false")}} },
            "pushName": {{(pushName is null ? "null" : $"\"{pushName}\"")}},
            "messageType": "{{messageType}}",
            "message": { "conversation": {{(texto is null ? "null" : $"\"{texto}\"")}} },
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
