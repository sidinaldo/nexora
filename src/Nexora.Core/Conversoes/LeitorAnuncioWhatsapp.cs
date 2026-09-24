using System.Text.Json;
using System.Text.Json.Nodes;
using Nexora.Core.Entidades;

namespace Nexora.Core.Conversoes;

/// <summary>A referência do anúncio que veio grudada na primeira mensagem do WhatsApp.
///
/// `CtwaClid` é o que importa de verdade: é o identificador que a Meta usa para casar a conversa com
/// o clique no anúncio. Os outros três são para a tela.</summary>
public record AnuncioDoWhatsapp(
    string? CtwaClid,
    string? AnuncioId,
    string? Url,
    string? Titulo)
{
    /// <summary>Tem algo que valha guardar? Um `contextInfo` sem nenhum destes campos é uma conversa
    /// que começou de outro jeito — busca na lupa, link comum, contato salvo.</summary>
    public bool TemAlgo => CtwaClid is not null || AnuncioId is not null
                        || Url is not null || Titulo is not null;
}

/// <summary>LÊ O ANÚNCIO NO PAYLOAD CRU DA EVOLUTION (INT-4, commit 8).
///
/// ===================== O QUE O DIAGNÓSTICO DO COMMIT 0 ESTABELECEU =====================
/// Boa parte deste público não tem site: o anúncio é "Clique para WhatsApp" e vai direto para a
/// conversa. A consulta em `mensagens.payload_raw` mostrou que **o canal chega** — a Evolution iça o
/// `contextInfo` para a raiz do `data` e o entrega sem podar, e o processador guarda o corpo do
/// webhook verbatim. Duas mensagens com `entryPointConversionSource = "click_to_chat_link"` provaram
/// isso.
///
/// ⚠️ O QUE ELE **NÃO** ESTABELECEU: os nomes exatos dos campos no caso "anúncio". Nunca houve lead
/// de anúncio no banco de desenvolvimento, então eles vêm da documentação, não dos nossos dados. O
/// que fecha esse buraco é **um clique real num anúncio do dono** — e é por isso que este leitor é
/// escrito para FALHAR FECHADO: nenhum campo reconhecido, nenhum rastro, comportamento idêntico ao
/// de hoje.
/// ====================================================================================
///
/// ===================== POR QUE A BUSCA É RECURSIVA =====================
/// Normalmente procurar uma chave varrendo o JSON inteiro é preguiça. Aqui é o contrário: o que se
/// desconhece é justamente o ANINHAMENTO. O `externalAdReply` aparece documentado em três lugares
/// diferentes conforme a versão do Baileys e o tipo da mensagem — na raiz do `data.contextInfo`,
/// dentro de `message.extendedTextMessage.contextInfo`, e dentro do `contextInfo` de cada tipo de
/// mídia. Fixar um caminho é escolher um dos três e perder os outros dois em silêncio.
///
/// A varredura tem teto de profundidade e só entra em objeto: o payload maior que já se viu neste
/// banco tem 36 KB, e o custo é desprezível contra o de perder o lead.
/// =======================================================================</summary>
public static class LeitorAnuncioWhatsapp
{
    /// <summary>As duas formas que a Meta usa, e as duas grafias de cada campo.
    ///
    /// `externalAdReply` é o nome no Baileys (que é o que a Evolution roda); `referral` é o da API
    /// oficial do WhatsApp Cloud. Aceitar as duas custa uma linha e é o que faz este leitor
    /// continuar valendo no dia em que o cliente migrar para a oficial.</summary>
    private static readonly string[] Blocos = ["externalAdReply", "referral"];

    /// <summary>Teto de profundidade. O payload da Evolution tem uns seis níveis; oito dá folga sem
    /// abrir a porta para um JSON malicioso de mil níveis.</summary>
    private const int ProfundidadeMaxima = 8;

    /// <summary>Acha a referência do anúncio, ou nulo.
    ///
    /// NUNCA lança: o payload vem da internet, e um formato que não se reconhece não pode derrubar o
    /// processamento da mensagem — que é o que faz o lead existir.</summary>
    public static AnuncioDoWhatsapp? Ler(string? payloadCru)
    {
        if (string.IsNullOrWhiteSpace(payloadCru)) return null;

        try
        {
            var raiz = JsonNode.Parse(payloadCru)?.AsObject();
            if (raiz is null) return null;

            var bloco = Procurar(raiz, 0);
            if (bloco is null) return null;

            // Cada campo com o teto da COLUNA onde ele vai morar — os mesmos de `RastreioLead`.
            var anuncio = new AnuncioDoWhatsapp(
                CtwaClid: RegrasRastreio.Cortar(
                    Texto(bloco, "ctwaClid", "ctwa_clid"), RastreioLead.TetoIdentificador),
                AnuncioId: RegrasRastreio.Cortar(
                    Texto(bloco, "sourceId", "source_id"), RastreioLead.TetoUtm),
                // Sem query string, pela mesma razão do rastro do site: é URL de terceiro, e não
                // temos como auditar o que vai nela.
                Url: RegrasRastreio.Cortar(
                    RegrasRastreio.SemQuery(Texto(bloco, "sourceUrl", "source_url")),
                    RastreioLead.TetoUrl),
                Titulo: RegrasRastreio.Cortar(
                    Texto(bloco, "title", "headline"), RastreioLead.TetoUtm));

            return anuncio.TemAlgo ? anuncio : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>O primeiro objeto cuja CHAVE é `externalAdReply` ou `referral`, em qualquer
    /// profundidade até o teto.
    ///
    /// ⚠️ ENTRA EM ARRAY TAMBÉM, e um teste mostrou por quê: na API oficial do WhatsApp Cloud o
    /// `referral` mora dentro de `entry[].changes[].value.messages[]` — três arrays no caminho. Sem
    /// isso, a metade "oficial" deste leitor não achava nada.</summary>
    private static JsonObject? Procurar(JsonNode? no, int nivel)
    {
        if (nivel > ProfundidadeMaxima) return null;

        switch (no)
        {
            case JsonObject objeto:
                foreach (var (chave, valor) in objeto)
                {
                    if (valor is JsonObject filho && Blocos.Contains(chave)) return filho;
                    if (Procurar(valor, nivel + 1) is { } achado) return achado;
                }
                return null;

            case JsonArray lista:
                foreach (var item in lista)
                    if (Procurar(item, nivel + 1) is { } achado) return achado;
                return null;

            default:
                return null;
        }
    }

    /// <summary>O primeiro dos nomes que existir, como texto não vazio.</summary>
    private static string? Texto(JsonObject objeto, params string[] nomes)
    {
        foreach (var nome in nomes)
        {
            if (objeto[nome] is not JsonValue valor) continue;
            if (valor.GetValueKind() != JsonValueKind.String) continue;

            var texto = valor.GetValue<string>().Trim();
            if (texto.Length > 0) return texto;
        }

        return null;
    }
}
