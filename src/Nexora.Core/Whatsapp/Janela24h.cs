using Nexora.Core.Entidades;

namespace Nexora.Core.Whatsapp;

/// <summary>===================== A JANELA DE 24H DA META (INT-XX) =====================
///
/// Na Cloud API, texto livre so pode sair nas 24 horas depois da ultima mensagem do cliente.
/// Fora disso, so template aprovado. A Meta recusa o envio (erro 131047) — entao a regra e
/// aplicada ANTES, aqui, e a tela avisa antes de o vendedor escrever.
///
/// ⚠️ NAO E A `JanelaAtendimento`. Aquela e o horario comercial da empresa, e ja se chama
/// "janela" no codigo e na tela. Esta e da Meta, e na tela aparece como "janela do WhatsApp".
///
/// ⚠️ AS 72 HORAS DE ANUNCIO NAO ENTRAM. A "free entry point window" do clique em anuncio e
/// sobre PRECO (as mensagens saem gratis), nao sobre poder mandar texto livre — que continua
/// preso as 24h. Medido na documentacao da Meta antes de escrever.
///
/// Na Evolution nada disso bloqueia: a janela aparece como boa pratica de tempo de resposta.
/// ================================================================================</summary>
public static class Janela24h
{
    public static readonly TimeSpan Duracao = TimeSpan.FromHours(24);

    /// <summary>Quanto antes de fechar a tela passa a avisar.</summary>
    public static readonly TimeSpan Aviso = TimeSpan.FromHours(2);

    /// <summary>Quando a janela fecha. Nulo = o cliente nunca escreveu para este numero, e ela
    /// nunca abriu.</summary>
    public static DateTime? FechaEm(DateTime? ultimaEntrada)
    {
        if (ultimaEntrada == null) return null;
        return ultimaEntrada.Value + Duracao;
    }

    /// <summary>A partir de quando a tela mostra "fechando".</summary>
    public static DateTime? AvisoEm(DateTime? ultimaEntrada)
    {
        var fecha = FechaEm(ultimaEntrada);
        if (fecha == null) return null;
        return fecha.Value - Aviso;
    }

    public static bool Aberta(DateTime? ultimaEntrada, DateTime agora)
    {
        var fecha = FechaEm(ultimaEntrada);
        if (fecha == null) return false;
        return agora < fecha.Value;
    }

    /// <summary>Se o canal prende o texto livre a janela. So a Cloud API.</summary>
    public static bool Bloqueia(CanalWhatsapp canal)
    {
        return canal == CanalWhatsapp.CloudApi;
    }

    /// <summary>A pergunta que o envio faz: pode sair texto livre agora?</summary>
    public static bool PermiteTextoLivre(CanalWhatsapp canal, DateTime? ultimaEntrada, DateTime agora)
    {
        if (!Bloqueia(canal)) return true;
        return Aberta(ultimaEntrada, agora);
    }
}
