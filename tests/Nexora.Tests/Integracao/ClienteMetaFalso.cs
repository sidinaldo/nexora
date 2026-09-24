using Nexora.Infra.Conversoes;

namespace Nexora.Tests.Integracao;

/// <summary>A Meta, sem rede (INT-4).
///
/// Guarda o que foi mandado e devolve o que se programou. É o dublê certo aqui, ao contrário do
/// publicador: o que se testa no motor é a DECISÃO sobre a resposta — qual código faz tentar de
/// novo, qual desativa a credencial, qual desiste —, e para isso a resposta precisa ser escolhida
/// pelo teste.
///
/// O cliente de verdade tem teste próprio, com as respostas cruas da Graph API.</summary>
public class ClienteMetaFalso : IClienteMeta
{
    public record Chamada(string PixelId, string Token, string Corpo, string? CodigoTeste);

    public List<Chamada> Chamadas { get; } = [];

    /// <summary>O que responder. Por padrão, aceita.</summary>
    public ResultadoEnvioMeta Resposta { get; set; } = new(true, 200, null, "fbtrace-ok", null);

    /// <summary>Respostas em sequência, para a rodada que precisa de resultados diferentes por
    /// evento. Quando acaba, volta a usar `Resposta`.</summary>
    public Queue<ResultadoEnvioMeta> Fila { get; } = new();

    public Task<ResultadoEnvioMeta> EnviarAsync(
        string pixelId, string token, string corpo, string? codigoTeste, CancellationToken ct)
    {
        Chamadas.Add(new Chamada(pixelId, token, corpo, codigoTeste));
        return Task.FromResult(Fila.Count > 0 ? Fila.Dequeue() : Resposta);
    }
}
