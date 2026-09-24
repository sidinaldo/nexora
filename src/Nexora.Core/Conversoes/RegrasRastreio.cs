using System.Text.Json;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;

namespace Nexora.Core.Conversoes;

/// <summary>AS REGRAS PURAS DO RASTRO (INT-4): o que se guarda do que o navegador mandou.
///
/// Sem banco, sem rede. Estão aqui e não dentro do serviço porque é o caminho de um endpoint
/// PÚBLICO: o que entra é texto da internet, e cada regra abaixo é uma que, se faltar, aparece
/// como erro 500 no formulário do site do cliente — ou como dado pessoal guardado sem precisar.</summary>
public static class RegrasRastreio
{
    // Os nomes das chaves do `jsonb`. Constantes porque o montador escreve e o montador do evento
    // (INT-4, commit 5) lê: duas grafias diferentes de "fbclid" não dariam erro nenhum, só um
    // `user_data` silenciosamente vazio.
    public const string ChaveFbclid = "fbclid";
    public const string ChaveFbp = "fbp";
    public const string ChaveFbc = "fbc";
    public const string ChaveGclid = "gclid";
    public const string ChaveTtclid = "ttclid";

    /// <summary>O identificador do Clique-para-WhatsApp. Não vem do formulário; vem do payload da
    /// mensagem (commit 8). A chave vive aqui porque o leitor é o mesmo.</summary>
    public const string ChaveCtwaClid = "ctwa_clid";

    /// <summary>Corta nos tetos, tira a query string das URLs, e devolve nulo o que ficou vazio.
    ///
    /// ⚠️ NULO E VAZIO SÃO A MESMA COISA AQUI. O formulário manda campo oculto vazio quando não
    /// achou o parâmetro — `utm_source=""` é ausência, e guardar string vazia faria o relatório de
    /// campanha ter uma linha "" com metade dos leads dentro.</summary>
    public static RastreioDoSite Normalizar(this RastreioDoSite r) => new(
        UtmSource: Cortar(r.UtmSource, RastreioLead.TetoUtm),
        UtmMedium: Cortar(r.UtmMedium, RastreioLead.TetoUtm),
        UtmCampaign: Cortar(r.UtmCampaign, RastreioLead.TetoUtm),
        UtmContent: Cortar(r.UtmContent, RastreioLead.TetoUtm),
        UtmTerm: Cortar(r.UtmTerm, RastreioLead.TetoUtm),
        Pagina: Cortar(SemQuery(r.Pagina), RastreioLead.TetoUrl),
        Referencia: Cortar(SemQuery(r.Referencia), RastreioLead.TetoUrl),
        Fbclid: Cortar(r.Fbclid, RastreioLead.TetoIdentificador),
        Fbp: Cortar(r.Fbp, RastreioLead.TetoIdentificador),
        Fbc: Cortar(r.Fbc, RastreioLead.TetoIdentificador),
        Gclid: Cortar(r.Gclid, RastreioLead.TetoIdentificador),
        Ttclid: Cortar(r.Ttclid, RastreioLead.TetoIdentificador),
        EventoId: r.EventoId);

    /// <summary>Tem alguma coisa dentro? Rastro só com nulos não vira linha: uma tabela com
    /// milhares de linhas vazias é pior que a ausência delas, porque a tela passaria a mostrar
    /// "De onde veio" em branco para todo mundo.</summary>
    public static bool TemAlgo(this RastreioDoSite r) =>
        r.UtmSource is not null || r.UtmMedium is not null || r.UtmCampaign is not null
        || r.UtmContent is not null || r.UtmTerm is not null
        || r.Pagina is not null || r.Referencia is not null
        || r.Fbclid is not null || r.Fbp is not null || r.Fbc is not null
        || r.Gclid is not null || r.Ttclid is not null;

    /// <summary>Os identificadores de clique como o `jsonb` os guarda.
    ///
    /// Só as chaves que têm valor. Chave presente com `null` dentro obrigaria todo leitor a
    /// distinguir "não veio" de "veio vazio" — e não há diferença entre as duas.</summary>
    public static string Identificadores(this RastreioDoSite r) => Montar(
        (ChaveFbclid, r.Fbclid),
        (ChaveFbp, r.Fbp),
        (ChaveFbc, r.Fbc),
        (ChaveGclid, r.Gclid),
        (ChaveTtclid, r.Ttclid));

    /// <summary>Monta o `jsonb` a partir dos pares que têm valor.</summary>
    public static string Montar(params (string Chave, string? Valor)[] pares)
    {
        var mapa = new Dictionary<string, string>();
        foreach (var (chave, valor) in pares)
            if (!string.IsNullOrWhiteSpace(valor)) mapa[chave] = valor;

        return JsonSerializer.Serialize(mapa);
    }

    /// <summary>Lê de volta o que `Montar` escreveu. Nunca lança: o `jsonb` pode ter sido
    /// esvaziado pela anonimização, ou escrito à mão numa correção.</summary>
    public static IReadOnlyDictionary<string, string> Ler(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>A URL sem query string e sem fragmento.
    ///
    /// ===================== POR QUE JOGAR A QUERY FORA =====================
    /// É a URL do site DO CLIENTE, montada por ele, e não temos como auditar o que ele põe ali —
    /// já se viu e-mail, CPF e token de sessão em query string. Guardar tudo seria importar dado
    /// pessoal de terceiro sem saber que importamos, e a anonimização não teria como limpar o que
    /// não sabe que existe.
    ///
    /// E não se perde nada: o que o produto pergunta é "que página trouxe" e "de onde ela veio",
    /// e o caminho responde as duas. Os parâmetros que interessam (`utm_*`, `fbclid`) já vêm em
    /// campo próprio, lidos pelo formulário antes de postar.
    /// =====================================================================</summary>
    public static string? SemQuery(string? url)
    {
        var limpa = (url ?? "").Trim();
        if (limpa.Length == 0) return null;

        var corte = limpa.IndexOfAny(['?', '#']);
        limpa = corte < 0 ? limpa : limpa[..corte];

        return limpa.Length == 0 ? null : limpa;
    }

    /// <summary>Corta no teto e devolve nulo para o que ficou vazio.</summary>
    public static string? Cortar(string? texto, int teto)
    {
        var limpo = (texto ?? "").Trim();
        if (limpo.Length == 0) return null;
        return limpo.Length <= teto ? limpo : limpo[..teto];
    }
}
