namespace Nexora.Core.Whatsapp;

/// <summary>Onde o binario da midia fica. FASE 1: disco local (ver ArmazenamentoDisco).
///
/// A interface existe desde ja para que a troca por S3/R2 na fase 2 seja um registro de DI, e
/// nao uma cirurgia no processador do webhook. O que muda la e o `chave` virar object key e o
/// download virar URL assinada — nada disso vaza para quem grava.</summary>
public interface IArmazenamentoMidia
{
    Task SalvarAsync(byte[] conteudo, string chave, CancellationToken ct);

    /// <summary>Stream de leitura, ou null se o objeto nao existe (arquivo removido a mao,
    /// restore parcial). Quem serve decide o que fazer — nao lanca.</summary>
    Task<Stream?> AbrirAsync(string chave, CancellationToken ct);
}
