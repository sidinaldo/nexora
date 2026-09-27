using System.Text.Json;
using System.Text.Json.Nodes;
using Nexora.Core.Entidades;
using Nexora.Core.Texto;

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
    /// Sozinho ele NÃO basta — ver `PaginaId`.</summary>
    string? CtwaClid = null,

    /// <summary>O id da página do Facebook vinculada ao conjunto de dados.
    ///
    /// ⚠️ SEM ELE A META RECUSA o evento de `business_messaging`, com `error_subcode 2804116`. Os
    /// dois andam juntos: o `ctwa_clid` diz QUAL clique, a página diz por ONDE a conversa entrou.</summary>
    string? PaginaId = null,

    /// <summary>O nome como está no cadastro. Vira `fn` e `ln` — +15% de qualidade cada, pela conta
    /// da própria Meta. Quem parte o nome é o montador, para a regra viver num lugar só.</summary>
    string? Nome = null,

    /// <summary>O `external_id`: o identificador da pessoa no Nexora, já hasheado por
    /// `HashPessoal.Externo`. É o elo que amarra o `Lead` e a `Compra` da mesma pessoa quando não há
    /// `fbc` nenhum — o caso de quem chegou pelo WhatsApp.</summary>
    string? ExternalId = null);

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
    /// nossa regra (`TipoConversao.Compra`) não deve depender do nome que um terceiro deu a ela.
    ///
    /// ===================== O NOME DEPENDE DA ORIGEM =====================
    /// Descoberto no primeiro teste real: com `action_source: business_messaging` a Meta **recusa**
    /// o nome `Lead` (`error_subcode 2804066`) e exige `LeadSubmitted`. `Purchase` vale nos dois.
    ///
    /// É o tipo de detalhe que nenhum teste com dublê pega: a resposta dela é que ensina.
    /// ====================================================================</summary>
    public static string NomeDoEvento(TipoConversao tipo, string? origem = null) => tipo switch
    {
        TipoConversao.Lead => origem == "business_messaging" ? "LeadSubmitted" : "Lead",
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
    public static string Origem(
        TipoConversao tipo, FonteRastreio? fonte, string? ctwaClid = null, string? paginaId = null)
        => (tipo, fonte) switch
        {
            // ⚠️ CONFIRMADO NO TESTE REAL: a Meta aceita a compra assim, e a contabiliza como evento
            // de Compra. Era a única pergunta de contrato que a documentação não fechava.
            (TipoConversao.Compra, _) => "system_generated",

            (_, FonteRastreio.FormularioSite) => "website",

            // ===================== `business_messaging` EXIGE OS DOIS =====================
            // O `ctwa_clid` diz QUAL clique; a página diz por ONDE a conversa entrou. Sem a segunda,
            // a Meta recusa com `error_subcode 2804116` — e a recusa é da requisição inteira.
            //
            // A condição é o ponto: um evento recusado por campo ausente vale MENOS que um aceito
            // como `chat`, que casa por telefone e funciona. Quem não tem página vinculada continua
            // recebendo atribuição, só mais grossa.
            // ==========================================================================
            (_, FonteRastreio.AnuncioWhatsapp) when ctwaClid is not null && paginaId is not null
                => "business_messaging",

            // Sem os dois: o lead entrou pelo WhatsApp, ou por um formulário antigo. `chat` é o
            // canal real da maioria deste público — e o casamento por telefone funciona sozinho.
            _ => "chat"
        };

    /// <summary>Tira o `business_messaging` de um corpo JÁ MONTADO e devolve o mesmo evento como
    /// `chat`. Nulo quando não havia o que rebaixar.
    ///
    /// ===================== A OUTRA METADE DA REGRA DO `Origem` =====================
    /// O `Origem` acima já diz que **um evento recusado vale menos que um aceito com casamento mais
    /// grosso** — mas só decide isso na IDA, quando o dado está ausente. Quando a Meta RECUSA o
    /// `ctwa_clid` ou a página, o corpo já foi montado e guardado, e sem isto a conversão morre.
    ///
    /// Mora aqui, e não no motor, para que as duas metades da mesma regra fiquem no mesmo arquivo:
    /// quem mudar o que é um evento de `chat` vê as duas de uma vez.
    /// ==============================================================================
    ///
    /// ⚠️ O `event_id` e o `event_time` NÃO MUDAM. É o mesmo fato — a Meta deduplica por
    /// `event_name` + `event_id`, e inventar um id novo faria o evento contar duas vezes para quem
    /// tem pixel no site.
    ///
    /// ⚠️ E É AUTOLIMITADO: depois de rebaixado o `action_source` é `chat`, então uma segunda
    /// chamada devolve nulo. Não existe laço possível.</summary>
    public static string? RebaixarParaChat(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;

        try
        {
            var envelope = JsonNode.Parse(corpo)?.AsObject();
            var evento = envelope?["data"]?.AsArray().FirstOrDefault()?.AsObject();

            if (evento is null || (string?)evento["action_source"] != "business_messaging")
                return null;

            evento["action_source"] = "chat";

            // O nome volta junto: `LeadSubmitted` só existe no caminho do anúncio, e a Meta recusa
            // um `LeadSubmitted` que não seja `business_messaging`. Trocar um sem o outro trocaria
            // uma recusa por outra.
            if ((string?)evento["event_name"] == NomeDoEvento(TipoConversao.Lead, "business_messaging"))
                evento["event_name"] = NomeDoEvento(TipoConversao.Lead);

            evento.Remove("messaging_channel");

            var usuario = evento["user_data"]?.AsObject();
            usuario?.Remove("ctwa_clid");
            usuario?.Remove("page_id");

            return envelope!.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

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

        // ===================== NOME E SOBRENOME, PARTIDOS AQUI =====================
        // `NomeDePessoa` já sabe achar o primeiro e o último pedaço COM LETRA — "(84) 95278-7173"
        // não vira nome nenhum, e "Ysia" sozinha não vira sobrenome. Reusar é o que impede uma
        // segunda definição de "primeiro nome" neste projeto.
        // ==========================================================================
        var primeiro = HashPessoal.Nome(NomeDePessoa.Primeiro(fato.Nome));
        if (primeiro is not null) usuario["fn"] = new JsonArray(primeiro);

        var ultimo = HashPessoal.Nome(NomeDePessoa.Ultimo(fato.Nome));
        if (ultimo is not null) usuario["ln"] = new JsonArray(ultimo);

        // O elo da pessoa consigo mesma ao longo do tempo — ver `HashPessoal.Externo`.
        if (!string.IsNullOrWhiteSpace(fato.ExternalId))
            usuario["external_id"] = new JsonArray(fato.ExternalId);

        // ⚠️ EM CLARO, e não hasheados. A Meta os usa como estão.
        if (!string.IsNullOrWhiteSpace(fato.Ip)) usuario["client_ip_address"] = fato.Ip;
        if (!string.IsNullOrWhiteSpace(fato.UserAgent)) usuario["client_user_agent"] = fato.UserAgent;
        if (!string.IsNullOrWhiteSpace(fato.Fbp)) usuario["fbp"] = fato.Fbp;
        if (!string.IsNullOrWhiteSpace(fato.Fbc)) usuario["fbc"] = fato.Fbc;

        var origem = Origem(fato.Tipo, fato.Fonte, fato.CtwaClid, fato.PaginaId);

        // ⚠️ EM `user_data` VÃO SÓ O CLIQUE E A PÁGINA. O `messaging_channel` vai no EVENTO, e não
        // aqui — foi o primeiro erro que o teste real devolveu (`error_subcode 2804063`: "parâmetro
        // de canal de mensagens ausente", mesmo com ele dentro do `user_data`).
        if (origem == "business_messaging")
        {
            usuario["ctwa_clid"] = fato.CtwaClid;
            usuario["page_id"] = fato.PaginaId;
        }

        var evento = new JsonObject
        {
            ["event_name"] = NomeDoEvento(fato.Tipo, origem),
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

        // O canal da conversa, NO EVENTO. Obrigatório em `business_messaging`, e os valores que a
        // Meta aceita são `whatsapp`, `messenger` e `instagram` — aqui é sempre o primeiro.
        if (origem == "business_messaging") evento["messaging_channel"] = "whatsapp";

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
