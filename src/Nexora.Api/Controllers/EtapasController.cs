using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;
using Nexora.Core.Seguranca;

namespace Nexora.Api.Controllers;

/// <summary>Configuração do funil. Só o DONO: mudar as etapas muda como a empresa inteira lê o
/// próprio negócio, e a etapa de ganho define o que conta como venda no dashboard.
///
/// A LEITURA do quadro continua no `FunilController` — lá é operação diária, para qualquer papel.
/// Aqui é configuração.</summary>
[ApiController]
[Route("api/etapas")]
[Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]
public class EtapasController(IServicoEtapas servico, IServicoPipelines pipelines) : ControllerBase
{
    /// <summary>As etapas DE UMA pipeline.
    ///
    /// `pipeline` é opcional e cai na padrão — o que mantém um link antigo para `/api/etapas`
    /// abrindo um funil válido em vez de 400.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] long? pipeline = null, CancellationToken ct = default) =>
        Ok(await servico.ListarAsync(pipeline ?? await pipelines.PadraoAsync(ct), ct));

    /// <summary>⚠️ `pipeline` É OBRIGATÓRIO AQUI, ao contrário do GET acima.
    ///
    /// Ele caía na padrão como a leitura, e foi assim que a tela de configuração — que nunca leu
    /// o `:pipeline` da rota — passou MESES criando e renomeando etapas no funil errado. Quem
    /// abria "Pós-venda" via e editava as etapas de "Vendas", sem nenhum aviso.
    ///
    /// Uma LEITURA que cai na padrão confunde; uma ESCRITA que cai na padrão estraga dado de
    /// outro funil. Preferir 400 a adivinhar.</summary>
    [HttpPost]
    public async Task<IActionResult> Criar(
        [FromBody] NovaEtapa nova,
        [FromQuery] long? pipeline = null,
        CancellationToken ct = default)
    {
        if (pipeline is not { } id || id <= 0)
            return BadRequest(new { erro = "Informe em qual funil a etapa deve ser criada." });

        return Ok(new { id = await servico.CriarAsync(id, nova, ct) });
    }

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
    /// clique moveria a coluna duas casas.
    ///
    /// ⚠️ `pipeline` obrigatório pelo mesmo motivo do POST: reordenar caindo na padrão
    /// reescreveria a ordem das colunas de outro funil.</summary>
    [HttpPut("ordem")]
    public async Task<IActionResult> Reordenar(
        [FromBody] NovaOrdemEtapas corpo,
        [FromQuery] long? pipeline = null,
        CancellationToken ct = default)
    {
        if (pipeline is not { } id || id <= 0)
            return BadRequest(new { erro = "Informe de qual funil é a ordem." });

        await servico.ReordenarAsync(id, corpo.Ids ?? [], ct);
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
