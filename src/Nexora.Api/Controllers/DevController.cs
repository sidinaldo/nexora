using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>FERRAMENTAS DE DESENVOLVIMENTO. Popula e limpa dados falsos.
///
/// ===================== DUAS TRAVAS, NÃO UMA =====================
/// 1. Só existe em **Development**: fora dele, toda ação devolve 404 — não 403. 403 confirmaria
///    que a rota existe; 404 não conta nada a quem estiver sondando.
/// 2. Só o **dono**: mesmo em desenvolvimento, semear apaga e recria dados do tenant.
///
/// A checagem de ambiente é em tempo de EXECUÇÃO, dentro de cada ação, e não só no registro do
/// serviço: controller é descoberto por convenção, e um registro condicional que alguém mexa
/// deixaria a rota viva sem ninguém notar. Duas travas independentes é o ponto.
/// ================================================================</summary>
[ApiController]
[Route("api/dev")]
[Authorize(Roles = "dono")]
public class DevController(
    IServicoSemente semente,
    IServicoSementeConversas conversas,
    IWebHostEnvironment ambiente) : ControllerBase
{
    /// <summary>Popula o tenant logado com o cenário completo: contatos nas cinco etapas,
    /// conversas em todas as faixas do semáforo, lembretes atrasados e de hoje, vendas do mês,
    /// equipe com papéis variados.
    ///
    /// LIMPA a semeadura anterior antes — pode rodar quantas vezes quiser.</summary>
    [HttpPost("semear")]
    public async Task<IActionResult> Semear(CancellationToken ct)
    {
        if (!ambiente.IsDevelopment()) return NotFound();
        return Ok(await semente.SemearAsync(ct));
    }

    /// <summary>Apaga SÓ o que foi semeado — o que está marcado com `semente-dev`. Contato
    /// digitado à mão não tem a marca e sobrevive.</summary>
    [HttpDelete("semear")]
    public async Task<IActionResult> Limpar(CancellationToken ct)
    {
        if (!ambiente.IsDevelopment()) return NotFound();
        return Ok(await semente.LimparAsync(ct));
    }

    /// <summary>Reescreve a THREAD das `quantas` conversas mais recentes com diálogos de verdade —
    /// pergunta que puxa resposta, ritmo, e desfecho.
    ///
    /// NÃO cria conversa nem contato, e NÃO mexe em `ultima_mensagem_em` nem `aguardando_desde`:
    /// a distribuição do semáforo que a semeadura geral montou fica intacta. Ver
    /// `IServicoSementeConversas`.
    ///
    /// É IDEMPOTENTE — apaga as mensagens das conversas escolhidas antes de escrever.</summary>
    [HttpPost("semear-conversas")]
    public async Task<IActionResult> SemearConversas(
        [FromQuery] int quantas = 60, CancellationToken ct = default)
    {
        if (!ambiente.IsDevelopment()) return NotFound();
        return Ok(await conversas.SemearAsync(quantas, ct));
    }
}
