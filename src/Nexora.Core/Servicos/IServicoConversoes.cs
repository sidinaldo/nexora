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
    /// <summary>O id da página do Facebook, para o caminho do Clique-para-WhatsApp. Nulo é o caso
    /// normal — sem ele o lead do WhatsApp sai como `chat`, que funciona.</summary>
    string? PaginaId,
    bool Ativo,
    bool EmLead,
    bool EmCompra,
    DateTime? ConsentimentoEm,
    string? ConsentimentoPor,
    DateTime? DesativadaEm,
    string? DesativadaMotivo,
    DateTime CriadoEm,

    /// <summary>Está de fato enviando alguma coisa? `PodeEnviar(Lead) || PodeEnviar(Compra)`,
    /// calculado no serviço a partir da ENTIDADE.
    ///
    /// ⚠️ ERA UMA PROPRIEDADE CALCULADA AQUI, E ESTAVA ERRADA (INT-5). Ela repetia o `PodeEnviar`
    /// por extenso e esquecia `EmLead`/`EmCompra` — então desmarcar "Purchase" deixava o selo
    /// dizendo **enviando** enquanto toda venda caía no chão. A sexta cópia de uma regra é a que
    /// ninguém lembra de atualizar; esta sumiu.
    ///
    /// E não dava para consertar aqui: o DTO só tem o token MASCARADO, e a regra pergunta pelo
    /// token de verdade. Quem sabe responder é a entidade.</summary>
    bool Enviando,

    /// <summary>POR QUE o envio de VENDA está parado — as frases de
    /// `CredencialConversao.MotivosParados(Compra)`. Vazia = está enviando.
    ///
    /// É a lista da COMPRA, e não a do lead, porque é dela que a tela de vendas não enviadas
    /// trata. Ela já é superconjunto: as quatro condições que travam tudo (ativo, recusada pela
    /// Meta, consentimento, token) aparecem nas duas. A única coisa que ela não mostra é o lead
    /// desmarcado com a compra ligada — e isso é uma escolha de quem configurou, não um defeito.</summary>
    IReadOnlyList<string> MotivosParados);

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
    /// <summary>O id da página do Facebook vinculada ao conjunto de dados. Opcional, e só o
    /// Clique-para-WhatsApp usa.</summary>
    string? PaginaId,
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

/// <summary>===================== UMA VENDA QUE A META NUNCA VIU (INT-5) =====================
///
/// Não existe linha nenhuma para estas vendas: `PublicadorConversoes` devolve `void` quando o
/// portão está fechado, sem log e sem registro. A lista é DERIVADA — vendas que não têm evento.
///
/// O dono precisa de quatro coisas para decidir: de quem é, quanto vale, quando fechou, e quanto
/// tempo falta. É o prazo que transforma a lista em ação: "R$ 556,12 da Ysianne, vence em 38h" é
/// outra coisa que "uma venda pendente".</summary>
public record VendaSemConversaoDto(
    long NegociacaoId,
    string Contato,
    decimal? Valor,
    DateTime GanhaEm,

    /// <summary>`ganha_em + 7 dias` — o instante em que a Meta deixa de aceitar. A tela desenha a
    /// contagem a partir daqui.</summary>
    DateTime ExpiraEm,

    /// <summary>⚠️ DECIDIDO NO SERVIDOR, com o relógio dele. A tela não recalcula a janela: o
    /// botão aparece ou não a partir DESTE campo. Cliente e servidor discordando sobre "passou do
    /// prazo" produziria um botão que só pode fracassar.</summary>
    bool ForaDoPrazo);

/// <summary>A lista mais os DOIS números que fazem alguém agir (INT-5).
///
/// ⚠️ `Total` e `ValorTotal` saem de consulta própria, e não da contagem de `Vendas`: a lista tem
/// teto de 50, e somar o que coube diria menos do que a verdade.
///
/// E o valor em dinheiro não é enfeite — é a diferença entre "6 vendas não enviadas", que se lê
/// como aviso técnico, e "R$ 1.527,85 não chegaram à Meta", que se lê como prejuízo. O mesmo
/// raciocínio do `LeadsComAnuncio30Dias`, que esta classe já paga por `ObterAsync`.</summary>
public record VendasSemEnvio(
    int Total,
    decimal ValorTotal,

    /// <summary>A janela em dias, para a tela não repetir o 21 em TypeScript.</summary>
    int DiasDaJanela,

    IReadOnlyList<VendaSemConversaoDto> Vendas);

