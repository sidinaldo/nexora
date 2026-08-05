using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Configuração da empresa.
///
/// ===================== O ENFORCEMENT DE PAPEL ESTÁ AQUI =====================
/// LER é [Authorize] simples: o vendedor precisa saber que horas a empresa atende e quando o
/// semáforo acende — esconder isso dele não protege nada e atrapalha o trabalho.
///
/// ESCREVER é [Authorize(Roles="dono")]. Gestor não altera: mudar a janela de atendimento muda
/// quando o robô escreve para o cliente, e isso é decisão de quem responde pela empresa.
///
/// A checagem vive SÓ nesta camada. Repetir "é dono?" dentro do serviço criaria duas regras
/// para a mesma coisa, e elas divergem no dia em que uma muda.
/// ============================================================================</summary>
[ApiController]
[Route("api/configuracao")]
[Authorize]
public class ConfiguracaoController(IServicoConfiguracao servico) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Obter(CancellationToken ct) =>
        Ok(await servico.ObterAsync(ct));

    /// <summary>Os fusos que ESTE servidor conhece, com o offset atual.
    ///
    /// Endpoint próprio e não constante no cliente: a lista depende do tzdata do HOST, e um id
    /// que o cliente ofereça mas o servidor não tenha viraria erro de validação na cara do dono,
    /// por culpa nossa. Leitura livre — saber os fusos disponíveis não é informação de ninguém.</summary>
    [HttpGet("fusos")]
    public IActionResult Fusos() => Ok(servico.FusosDisponiveis());

    /// <summary>As UFs, para o select. Constante do domínio, exposta aqui para o cliente não
    /// manter uma segunda cópia que diverge.</summary>
    [HttpGet("ufs")]
    public IActionResult Ufs() => Ok(ConfiguracaoRef.Ufs);

    [HttpPut("empresa")]
    [Authorize(Roles = "dono")]
    public async Task<IActionResult> AtualizarDados(
        [FromBody] EditarDadosEmpresa dados, CancellationToken ct)
    {
        await servico.AtualizarDadosAsync(dados, ct);
        return NoContent();
    }

    /// <summary>Janela, faixas do semáforo e dias de follow-up.
    ///
    /// NÃO reprocessa o que já foi carimbado: lembrete mantém a data-alvo e mensagem reservada
    /// mantém o `data_disparo`. Vale da próxima rodada em diante. As faixas do semáforo, por
    /// serem calculadas no cliente, valem no próximo /api/painel/status.</summary>
    [HttpPut("atendimento")]
    [Authorize(Roles = "dono")]
    public async Task<IActionResult> AtualizarAtendimento(
        [FromBody] EditarAtendimento dados, CancellationToken ct)
    {
        await servico.AtualizarAtendimentoAsync(dados, ct);
        return NoContent();
    }
}
