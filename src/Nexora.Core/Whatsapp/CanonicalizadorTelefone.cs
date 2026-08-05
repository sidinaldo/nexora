namespace Nexora.Core.Whatsapp;

/// <summary>Normaliza telefone para o formato do WhatsApp: 55 + DDD + numero, so digitos.
///
/// ISTO E LOAD-BEARING. O cadastro digita "(84) 98888-7777" (sem DDI), e o WhatsApp entrega
/// "5584988887777@s.whatsapp.net" (com DDI). Se os dois lados nao canonicalizarem igual, a
/// mensagem recebida NAO casa com ninguem: sem conversa, sem contato, sem badge — e sem erro
/// nenhum no log. Falha em silencio.
///
/// Extraido do RenderizadorTemplate do Recupera. Fica numa classe propria de proposito: o
/// resto daquele arquivo (Renderizar, com {{credor}}, {{vencimento}}, {{dias_atraso}}) e
/// vocabulario de cobranca e nao tem lugar aqui.</summary>
public static class CanonicalizadorTelefone
{
    /// <summary>Canonicaliza para 55 + DDD + numero. Devolve so digitos.</summary>
    public static string Canonicalizar(string telefone)
    {
        var digitos = new string([.. (telefone ?? "").Where(char.IsDigit)]).TrimStart('0');

        // 10 = DDD + 8 digitos (fixo/celular antigo); 11 = DDD + 9 digitos (celular atual).
        // Nos dois casos veio sem DDI — e assim que a pessoa digita no cadastro.
        if (digitos.Length is 10 or 11)
            digitos = "55" + digitos;

        return digitos;
    }

    /// <summary>Variantes do numero para CASAR a mensagem recebida com o contato.
    ///
    /// O WhatsApp as vezes entrega o JID SEM o nono digito (numeros habilitados antes da
    /// mudanca de 2012). Entao 5584988887777 e 558488887777 podem ser a MESMA pessoa. Procurar
    /// so pela forma exata perde a mensagem do contato.</summary>
    public static IReadOnlyList<string> Variantes(string telefone)
    {
        var c = Canonicalizar(telefone);
        var variantes = new List<string> { c };

        if (!c.StartsWith("55") || c.Length < 12) return variantes;

        var ddd = c[2..4];
        var numero = c[4..];

        if (numero.Length == 9 && numero[0] == '9')
            variantes.Add($"55{ddd}{numero[1..]}");        // tira o nono digito
        else if (numero.Length == 8)
            variantes.Add($"55{ddd}9{numero}");            // poe o nono digito

        return variantes;
    }

    /// <summary>Um numero canonico plausivel? 55 + DDD(2) + 8 ou 9 digitos = 12 ou 13.
    ///
    /// Serve para falhar ALTO no cadastro, em vez de aceitar lixo que depois nunca casa com
    /// mensagem nenhuma — o modo de falha mais caro deste desenho e justamente o silencioso.</summary>
    public static bool EhValido(string? telefone)
    {
        if (string.IsNullOrWhiteSpace(telefone)) return false;
        var c = Canonicalizar(telefone);
        return c.Length is 12 or 13 && c.StartsWith("55");
    }

    /// <summary>Formata para exibicao: (84) 98888-7777. Usado como NOME do contato quando o
    /// WhatsApp nao manda pushName — melhor que deixar em branco numa coluna NOT NULL.</summary>
    public static string Formatar(string telefone)
    {
        var c = Canonicalizar(telefone);
        if (!c.StartsWith("55") || c.Length is not (12 or 13)) return c;

        var ddd = c[2..4];
        var numero = c[4..];
        return numero.Length == 9
            ? $"({ddd}) {numero[..5]}-{numero[5..]}"
            : $"({ddd}) {numero[..4]}-{numero[4..]}";
    }
}
