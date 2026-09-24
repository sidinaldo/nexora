using System.Text.Json;
using System.Text.Json.Nodes;
using Nexora.Core.Entidades;

namespace Nexora.Core.Conversoes;

/// <summary>O que se sabe sobre o fato, antes de virar JSON.
///
/// Tudo opcional menos o tipo, o id e a hora: o lead que chegou pelo WhatsApp sem site nenhum tem
/// só telefone, e ele é o caso mais comum deste público.</summary>
public record FatoDeConversao(
    TipoConversao Tipo,
    Guid EventoId,
    DateTime OcorridoEm,
    string? Email = null,
    string? Telefone = null,
    string? Ip = null,
    string? UserAgent = null,
    string? Fbp = null,
    string? Fbc = null,
    string? Pagina = null,
    FonteRastreio? Fonte = null,
    decimal? Valor = null,

    /// <summary>O identificador do clique no anúncio Clique-para-WhatsApp (INT-4, commit 8).
    ///
    /// Quando ele existe, o evento muda de natureza: `action_source` passa a `business_messaging` e
    /// o `user_data` ganha o `ctwa_clid` e o canal. É o que permite a Meta casar uma conversa de
    /// WhatsApp com o anúncio que a começou — sem site nenhum no meio.</summary>
    string? CtwaClid = null);

