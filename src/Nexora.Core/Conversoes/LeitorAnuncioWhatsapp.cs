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

/// <summary>O que a leitura encontrou, e o que ela VIU sem entender.
///
/// `BlocoAchado` separa os dois silêncios que hoje são o mesmo `null`: "não era anúncio" e "era
/// anúncio e eu não reconheci nada dentro". `Chaves` são os NOMES dos campos que vieram no bloco —
/// nunca os valores —, que é exatamente o que falta para consertar o leitor.</summary>
public record LeituraDeAnuncio(AnuncioDoWhatsapp? Anuncio, bool BlocoAchado, string[] Chaves)
{
    /// <summary>Não era anúncio: nem bloco havia. É a esmagadora maioria das mensagens, e por isso
    /// é uma instância só, sem alocação por mensagem.</summary>
    public static readonly LeituraDeAnuncio Nada = new(null, false, []);
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

    /// <summary>Quantos nomes de chave o diagnóstico carrega. O bloco real da Meta tem meia dúzia;
    /// doze dá folga e impede que um payload hostil vire uma linha de log de quilômetros.</summary>
    private const int MaximoDeChaves = 12;

    /// <summary>Acha a referência do anúncio, ou nulo.
    ///
    /// NUNCA lança: o payload vem da internet, e um formato que não se reconhece não pode derrubar o
    /// processamento da mensagem — que é o que faz o lead existir.</summary>
    public static AnuncioDoWhatsapp? Ler(string? payloadCru) => LerComDiagnostico(payloadCru).Anuncio;

    /// <summary>O mesmo que `Ler`, mais o que foi VISTO pelo caminho.
    ///
    /// ===================== POR QUE ISTO EXISTE =====================
    /// Os nomes que este leitor procura vieram da documentação da Meta, nunca de uma mensagem real.
    /// Se estiverem errados, o `Ler` devolve nulo e o chamador não tem como saber a diferença entre
    /// "não era anúncio" e "era anúncio e eu não entendi" — e a segunda passa despercebida para
    /// sempre, porque nada quebra: o lead entra, a venda fecha, só o elo com o anúncio se perde.
    ///
    /// Com `BlocoAchado` e `Chaves`, o primeiro clique real de QUALQUER cliente, em qualquer
    /// instalação, deixa os nomes verdadeiros registrados no log — no lugar de um anúncio pago só
    /// para descobrir isso.
    ///
    /// ⚠️ SÓ OS NOMES DAS CHAVES, nunca os valores. O achado #8 do `docs/SEGURANCA.md` proíbe corpo
    /// de payload em log, e lá o atenuante era "só acontece no caminho de erro". Este relatório sai
    /// no caminho NORMAL, então a régua é mais apertada, não mais frouxa.
    ///
    /// ⚠️ E AQUI SÓ MORA O FATO. Quem decide o que é digno de aviso é o processador, ao lado da
    /// linha de log que explica a decisão — este método não sabe o que é um `ctwa_clid` ausente.
    /// ===============================================================</summary>
    public static LeituraDeAnuncio LerComDiagnostico(string? payloadCru)
    {
        if (string.IsNullOrWhiteSpace(payloadCru)) return LeituraDeAnuncio.Nada;

        try
        {
            var raiz = JsonNode.Parse(payloadCru)?.AsObject();
            if (raiz is null) return LeituraDeAnuncio.Nada;

            var bloco = Procurar(raiz, 0);
            if (bloco is null) return LeituraDeAnuncio.Nada;

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

            return new LeituraDeAnuncio(anuncio.TemAlgo ? anuncio : null, BlocoAchado: true,
                                        Chaves: ChavesDe(bloco));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return LeituraDeAnuncio.Nada;
        }
    }

    /// <summary>Os nomes das chaves do bloco, ordenados, com teto.
    ///
    /// ⚠️ O TETO NÃO É ENFEITE: o payload vem da internet e o maior que já se viu neste banco tem
    /// 36 KB — sem o corte, uma linha de log viraria milhares de nomes. Ordenado porque o valor
    /// disto é comparar o que chegou com o que o leitor procura, e lista fora de ordem não se
    /// compara.</summary>
    private static string[] ChavesDe(JsonObject bloco) =>
        bloco.Select(par => par.Key).Order(StringComparer.Ordinal).Take(MaximoDeChaves).ToArray();

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
