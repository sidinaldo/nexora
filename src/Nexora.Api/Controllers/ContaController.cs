using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A própria conta. `[Authorize]` simples: qualquer papel mexe na SUA conta, e só na
/// sua — o serviço usa o id do contexto, ninguém passa id de outro.</summary>
[ApiController]
[Route("api/conta")]
[Authorize]
public class ContaController(IServicoEquipe servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Minha(CancellationToken ct) =>
        Ok(await servico.MinhaContaAsync(ct));

    /// <summary>Não recebe id: o alvo é sempre o usuário do contexto. É o que permite esta rota
    /// ser `[Authorize]` simples — não há como um vendedor editar a conta de outro.</summary>
    [HttpPut]
    public async Task<IActionResult> Atualizar([FromBody] EditarMinhaConta dados, CancellationToken ct)
    {
        await servico.AtualizarMinhaContaAsync(dados, ct);
        return NoContent();
    }

    [HttpPost("senha")]
    public async Task<IActionResult> TrocarSenha([FromBody] TrocarSenhaRequest req, CancellationToken ct)
    {
        await servico.TrocarMinhaSenhaAsync(req.SenhaAtual, req.SenhaNova, ct);
        return NoContent();
    }
}

public record TrocarSenhaRequest(string SenhaAtual, string SenhaNova);
