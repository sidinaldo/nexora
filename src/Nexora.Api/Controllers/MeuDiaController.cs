using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A tela de abrir o sistema: o que fazer hoje, sem tabela nova.</summary>
[ApiController]
[Route("api/meu-dia")]
[Authorize]
public class MeuDiaController(IServicoMeuDia servico) : ControllerBase
{
    /// <summary>`limite` corta a LISTA; os contadores da resposta continuam sendo o total.
    ///
    /// O padrão é o teto (200) para quem não passa nada — a tela Meu Dia. O cartão do dashboard
    /// pede 6, que é o que ele mostra: antes ele baixava tudo e jogava fora com `.slice`.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? limite, CancellationToken ct) =>
        Ok(await servico.MeuDiaAsync(limite ?? LimiteMeuDia.Maximo, ct));

    /// <summary>Uma página de uma aba do Meu Dia, com as contagens de todas (AUD-XX). É a tela do
    /// Meu Dia que chama; o cartão do dashboard continua no `GET` acima.</summary>
    [HttpGet("pagina")]
    public async Task<IActionResult> Pagina(
        [FromQuery] string? filtro,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanho = 20,
        CancellationToken ct = default)
    {
        var aba = FiltroDoDia.Todas;
        if (!string.IsNullOrWhiteSpace(filtro))
        {
            // O `TryParse` aceita "2" e devolve a terceira aba — o número é recusado à parte.
            if (int.TryParse(filtro, out _)
                || !Enum.TryParse(filtro, ignoreCase: true, out aba)
                || !Enum.IsDefined(aba))
            {
                return BadRequest(new { erro = $"Aba inválida: \"{filtro}\". Use todas, responder, lembrete ou atrasadas." });
            }
        }

        return Ok(await servico.PaginaAsync(aba, pagina, tamanho, ct));
    }
}
