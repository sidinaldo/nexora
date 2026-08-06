using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Os canais de captação por QR Code e link. Só o DONO: decidir por qual número um
/// panfleto atende é configuração, não atendimento.
///
/// O QR sai daqui DESENHADO (SVG e PNG), e não como uma URL para o navegador resolver: gerar no
/// servidor é o que mantém o número de WhatsApp do cliente longe da infraestrutura de terceiros e
/// o material impresso independente da disponibilidade de alguém.</summary>
[ApiController]
[Route("api/canais")]
[Authorize(Roles = "dono")]
public class CanaisController(IServicoCanais servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) =>
        Ok(await servico.ListarAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] NovoCanal novo, CancellationToken ct) =>
        Ok(new { id = await servico.CriarAsync(novo, ct) });

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Atualizar(long id, [FromBody] NovoCanal dados, CancellationToken ct)
    {
        await servico.AtualizarAsync(id, dados, ct);
        return NoContent();
    }

    [HttpPost("{id:long}/ativo")]
    public async Task<IActionResult> Alternar(
        long id, [FromBody] AlternarCanal corpo, CancellationToken ct)
    {
        await servico.AlternarAtivoAsync(id, corpo.Ativo, ct);
        return NoContent();
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Remover(long id, CancellationToken ct)
    {
        await servico.RemoverAsync(id, ct);
        return NoContent();
    }

    /// <summary>O QR em SVG — o formato que IMPORTA. Panfleto e placa são impressos em tamanho que
    /// nenhum PNG de tela aguenta, e QR pixelado não escaneia.
    ///
    /// `Content-Disposition: attachment` com nome de arquivo: o navegador baixa em vez de abrir o
    /// SVG como página, e o arquivo chega com o nome do canal em vez de `download.svg`.</summary>
    [HttpGet("{id:long}/qr.svg")]
    public async Task<IActionResult> Svg(long id, CancellationToken ct)
    {
        if (await servico.SvgAsync(id, ct) is not { } qr) return NotFound(new { erro = "Canal não encontrado." });
        return File(System.Text.Encoding.UTF8.GetBytes(qr.Svg), "image/svg+xml", qr.NomeArquivo);
    }

    /// <summary>O QR em PNG, para post, story e apresentação — onde SVG não entra.</summary>
    [HttpGet("{id:long}/qr.png")]
    public async Task<IActionResult> Png(long id, CancellationToken ct)
    {
        if (await servico.PngAsync(id, ct) is not { } qr) return NotFound(new { erro = "Canal não encontrado." });
        return File(qr.Png, "image/png", qr.NomeArquivo);
    }
}

public record AlternarCanal(bool Ativo);
