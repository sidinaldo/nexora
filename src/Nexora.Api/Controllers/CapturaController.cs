using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Seguranca;
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
        var userAgent = Request.Headers.UserAgent.ToString();

        // ===================== IP E USER-AGENT SAEM DAQUI, NÃO DO CORPO (INT-4) =====================
        // A Meta usa os dois para casar a pessoa que clicou no anúncio com a que virou lead. Vêm
        // da CONEXÃO de propósito: o corpo é escrito pelo JavaScript da página e diria qualquer
        // coisa, inclusive o IP de outra pessoa.
        //
        // `RemoteIpAddress` já vem corrigido pelo `UseForwardedHeaders` quando o sistema roda atrás
        // de proxy (`ConfiarProxyReverso`) — o mesmo IP que o rate limiter usa.
        //
        // Faltar qualquer um dos dois NÃO recusa o lead: o casamento por telefone e e-mail funciona
        // sozinho, e IP ausente é só atribuição um pouco pior.
        // ===========================================================================================
        var conexao = new DadosDaConexao(
            Origem: string.IsNullOrWhiteSpace(origem) ? null : origem,
            Ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent: string.IsNullOrWhiteSpace(userAgent) ? null : userAgent);

        var resultado = await servico.ReceberAsync(chave, lead, conexao, ct);

        log.LogInformation("Captura processada: {Resultado}.", resultado);

        // MESMO corpo nos três casos — inclusive no honeypot. Bot que recebe erro tenta de novo
        // com outra variação; bot que recebe sucesso risca o alvo da lista.
        return Ok(new { recebido = true, mensagem = "Recebemos seu contato. Falaremos com você em breve." });
    }
}
