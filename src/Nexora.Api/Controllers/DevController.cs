using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>FERRAMENTAS DE DESENVOLVIMENTO. Popula e limpa dados falsos.
///
/// ===================== DUAS TRAVAS, NÃO UMA =====================
/// 1. Só existe em **Development**: fora dele, toda ação devolve 404 — não 403. 403 confirmaria
///    que a rota existe; 404 não conta nada a quem estiver sondando.
/// 2. Só o **dono**: mesmo em desenvolvimento, semear apaga e recria dados do tenant.
///
/// A checagem de ambiente é em tempo de EXECUÇÃO, dentro de cada ação, e não só no registro do
/// serviço: controller é descoberto por convenção, e um registro condicional que alguém mexa
/// deixaria a rota viva sem ninguém notar. Duas travas independentes é o ponto.
/// ================================================================</summary>
[ApiController]
[Route("api/dev")]
[Authorize(Roles = "dono")]
public class DevController(
    IServicoSemente semente,
    IWebHostEnvironment ambiente) : ControllerBase
{
    /// <summary>Popula o tenant logado com o cenário completo: contatos nas cinco etapas,
    /// conversas em todas as faixas do semáforo, lembretes atrasados e de hoje, vendas do mês,
    /// equipe com papéis variados.
    ///
    /// LIMPA a semeadura anterior antes — pode rodar quantas vezes quiser.</summary>
    [HttpPost("semear")]
    public async Task<IActionResult> Semear(CancellationToken ct)
    {
        if (!ambiente.IsDevelopment()) return NotFound();
        return Ok(await semente.SemearAsync(ct));
    }

    /// <summary>Apaga SÓ o que foi semeado — o que está marcado com `semente-dev`. Contato
    /// digitado à mão não tem a marca e sobrevive.</summary>
    [HttpDelete("semear")]
    public async Task<IActionResult> Limpar(CancellationToken ct)
    {
        if (!ambiente.IsDevelopment()) return NotFound();
        return Ok(await semente.LimparAsync(ct));
    }
}
