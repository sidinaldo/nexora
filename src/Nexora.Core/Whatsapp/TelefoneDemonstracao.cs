namespace Nexora.Core.Whatsapp;

/// <summary>A faixa de telefones do tenant de DEMONSTRAÇÃO.
///
/// ===================== O RISCO QUE ISTO EXISTE PARA IMPEDIR =====================
/// Contato de demonstração tem telefone, e o motor de follow-up manda mensagem para telefone.
/// Se o tenant de demonstração estiver pareado a uma instância real da Evolution, a rodada
/// dispara para números de ESTRANHOS — pessoas que nunca pediram nada, pelo WhatsApp, em nome
/// de uma empresa que elas não conhecem.
///
/// Não é um bug de tela. É mandar mensagem para gente de verdade.
/// ================================================================================
///
/// ===================== POR QUE DDD 00 =====================
/// O DDI é 55 e o DDD é `00`. Os DDDs brasileiros vão de 11 a 99 — `00` NÃO EXISTE e não pode
/// vir a existir sem uma remuneração do plano nacional inteiro. Um JID
/// `5500900000001@s.whatsapp.net` não corresponde a conta nenhuma, hoje nem depois.
///
/// A alternativa comum — um prefixo "não alocado" dentro de um DDD real, tipo (11) 90000-0000 —
/// é mais bonita na tela e MUITO pior aqui: "não alocado hoje" não é o mesmo que "impossível", e
/// blocos de numeração são alocados o tempo todo. Uma faixa que hoje não existe pode estar no
/// celular de alguém no ano que vem, e ninguém iria reavaliar esta escolha.
///
/// O efeito colateral é deliberado: `(00) 90000-0001` na tela é obviamente falso. Numa captura
/// de tela de demonstração, isso é honestidade, não defeito.
/// ==========================================================
///
/// Esta é a PRIMEIRA de três barreiras. As outras duas: `empresas.demonstracao` tira o tenant da
/// rodada do motor, e o `EnviadorMensagem` recusa o disparo. Uma sozinha não bastaria — a faixa
/// protege contra o motor, mas não contra alguém trocar o telefone de um contato à mão.</summary>
public static class TelefoneDemonstracao
{
    /// <summary>DDI 55 + DDD 00. Tudo que começa com isto é telefone de demonstração.</summary>
    public const string Prefixo = "5500";

    /// <summary>Número de demonstração do índice: `5500` + `9` + 8 dígitos = 13, que é o formato
    /// de celular canônico e passa pelo `CanonicalizadorTelefone.EhValido` — o seed grava pelo
    /// mesmo caminho que o cadastro, sem exceção para si mesmo.</summary>
    public static string Numero(int indice) => $"{Prefixo}9{indice:D8}";

    /// <summary>Este número é da faixa de demonstração?
    ///
    /// Função PURA e sem I/O de propósito: ela é chamada no caminho de envio, onde uma consulta
    /// a mais por mensagem pesaria, e onde falhar por indisponibilidade do banco não pode virar
    /// "então manda".</summary>
    public static bool EhDemonstracao(string? telefone) =>
        !string.IsNullOrWhiteSpace(telefone)
        && CanonicalizadorTelefone.Canonicalizar(telefone).StartsWith(Prefixo, StringComparison.Ordinal);
}
