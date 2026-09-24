using Nexora.Core.Entidades;

namespace Nexora.Core.Servicos;

/// <summary>A credencial de anúncio da empresa, como o painel a vê.
///
/// ===================== SEM O TOKEN. NUNCA. NEM UMA VEZ =====================
/// É a diferença para o `WebhookDto`: aquele esconde um segredo que NÓS geramos, e por isso dava
/// para revelá-lo no ato da criação. Este o cliente cola do Gerenciador de Eventos da Meta — não
/// temos o que revelar, e devolvê-lo seria pôr a credencial dele no histórico do navegador, no
/// cache do proxy e na captura de tela do suporte.
///
/// `TokenFinal` é o sufixo mascarado, só para a pessoa reconhecer QUAL token está lá.
/// ==========================================================================</summary>
public record CredencialDto(
    long Id,
    string Plataforma,
    string Identificador,
    string? TokenFinal,
    string? CodigoTeste,
    bool Ativo,
    bool EmLead,
    bool EmCompra,
    DateTime? ConsentimentoEm,
    string? ConsentimentoPor,
    DateTime? DesativadaEm,
    string? DesativadaMotivo,
    DateTime CriadoEm)
{
    /// <summary>Está de fato enviando? É o que a tela precisa dizer numa palavra, e derivar isto
    /// no HTML seria escrever a regra do `PodeEnviar` de novo, em outra língua.</summary>
    public bool Enviando => Ativo && DesativadaEm is null && ConsentimentoEm is not null
                            && TokenFinal is not null;
}

/// <summary>O que o cliente salva. `Token` nulo ou vazio MANTÉM o que já estava lá.
///
/// ⚠️ Trocar o Pixel ID não pode ser um jeito de apagar o token por descuido: a tela não tem como
/// preencher o campo de volta (o `GET` não devolve o token), então um `PUT` com o campo em branco
/// é sempre "não mexi nisso", nunca "apague".
///
/// `ConsentimentoDeclarado` é o interruptor de LGPD. Ele não guarda um `bool`: quando vira
/// verdadeiro, o serviço grava a DATA e QUEM declarou.</summary>
public record SalvarCredencial(
    string Identificador,
    string? Token,
    string? CodigoTeste,
    bool Ativo,
    bool EmLead,
    bool EmCompra,
    bool ConsentimentoDeclarado);

public record PainelConversoes(
    CredencialDto? Credencial,

    /// <summary>O NÚMERO QUE FAZ CONECTAR: quantos leads dos últimos 30 dias chegaram com
    /// identificador de anúncio.
    ///
    /// Sem credencial, ele é a frase que cobra — "12 leads vieram de anúncio e a Meta não ficou
    /// sabendo". Com credencial, é a conferência de que o rastro está de fato chegando: um zero
    /// aqui, com o código novo publicado, significa que alguém colou o código antigo.</summary>
    int LeadsComAnuncio30Dias);

public interface IServicoConversoes
{
    Task<PainelConversoes> ObterAsync(CancellationToken ct);

    /// <summary>Cria ou atualiza a credencial da Meta. Token em branco mantém o anterior.</summary>
    Task SalvarAsync(SalvarCredencial dados, CancellationToken ct);

    /// <summary>Apaga a credencial. As conversões pendentes são CANCELADAS, não deixadas na fila:
    /// sem token elas não têm como sair, e fila que não drena é dívida silenciosa.</summary>
    Task RemoverAsync(CancellationToken ct);

    // O botão de teste, a lista de conversões e o reenvio entram no commit 6 — junto com o
    // cliente HTTP que de fato fala com a Meta. Aqui ainda não existe o que testar.
}
