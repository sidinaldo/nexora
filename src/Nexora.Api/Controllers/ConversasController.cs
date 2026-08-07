using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;

namespace Nexora.Api.Controllers;

/// <summary>O caminho quente do vendedor: responder na conversa.
///
/// Qualquer papel atende — atendimento e o trabalho do vendedor, nao configuracao.</summary>
[ApiController]
[Route("api/conversas")]
[Authorize]
public class ConversasController(IServicoConversas servico, IServicoCaixa caixa) : ControllerBase
{
    /// <summary>A lista da caixa, paginada por CURSOR (cursorEm = ultima_mensagem_em +
    /// cursorId = id do último item). Nulos = primeira página.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] FiltroConversa filtro = FiltroConversa.Aguardando,
        [FromQuery] string? busca = null,
        [FromQuery] DateTime? cursorEm = null,
        [FromQuery] long? cursorId = null,
        [FromQuery] int tamanho = 30,
        CancellationToken ct = default) =>
        Ok(await caixa.ConversasAsync(filtro, busca, cursorEm, cursorId, tamanho, ct));

    /// <summary>UMA conversa, pelo id.
    ///
    /// ===================== POR QUE ESTA ROTA EXISTE =====================
    /// A lista é por CURSOR e o cliente carrega só a primeira página. O Meu Dia manda o vendedor
    /// direto para uma conversa (`/caixa?conversa=N`); se ela estiver na página 4, não havia o
    /// que selecionar e a tela abria vazia — sem erro e sem explicação.
    ///
    /// 404 tanto para inexistente quanto para conversa de OUTRA empresa: o serviço devolve null
    /// nos dois casos, pelo query filter. Distinguir contaria a quem sonda que a conversa existe
    /// noutro tenant.
    /// ==================================================================== */</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Obter(long id, CancellationToken ct) =>
        await caixa.ConversaAsync(id, ct) is { } conversa
            ? Ok(conversa)
            : NotFound(new { erro = "Conversa não encontrada." });

    /// <summary>A thread, por cursor: as `tamanho` mensagens mais novas antes de `antes`
    /// (null = as últimas).</summary>
    [HttpGet("{id:long}/mensagens")]
    public async Task<IActionResult> Mensagens(
        long id, [FromQuery] long? antes = null, [FromQuery] int tamanho = 30,
        CancellationToken ct = default) =>
        Ok(await caixa.MensagensAsync(id, antes, tamanho, ct));

    /// <summary>Zera o contador de não lidas. NÃO mexe no semáforo: ler não é responder.</summary>
    [HttpPost("{id:long}/lida")]
    public async Task<IActionResult> MarcarLida(long id, CancellationToken ct)
    {
        await caixa.MarcarLidaAsync(id, ct);
        return NoContent();
    }

    /// <summary>Responder. Registrar a mensagem e SUCESSO; a entrega ao WhatsApp falhar e um
    /// detalhe (`enviada: false`) — a linha fica gravada com o erro e a tela a mostra como
    /// "não chegou". Devolver 502 aqui esconderia o id que a tela precisa para o balão.</summary>
    [HttpPost("{id:long}/responder")]
    public async Task<IActionResult> Responder(long id, [FromBody] ResponderRequest req, CancellationToken ct) =>
        Ok(await servico.ResponderAsync(id, req.Texto, ct));

    /// <summary>Assume a conversa (vira o dono). 409 se já for de outro vendedor.</summary>
    /// <summary>Envia imagem ou PDF (MID-1). `multipart/form-data`, um arquivo por vez.
    ///
    /// ===================== O TETO ANTES DE LER O CORPO =====================
    /// `RequestSizeLimit` recusa no pipeline, sem materializar o arquivo. Sem ele um upload de
    /// 500 MB seria lido inteiro para memoria antes de o servico dizer "grande demais" — e o
    /// teto viraria um convite a derrubar o processo.
    ///
    /// O numero e o MESMO de `ValidadorMidia`, com folga para o envelope do multipart (limites
    /// de borda, nome de arquivo, legenda). Duas constantes diferentes divergiriam.
    /// =======================================================================</summary>
    [HttpPost("{id:long}/midia")]
    [RequestSizeLimit(ValidadorMidia.TamanhoMaximoBytes + 1024 * 1024)]
    public async Task<IActionResult> EnviarMidia(
        long id, IFormFile arquivo, [FromForm] string? legenda, CancellationToken ct)
    {
        if (arquivo is null || arquivo.Length == 0)
            return BadRequest(new { erro = "Escolha um arquivo." });

        using var memoria = new MemoryStream();
        await arquivo.CopyToAsync(memoria, ct);

        // O ContentType do multipart vai junto so para registro: quem decide o tipo e o
        // conteudo, no servico. Ver AssinaturaArquivo.
        var pronto = new ArquivoParaEnvio(memoria.ToArray(), arquivo.FileName, arquivo.ContentType);

        return Ok(await servico.EnviarMidiaAsync(id, pronto, legenda, ct));
    }

    /// <summary>Nota de voz (bloco 13). Mesmo caminho do anexo, com a regra de formato própria
    /// do áudio — ver `AudioOpus`.</summary>
    [HttpPost("{id:long}/audio")]
    [RequestSizeLimit(ValidadorMidia.TamanhoMaximoBytes + 1024 * 1024)]
    public async Task<IActionResult> EnviarAudio(long id, IFormFile arquivo, CancellationToken ct)
    {
        if (arquivo is null || arquivo.Length == 0)
            return BadRequest(new { erro = "A gravação saiu vazia." });

        using var memoria = new MemoryStream();
        await arquivo.CopyToAsync(memoria, ct);

        return Ok(await servico.EnviarAudioAsync(
            id, new ArquivoParaEnvio(memoria.ToArray(), arquivo.FileName, arquivo.ContentType), ct));
    }

    /// <summary>Tentar de novo. REAPROVEITA a linha que falhou — nao cria outra.</summary>
    [HttpPost("mensagens/{mensagemId:long}/reenviar")]
    public async Task<IActionResult> Reenviar(long mensagemId, CancellationToken ct) =>
        Ok(await servico.ReenviarAsync(mensagemId, ct));

    [HttpPost("{id:long}/assumir")]
    public async Task<IActionResult> Assumir(long id, CancellationToken ct)
    {
        await servico.AssumirAsync(id, ct);
        return NoContent();
    }

    /// <summary>Devolve para "Não atribuídas".</summary>
    [HttpPost("{id:long}/liberar")]
    public async Task<IActionResult> Liberar(long id, CancellationToken ct)
    {
        await servico.LiberarAsync(id, ct);
        return NoContent();
    }
}

public record ResponderRequest(string Texto);
