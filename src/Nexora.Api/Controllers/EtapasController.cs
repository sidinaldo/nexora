using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Configuração do funil. Só o DONO: mudar as etapas muda como a empresa inteira lê o
/// próprio negócio, e a etapa de ganho define o que conta como venda no dashboard.
///
/// A LEITURA do quadro continua no `FunilController` — lá é operação diária, para qualquer papel.
/// Aqui é configuração.</summary>
[ApiController]
[Route("api/etapas")]
[Authorize(Roles = "dono")]
public class EtapasController(IServicoEtapas servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) =>
        Ok(await servico.ListarAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] NovaEtapa nova, CancellationToken ct) =>
        Ok(new { id = await servico.CriarAsync(nova, ct) });

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Atualizar(
        long id, [FromBody] EditarEtapa dados, CancellationToken ct)
    {
        await servico.AtualizarAsync(id, dados, ct);
        return NoContent();
    }

    /// <summary>Recebe a lista COMPLETA de ids na ordem desejada — não um "mover para cima".
    ///
    /// A tela sabe a ordem inteira e mandar tudo torna a operação idempotente: repetir a mesma
    /// requisição dá o mesmo resultado. Um "sobe uma posição" aplicado duas vezes por um duplo
    /// clique moveria a coluna duas casas.</summary>
    [HttpPut("ordem")]
    public async Task<IActionResult> Reordenar([FromBody] NovaOrdemEtapas corpo, CancellationToken ct)
    {
        await servico.ReordenarAsync(corpo.Ids ?? [], ct);
        return NoContent();
    }

    [HttpPost("{id:long}/ganho")]
    public async Task<IActionResult> DefinirGanho(long id, CancellationToken ct)
    {
        await servico.DefinirGanhoAsync(id, ct);
        return NoContent();
    }

    /// <summary>`destino` é obrigatório quando a etapa tem contatos. Vai na query string e não no
    /// corpo porque DELETE com corpo é mal suportado por proxy e cliente HTTP.</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Remover(
        long id, [FromQuery] long? destino, CancellationToken ct)
    {
        await servico.RemoverAsync(id, destino, ct);
        return NoContent();
    }
}

public record NovaOrdemEtapas(long[]? Ids);
