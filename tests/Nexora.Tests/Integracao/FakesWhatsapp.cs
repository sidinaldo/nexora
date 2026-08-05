using Nexora.Core.Whatsapp;

namespace Nexora.Tests.Integracao;

/// <summary>Cliente de WhatsApp falso. O teste do webhook nao pode falar com a Evolution de
/// verdade — o que se prova aqui e o que o PROCESSADOR faz com o payload, nao o HTTP.</summary>
public sealed class ClienteWhatsAppFalso : IClienteWhatsApp
{
    public MidiaRecebida? MidiaParaDevolver { get; set; }
    public DetalhesInstancia? DetalhesParaDevolver { get; set; }
    public string EstadoParaDevolver { get; set; } = "open";

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

    public Task<string> EnviarMidiaAsync(string instanceName, string telefone, string base64,
        string mediatype, string mimeType, string fileName, string? legenda, CancellationToken ct) =>
        Task.FromResult("WA-FAKE-MIDIA");

    public Task<MidiaRecebida?> ObterMidiaAsync(string instanceName, string waMessageId, CancellationToken ct) =>
        Task.FromResult(MidiaParaDevolver);

    public Task<string> StatusInstanciaAsync(string instanceName, CancellationToken ct) =>
        Task.FromResult(EstadoParaDevolver);

    public Task<RespostaQr> ConectarInstanciaAsync(string instanceName, string? numeroPareamento, CancellationToken ct) =>
        Task.FromResult(new RespostaQr("base64-do-qr", "codigo", numeroPareamento is null ? null : "PAIR-1234", "connecting"));

    public Task<DetalhesInstancia?> ObterDetalhesInstanciaAsync(string instanceName, CancellationToken ct) =>
        Task.FromResult(DetalhesParaDevolver);

    public Task DesconectarInstanciaAsync(string instanceName, CancellationToken ct) => Task.CompletedTask;
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
