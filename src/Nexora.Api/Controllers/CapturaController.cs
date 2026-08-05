using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Seguranca;
using Nexora.Core;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Captação por formulário do site do cliente. PÚBLICO — o visitante não tem sessão.
///
/// ===================== A RESPOSTA É SEMPRE A MESMA =====================
/// 200 com o mesmo corpo para lead criado, telefone repetido e honeypot. O visitante do site do
/// cliente não pode aprender nada sobre a base: se a resposta distinguisse "criado" de "já
/// existia", o formulário viraria um verificador de clientes — bastaria testar telefones para
/// descobrir quem é cliente de quem.
///
/// A distinção existe no LOG e no retorno do serviço, que é onde ela é útil.
/// =======================================================================
///
/// Erro de validação (nome curto, telefone inválido, origem não permitida, chave desconhecida)
/// devolve 400 pelo `FiltroRegraDeNegocio`, porque aí o formulário PRECISA mostrar algo ao
/// visitante — ele digitou errado e pode corrigir.</summary>
[ApiController]
[Route("api/captura")]
[AllowAnonymous]
// A política do painel tem lista fixa de origens e barraria o site de todo cliente novo. Esta
// aceita qualquer origem — o que recusa a origem errada é a checagem no serviço, no servidor,
// não o navegador. Ver o bloco de comentário na declaração da política, em `Program.cs`.
[EnableCors(RateLimitingConfig.PolCaptura)]
public class CapturaController(
    IServicoCaptura servico,
    ILogger<CapturaController> log) : ControllerBase
{
    [HttpPost("{chave}")]
    [EnableRateLimiting(RateLimitingConfig.PolCaptura)]
    public async Task<IActionResult> Receber(
        string chave, [FromBody] LeadDoFormulario lead, CancellationToken ct)
    {
        // O cabeçalho `Origin` é posto pelo NAVEGADOR e não pode ser alterado por JavaScript da
        // página — é o que dá algum valor à checagem de domínio. Ausente = não há navegador na
        // frente (curl, servidor, app), e o serviço trata esse caso.
        var origem = Request.Headers.Origin.ToString();

        var resultado = await servico.ReceberAsync(
            chave, lead, string.IsNullOrWhiteSpace(origem) ? null : origem, ct);

        log.LogInformation("Captura processada: {Resultado}.", resultado);

        // MESMO corpo nos três casos — inclusive no honeypot. Bot que recebe erro tenta de novo
        // com outra variação; bot que recebe sucesso risca o alvo da lista.
        return Ok(new { recebido = true, mensagem = "Recebemos seu contato. Falaremos com você em breve." });
    }
}

/// <summary>Os formulários de captação, na área logada. Só o DONO configura: a chave gerada aqui
/// abre um endpoint de escrita na internet.</summary>
[ApiController]
[Route("api/formularios")]
[Authorize(Roles = "dono")]
public class FormulariosController(IServicoFormularios servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) =>
        Ok(await servico.ListarAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] NovoFormulario novo, CancellationToken ct) =>
        Ok(new { id = await servico.CriarAsync(novo, ct) });

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Atualizar(
        long id, [FromBody] NovoFormulario dados, CancellationToken ct)
    {
        await servico.AtualizarAsync(id, dados, ct);
        return NoContent();
    }

    [HttpPost("{id:long}/ativo")]
    public async Task<IActionResult> Alternar(
        long id, [FromBody] AlternarFormulario corpo, CancellationToken ct)
    {
        await servico.AlternarAtivoAsync(id, corpo.Ativo, ct);
        return NoContent();
    }

    /// <summary>Regera a chave. A antiga para de funcionar NA HORA — é o ponto de existir: quem
    /// regera está reagindo a um vazamento. O HTML no site do cliente precisa ser trocado.</summary>
    [HttpPost("{id:long}/chave")]
    public async Task<IActionResult> Regerar(long id, CancellationToken ct) =>
        Ok(new { chave = await servico.RegerarChaveAsync(id, ct) });
}

public record AlternarFormulario(bool Ativo);
