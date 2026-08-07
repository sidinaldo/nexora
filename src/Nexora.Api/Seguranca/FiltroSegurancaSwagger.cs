using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Nexora.Api.Seguranca;

/// <summary>Marca "precisa de token" SÓ nas operações que precisam.
///
/// ===================== O QUE ESTAVA ERRADO =====================
/// `AddSecurityRequirement` é GLOBAL: ele carimbava a exigência em toda operação do documento,
/// inclusive `POST /api/auth/login` — o endpoint cuja razão de existir é justamente não ter token
/// ainda. O mesmo valia para a captação do formulário do site, o webhook da Evolution, o aceite
/// de convite e a redefinição de senha.
///
/// Na tela do Swagger isso era cadeado em rota aberta: irritante, mas cosmético. O custo real é
/// que o documento é CONSUMIDO — a coleção do Bruno é gerada a partir dele, e um cliente que
/// mande `Authorization: Bearer` no login está mandando cabeçalho que ainda não existe. Documento
/// que mente sobre autenticação faz quem lê acertar por acidente e errar por confiança.
/// ===============================================================
///
/// ===================== POR QUE POR OPERAÇÃO, E NÃO LIMPANDO A GLOBAL =====================
/// A primeira tentativa foi manter a exigência global e zerar (`Security = []`) o que fosse
/// anônimo. Não funciona, e o motivo é do serializador: o escritor do Microsoft.OpenApi OMITE
/// coleção vazia, então `security: []` — que no OpenAPI significa "esta operação dispensa a
/// exigência global" — some do JSON e vira indistinguível de "não declarei nada". A global
/// voltava a valer, e as 97 operações continuavam pedindo token.
///
/// Sem global, cada operação protegida declara a sua. O documento fica mais verboso e deixa de
/// ser ambíguo.
/// =========================================================================================
///
/// ===================== A FONTE DA VERDADE SÃO OS ATRIBUTOS =====================
/// `[Authorize]` no método ou no controller exige; `[AllowAnonymous]` no método dispensa. São os
/// MESMOS atributos que o pipeline lê em tempo de execução — e é por isso que não há lista de
/// rotas públicas mantida à mão aqui. Uma lista dessas se desatualiza no primeiro endpoint novo,
/// e o erro seria silencioso nos dois sentidos.
///
/// ⚠️ Controller sem `[Authorize]` nenhum é ANÔNIMO — `AddAuthorization()` roda sem política de
/// fallback. É o caso de `AuthController` e `WebhookController`, que não declaram nada.
/// ===============================================================================</summary>
public class FiltroSegurancaSwagger : IOperationFilter
{
    private static readonly OpenApiSecurityRequirement ExigeBearer = new()
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
        }] = []
    };

    public void Apply(OpenApiOperation operacao, OperationFilterContext contexto)
    {
        var metodo = contexto.MethodInfo;
        var controller = metodo.DeclaringType;

        // `[AllowAnonymous]` NO MÉTODO vence qualquer `[Authorize]` do controller — é assim que o
        // pipeline resolve, e o documento tem que dizer a mesma coisa.
        if (metodo.GetCustomAttributes(true).OfType<IAllowAnonymous>().Any()) return;

        var exige = metodo.GetCustomAttributes(true).OfType<IAuthorizeData>().Any()
            || (controller?.GetCustomAttributes(true).OfType<IAuthorizeData>().Any() ?? false);

        if (exige) operacao.Security = [ExigeBearer];
    }
}
