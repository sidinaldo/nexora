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

/// <summary>Um evento no registro, para a tela de "últimas conversões".
///
/// `Payload` vai junto pela mesma razão do registro de webhooks: "não está chegando na Meta" termina
/// sempre em "o que exatamente vocês mandaram?". E ele NÃO tem o token dentro — ver
/// `MontadorEventoMeta`.</summary>
public record ConversaoDto(
    long Id,
    string Tipo,
    string Status,
    string Contato,
    decimal? Valor,
    short Tentativas,
    int? CodigoResposta,
    int? CodigoMeta,
    string? FbtraceId,
    string? Erro,
    DateTime OcorridoEm,
    DateTime ExpiraEm,
    DateTime? EntregueEm,
    DateTime CriadoEm,
    string Payload,

    /// <summary>⚠️ `expirado` NÃO pode reenviar, e é por isso que ele é um status próprio. A janela
    /// de 7 dias da Meta é recusa do mundo: reenviar nunca vai funcionar, e oferecer o botão seria
    /// oferecer um gesto que só pode fracassar.</summary>
    bool PodeReenviar);

public record PainelConversoes(
    CredencialDto? Credencial,

    /// <summary>O NÚMERO QUE FAZ CONECTAR: quantos leads dos últimos 30 dias chegaram com
    /// identificador de anúncio.
    ///
    /// Sem credencial, ele é a frase que cobra — "12 leads vieram de anúncio e a Meta não ficou
    /// sabendo". Com credencial, é a conferência de que o rastro está de fato chegando: um zero
    /// aqui, com o código novo publicado, significa que alguém colou o código antigo.</summary>
    int LeadsComAnuncio30Dias,

    IReadOnlyList<ConversaoDto> Conversoes);

/// <summary>O que aconteceu no botão "Enviar evento de teste". `Codigo` nulo = nem houve resposta
/// (rede, DNS, timeout). `FbtraceId` é o que o suporte da Meta pede primeiro.</summary>
public record ResultadoTesteConversao(bool Ok, int? Codigo, string? FbtraceId, string? Erro);

public interface IServicoConversoes
{
    Task<PainelConversoes> ObterAsync(CancellationToken ct);

    /// <summary>Cria ou atualiza a credencial da Meta. Token em branco mantém o anterior.</summary>
    Task SalvarAsync(SalvarCredencial dados, CancellationToken ct);

    /// <summary>Apaga a credencial. As conversões pendentes são CANCELADAS, não deixadas na fila:
    /// sem token elas não têm como sair, e fila que não drena é dívida silenciosa.</summary>
    Task RemoverAsync(CancellationToken ct);

    /// <summary>Manda um evento de teste e ESPERA a resposta.
    ///
    /// ===================== O ÚNICO LUGAR QUE ENTREGA DENTRO DA REQUISIÇÃO =====================
    /// Mesma exceção do botão de teste do webhook, e pela mesma razão: a pessoa está olhando o
    /// botão. Um "enviado, veja depois" não resolveria o chamado que este botão existe para
    /// resolver — que é sempre "não está chegando, e não sei por quê".
    /// ========================================================================================
    ///
    /// ⚠️ NÃO GRAVA NADA NA FILA. O evento de teste não é conversão de ninguém: gravá-lo poria uma
    /// linha de `Lead` de um contato inventado no registro do cliente, e ocuparia o único parcial
    /// daquele contato.
    ///
    /// Com `codigo_teste` preenchido, ele aparece em "Eventos de teste" no Gerenciador e **não
    /// entra** na otimização. Sem ele, entra — e a tela avisa.</summary>
    Task<ResultadoTesteConversao> TestarAsync(CancellationToken ct);

    /// <summary>Devolve uma conversão falha para a fila. Não envia na hora: volta a `pendente` com
    /// as tentativas zeradas, e a próxima rodada a manda.</summary>
    Task ReenviarAsync(long id, CancellationToken ct);
}