/// <summary>MONTA O CORPO DO EVENTO DA META (INT-4). Puro: sem banco, sem rede.
///
/// ===================== O QUE ESTE CORPO **NÃO** TEM =====================
/// Nem `access_token`, nem `test_event_code`. Os dois são acrescentados pelo cliente HTTP na hora
/// do envio, e a razão é dupla:
///
///   • o payload guardado aparece NA TELA do cliente, no registro de conversões. Token ali é
///     credencial em captura de tela de suporte;
///   • trocar o token não pode invalidar o que já está na fila — e invalidaria, se o token fosse
///     parte do corpo congelado.
///
/// O que o corpo congela é o FORMATO: se ele mudasse entre a criação e a terceira tentativa, a Meta
/// receberia dois corpos diferentes com o mesmo `event_id`.
/// =======================================================================
///
/// ===================== UM EVENTO POR REQUISIÇÃO =====================
/// A Meta aceita até 1.000 por chamada, mas **um evento inválido recusa o lote inteiro**. Com um
/// por requisição, o lead da padaria não se perde porque o da farmácia veio sem telefone — mesma
/// disciplina da fila de webhooks.
/// ====================================================================</summary>
public static class MontadorEventoMeta
{
    /// <summary>O nome do evento no vocabulário da Meta. A tradução mora AQUI e só aqui: o nome da
    /// nossa regra (`TipoConversao.Compra`) não deve depender do nome que um terceiro deu a ela.</summary>
    public static string NomeDoEvento(TipoConversao tipo) => tipo switch
    {
        TipoConversao.Lead => "Lead",
        TipoConversao.Compra => "Purchase",
        _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "Tipo sem nome na Meta.")
    };

    /// <summary>De onde a Meta entende que o fato veio.
    ///
    /// ===================== POR QUE A COMPRA É `system_generated` =====================
    /// O `Lead` acontece onde a pessoa está: no site (`website`) ou na conversa (`chat`). A COMPRA
    /// não: ela acontece quando um vendedor arrasta um card, dias depois, dentro do CRM. Nenhum
    /// canal a observou.
    ///
    /// `website` para a compra seria mais bonito — herdaria a URL da visita original — e seria
    /// falso: aquela URL não é onde a venda aconteceu. `system_generated` é o que descreve
    /// "o sistema do negócio registrou", e dispensa `event_source_url`, que é obrigatório só em
    /// evento de site.
    ///
    /// ⚠️ A DECIDIR COM O TESTE REAL (commit 6): com `test_event_code`, o Gerenciador de Eventos
    /// mostra se ela aceita a compra assim. Se recusar, o caminho é `chat`/`website` herdado do
    /// rastro — e esta é a linha que muda.
    /// ==============================================================================</summary>
    public static string Origem(TipoConversao tipo, FonteRastreio? fonte, string? ctwaClid = null)
        => (tipo, fonte) switch
        {
            (TipoConversao.Compra, _) => "system_generated",
            (_, FonteRastreio.FormularioSite) => "website",

            // ⚠️ `business_messaging` SÓ COM `ctwa_clid`, e a condição é o ponto. É o valor que a Meta
            // documenta para conversão de Clique-para-WhatsApp, e ela o espera acompanhado do
            // identificador do clique. Mandá-lo sem o `ctwa_clid` seria descrever um caminho que não
            // temos como provar — e um evento recusado por campo ausente vale menos que um aceito
            // como `chat`.
            (_, FonteRastreio.AnuncioWhatsapp) when ctwaClid is not null => "business_messaging",

            // Sem identificador: o lead entrou pelo WhatsApp, ou por um formulário antigo. `chat` é o
            // canal real da maioria deste público — e o casamento por telefone funciona sozinho.
            _ => "chat"
        };

    /// <summary>O corpo, pronto para o `POST`.
    ///
    /// `JsonObject` e não um record serializado: metade dos campos é opcional, e `user_data` sem
    /// chave é diferente de `user_data` com a chave nula — a segunda forma faz a Meta contar o
    /// campo como presente e vazio.</summary>
    public static string Montar(FatoDeConversao fato)
    {
        var usuario = new JsonObject();

        // Arrays de um elemento: é o formato da Meta, e ela aceita mais de um valor por campo.
        // Chave AUSENTE quando não há hash — ver `HashPessoal`.
        var email = HashPessoal.Email(fato.Email);
        if (email is not null) usuario["em"] = new JsonArray(email);

        var telefone = HashPessoal.Telefone(fato.Telefone);
        if (telefone is not null) usuario["ph"] = new JsonArray(telefone);

        // ⚠️ EM CLARO, e não hasheados. A Meta os usa como estão.
        if (!string.IsNullOrWhiteSpace(fato.Ip)) usuario["client_ip_address"] = fato.Ip;
        if (!string.IsNullOrWhiteSpace(fato.UserAgent)) usuario["client_user_agent"] = fato.UserAgent;
        if (!string.IsNullOrWhiteSpace(fato.Fbp)) usuario["fbp"] = fato.Fbp;
        if (!string.IsNullOrWhiteSpace(fato.Fbc)) usuario["fbc"] = fato.Fbc;

        var origem = Origem(fato.Tipo, fato.Fonte, fato.CtwaClid);

        // O identificador do clique no anúncio, e o canal em que a conversa aconteceu. A Meta pede os
        // dois juntos em `business_messaging` — e é este par que casa a conversa com o anúncio.
        if (origem == "business_messaging")
        {
            usuario["ctwa_clid"] = fato.CtwaClid;
            usuario["messaging_channel"] = "whatsapp";
        }

        var evento = new JsonObject
        {
            ["event_name"] = NomeDoEvento(fato.Tipo),
            // Segundos desde a época, em UTC. A Meta recusa a requisição INTEIRA se este valor
            // estiver mais de 7 dias no passado.
            ["event_time"] = new DateTimeOffset(
                DateTime.SpecifyKind(fato.OcorridoEm, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            ["action_source"] = origem,
            // A DEDUPLICAÇÃO: mesmo `event_name` + mesmo `event_id` que o pixel mandou = um evento,
            // não dois. Sem isto, o cliente com pixel instalado conta cada lead duas vezes.
            ["event_id"] = fato.EventoId.ToString(),
            ["user_data"] = usuario
        };

        // Obrigatório em evento de site, e só lá. Mandar em `system_generated` não ajuda e é mais
        // um dado saindo daqui.
        if (origem == "website" && !string.IsNullOrWhiteSpace(fato.Pagina))
            evento["event_source_url"] = fato.Pagina;

        // O valor é o que muda a natureza do `Purchase`: sem ele a Meta sabe que houve venda e não
        // sabe quanto — e "otimizar por valor de compra" deixa de existir como opção para o
        // cliente.
        if (fato.Tipo == TipoConversao.Compra && fato.Valor is > 0)
            evento["custom_data"] = new JsonObject
            {
                ["value"] = fato.Valor,
                ["currency"] = "BRL"
            };

        return new JsonObject { ["data"] = new JsonArray(evento) }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
