using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>A importação do CSV do Meta Lead Ads (INT-XX). Ver `IServicoImportacaoMeta`.
///
/// A recusa por papel vem do serviço (dono ou gestor), como na importação da issue #8 — a regra mora
/// num lugar só, e o controller não repete.</summary>
[ApiController]
[Route("api/importacoes")]
[Authorize]
public class ImportacoesController(IServicoImportacaoMeta servico) : ControllerBase
{
    /// <summary>Recebe o arquivo e guarda as linhas cruas. NADA vira contato aqui.
    ///
    /// ⚠️ `RequestSizeLimit` COM FOLGA sobre os 10 MB do serviço, pelo mesmo motivo de `EnviarMidia`:
    /// o teto do serviço é do CONTEÚDO, e o multipart carrega envelope por cima. Sem a folga o
    /// servidor cortaria antes, e a recusa viria como erro de protocolo em vez da mensagem que diz
    /// para dividir o export.</summary>
    [HttpPost]
    [RequestSizeLimit(IServicoImportacaoMeta.MaximoBytes + 256 * 1024)]
    public async Task<IActionResult> Receber(IFormFile arquivo, CancellationToken ct)
    {
        if (arquivo is null || arquivo.Length == 0)
            return BadRequest(new { erro = "Escolha o arquivo .csv exportado do Gerenciador de Leads." });

        using var memoria = new MemoryStream();
        await arquivo.CopyToAsync(memoria, ct);

        return Ok(await servico.ReceberAsync(arquivo.FileName, memoria.ToArray(), ct));
    }

    /// <summary>Aplica um mapeamento e mostra o que vai acontecer. NADA vira contato aqui.</summary>
    [HttpPost("{id:long}/previa")]
    public async Task<IActionResult> Previa(
        long id, [FromBody] PedidoPrevia pedido, CancellationToken ct) =>
        Ok(await servico.PreverAsync(id, pedido.Mapeamento, ct));

    public record PedidoPrevia(IReadOnlyList<ColunaMapeada> Mapeamento);
}
