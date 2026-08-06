using System.Security.Cryptography;
using System.Text;

namespace Nexora.Core.Webhooks;

/// <summary>A ASSINATURA da entrega — HMAC-SHA256 do corpo com o segredo da empresa.
///
/// ===================== POR QUE ISTO EXISTE, E O CONTRASTE =====================
/// A Evolution NÃO assina o que manda para o Nexora, e é por isso que a autenticação de entrada
/// depende de um segredo na query string — coisa que vaza em log de proxy, em histórico de
/// navegador e em `Referer`. Isso é limitação dela, não modelo a seguir.
///
/// Na saída fazemos o que ela não faz: o receptor consegue provar que o corpo veio do Nexora e
/// que não foi alterado no caminho. Sem assinatura, qualquer um que descubra a URL do cliente
/// consegue inventar "venda fechada, R$ 40.000" no ERP dele.
/// ==============================================================================
///
/// ===================== O TIMESTAMP ENTRA NA ASSINATURA =====================
/// Assinar só o corpo deixa o replay aberto: quem capturou uma entrega válida pode reenviá-la
/// amanhã com qualquer timestamp, e a assinatura continua conferindo. Assinando
/// `{timestamp}.{corpo}`, o par fica amarrado — trocar o timestamp invalida.
///
/// Quem recusa o replay é o RECEPTOR (janela de tolerância dele, ex. 5 minutos). O que o Nexora
/// faz é dar a ele um timestamp em que dá para confiar. A tela documenta as duas metades, porque
/// assinatura que ninguém valida é enfeite.
/// ===========================================================================</summary>
public static class AssinaturaWebhook
{
    /// <summary>O prefixo declara o algoritmo. Custa 7 caracteres e é o que permite trocar de
    /// algoritmo um dia sem quebrar quem valida — ele lê o prefixo em vez de assumir.</summary>
    public const string Prefixo = "sha256=";

    public const string HeaderAssinatura = "X-Nexora-Assinatura";
    public const string HeaderTimestamp = "X-Nexora-Timestamp";
    public const string HeaderEvento = "X-Nexora-Evento";
    public const string HeaderEntrega = "X-Nexora-Entrega";

    /// <summary>O que é assinado: `{timestamp}.{corpo}`, em UTF-8.</summary>
    public static string BaseAssinada(long timestamp, string corpo) => $"{timestamp}.{corpo}";

    public static string Calcular(string segredo, long timestamp, string corpo)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(segredo));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(BaseAssinada(timestamp, corpo)));
        return Prefixo + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Confere em tempo CONSTANTE.
    ///
    /// Não é paranoia acadêmica: comparação que sai no primeiro byte diferente vaza, pelo relógio,
    /// quantos bytes iniciais estavam certos — e com isso a assinatura se descobre byte a byte.
    /// `FixedTimeEquals` existe na BCL exatamente para isto.
    ///
    /// O Nexora não valida assinatura de webhook de saída (quem valida é o receptor); isto existe
    /// para o TESTE conferir do jeito certo e para servir de referência ao que a tela documenta.</summary>
    public static bool Confere(string segredo, long timestamp, string corpo, string? assinaturaRecebida)
    {
        if (string.IsNullOrEmpty(assinaturaRecebida)) return false;

        var esperada = Encoding.UTF8.GetBytes(Calcular(segredo, timestamp, corpo));
        var recebida = Encoding.UTF8.GetBytes(assinaturaRecebida);

        return esperada.Length == recebida.Length
            && CryptographicOperations.FixedTimeEquals(esperada, recebida);
    }

    /// <summary>32 bytes em hex, de RNG criptográfico. É a chave de um HMAC — `Guid` não serve:
    /// ele tem estrutura e menos entropia do que aparenta.</summary>
    public static string GerarSegredo() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
