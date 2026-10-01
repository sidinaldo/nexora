using System.Security.Cryptography;
using System.Text;

namespace Nexora.Api.Seguranca;

/// <summary>O portão das rotas de OPERAÇÃO — as que o operador do produto usa, e nenhum cliente.
///
/// ===================== POR QUE ISTO NÃO É UM FILTRO =====================
/// O caminho idiomático do ASP.NET seria um `IAuthorizationFilter` com `[ExigeChaveAdmin]`. Não é o
/// que está aqui, e a razão é concreta: **nenhum teste deste repositório passa pelo pipeline HTTP**
/// — não há `WebApplicationFactory` nem `TestServer` em lugar nenhum. Todo teste de controller
/// instancia a classe e chama o método, montando um `DefaultHttpContext` na mão.
///
/// Um filtro não roda nesse caminho. Mover a guarda para lá deixaria os testes que hoje afirmam
/// "chave errada é recusada" passando sem exercitar guarda nenhuma — verdes, e cegos.
///
/// Função estática resolve o mesmo problema que o filtro resolveria (uma cópia da comparação, em
/// vez de uma por controller) e continua testável direto, sem controller nenhum no meio.
/// =======================================================================
///
/// ⚠️ QUEM USA ISTO PRECISA DE `[EnableRateLimiting]` EXPLÍCITO. Rota anônima NÃO TEM limite neste
/// sistema: o limitador global devolve `NoLimiter("anon")` para quem não está autenticado (ver
/// `RateLimitingConfig`). Esquecer o atributo numa ação de operador deixa uma leitura de TODAS as
/// empresas destravada atrás de um segredo estático.</summary>
public static class ChaveAdmin
{
    public const string Cabecalho = "X-Chave-Admin";

    /// <summary>Comparação em TEMPO CONSTANTE. Diferente do segredo do webhook — que vai na URL e
    /// já aparece em log de proxy —, esta chave viaja em header e não é registrada em lugar nenhum.
    /// Comparar com `==` vazaria o prefixo correto pelo tempo de resposta.
    ///
    /// ⚠️ CHAVE CONFIGURADA VAZIA RECUSA SEMPRE, e é o padrão. Um clone do repositório que alguém
    /// suba sem configurar nada não pode ficar com criação de conta aberta na internet.</summary>
    public static bool Confere(HttpRequest requisicao, string? chaveConfigurada)
    {
        if (string.IsNullOrEmpty(chaveConfigurada)) return false;
        if (!requisicao.Headers.TryGetValue(Cabecalho, out var enviada)) return false;

        var esperada = Encoding.UTF8.GetBytes(chaveConfigurada);
        var recebida = Encoding.UTF8.GetBytes(enviada.ToString());

        return CryptographicOperations.FixedTimeEquals(esperada, recebida);
    }

    /// <summary>O corpo do 401, idêntico para chave ausente e para chave errada.
    ///
    /// Distinguir as duas confirmaria a quem está sondando que o endpoint existe e o que ele espera.
    /// Está aqui, e não repetido em cada controller, porque a tela do operador casa com este texto:
    /// mudá-lo num lugar e não no outro faria a tela mostrar o erro genérico em vez do específico.</summary>
    public static object CorpoRecusa() => new { erro = "Não autorizado." };
}