/// <summary>O que o botão "Enviar as que ainda dão tempo" conseguiu (INT-5).
///
/// `Restantes` existe porque o lote para no teto de uma rodada do motor: prometer "sai em um
/// minuto" para duzentas vendas seria mentira, e a tela precisa saber quando dizer "clique de novo
/// quando estas saírem".</summary>
public record ResultadoEnvioEmLote(int Enfileiradas, int Restantes);

public record PainelConversoes(
    CredencialDto? Credencial,

    /// <summary>O NÚMERO QUE FAZ CONECTAR: quantos leads dos últimos 30 dias chegaram com
    /// identificador de anúncio.
    ///
    /// Sem credencial, ele é a frase que cobra — "12 leads vieram de anúncio e a Meta não ficou
    /// sabendo". Com credencial, é a conferência de que o rastro está de fato chegando: um zero
    /// aqui, com o código novo publicado, significa que alguém colou o código antigo.</summary>
    int LeadsComAnuncio30Dias,

    IReadOnlyList<ConversaoDto> Conversoes,

    /// <summary>As vendas que fecharam sem virar evento (INT-5). `Total` zero é o normal — e nesse
    /// caso a tela não desenha nada, porque um painel que diz "0 pendências" todo dia é ruído, e
    /// foi ruído que fez ninguém reparar no selo `parado`.</summary>
    VendasSemEnvio VendasSemEnvio);

/// <summary>O RESUMO, para quem só precisa saber se está perdendo lead (INT-4).
///
/// ===================== POR QUE UMA ROTA PRÓPRIA, E NÃO O PAINEL INTEIRO =====================
/// A tela de Captação mostra um aviso quando chega lead de anúncio e ninguém conectou. Ela não
/// precisa da credencial nem das 50 últimas conversões — e buscá-las para desenhar uma frase seria
/// carregar cinquenta linhas de payload para não usar nenhuma.
///
/// Duas contas, e nada mais.
/// ==========================================================================================</summary>
public record ResumoConversoes(bool Enviando, int LeadsComAnuncio30Dias);

/// <summary>O que aconteceu no botão "Enviar evento de teste". `Codigo` nulo = nem houve resposta
/// (rede, DNS, timeout). `FbtraceId` é o que o suporte da Meta pede primeiro.</summary>
public record ResultadoTesteConversao(bool Ok, int? Codigo, string? FbtraceId, string? Erro);

public interface IServicoConversoes
{
    Task<PainelConversoes> ObterAsync(CancellationToken ct);

    /// <summary>Só as duas contas do aviso — ver `ResumoConversoes`.</summary>
    Task<ResumoConversoes> ResumoAsync(CancellationToken ct);

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

    /// <summary>Põe na fila a conversão de UMA venda que nunca virou evento (INT-5).
    ///
    /// ⚠️ NÃO É O `ReenviarAsync`, e os dois não podem se confundir: aquele recebe o id de um
    /// EVENTO que existe e falhou; este recebe o id de uma NEGOCIAÇÃO que nunca teve evento. Dois
    /// espaços de id diferentes, e é por isso que a rota deste fica debaixo de `vendas/`.</summary>
    Task EnviarVendaAsync(long negociacaoId, CancellationToken ct);

    /// <summary>O mesmo, para todas as que ainda cabem nos 7 dias, até o teto de uma rodada.</summary>
    Task<ResultadoEnvioEmLote> EnviarVendasPendentesAsync(CancellationToken ct);

    /// <summary>Devolve uma conversão falha para a fila. Não envia na hora: volta a `pendente` com
    /// as tentativas zeradas, e a próxima rodada a manda.</summary>
    Task ReenviarAsync(long id, CancellationToken ct);
}
