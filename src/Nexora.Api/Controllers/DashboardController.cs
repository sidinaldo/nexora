using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>O payload RICO, sob demanda. NÃO é o que o shell faz polling — aquele é o
/// /api/painel/status, que é barato de propósito.</summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController(
    IServicoDashboard servico,
    IServicoSerie serie,
    IServicoAtividades atividades) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await servico.DashboardAsync(ct));

    /// <summary>A evolução no período. Padrão: últimos 30 dias por dia.
    ///
    /// O intervalo é limitado a 400 pontos — não por medo do banco, que agrega isso sem suar,
    /// mas porque o gráfico tem 1000px de largura: mais que isso é um ponto por pixel, ilegível
    /// e caro de serializar.</summary>
    [HttpGet("serie")]
    public async Task<IActionResult> Serie(
        [FromQuery] DateOnly? de,
        [FromQuery] DateOnly? ate,
        [FromQuery] string? agrupamento,
        CancellationToken ct)
    {
        var fim = ate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var inicio = de ?? fim.AddDays(-29);

        if (!TentarAgrupamento(agrupamento, out var modo))
            return BadRequest(new { erro = "Agrupamento inválido. Use dia, semana ou mes." });

        if (inicio > fim)
            return BadRequest(new { erro = "A data inicial não pode ser depois da final." });

        if (fim.DayNumber - inicio.DayNumber > 400)
            return BadRequest(new { erro = "O período é longo demais. Use no máximo 400 dias." });

        return Ok(await serie.ObterAsync(inicio, fim, modo, ct));
    }

    /// <summary>Atividade recente, por cursor.
    ///
    /// SEM restrição de papel no atributo, de propósito: todo mundo vê o feed, mas o SERVIÇO
    /// recorta o conteúdo — Vendedor só enxerga o que é dele ou o que não tem dono. Um
    /// `[Authorize(Roles=...)]` aqui esconderia a tela inteira do vendedor em vez de mostrar a
    /// parte que é dele.</summary>
    [HttpGet("atividades")]
    public async Task<IActionResult> Atividades(
        [FromQuery] DateTime? cursorEm,
        [FromQuery] string? cursorChave,
        [FromQuery] long? responsavelId,
        [FromQuery] int tamanho = 20,
        CancellationToken ct = default) =>
        Ok(await atividades.ListarAsync(cursorEm, cursorChave, responsavelId, tamanho, ct));

    // ===================== O `/demo` FOI REMOVIDO =====================
    // Ele devolvia números inventados e resolvia UMA tela: quem abria a demonstração via um
    // dashboard cheio e depois caixa vazia, funil vazio, Meu Dia vazio. E os números não
    // passavam por consulta nenhuma — não provavam que o produto funciona, só que o gerador
    // funcionava.
    //
    // A demonstração agora é LOGAR no tenant de demonstração e usar o produto: mesmos serviços,
    // mesmas consultas, mesmas telas. Ver docs/PI-4b.md.
    // ==================================================================

    private static bool TentarAgrupamento(string? valor, out AgrupamentoSerie modo)
    {
        modo = AgrupamentoSerie.Dia;
        if (string.IsNullOrWhiteSpace(valor)) return true;

        return valor.ToLowerInvariant() switch
        {
            "dia" => true,
            "semana" => Definir(AgrupamentoSerie.Semana, out modo),
            "mes" or "mês" => Definir(AgrupamentoSerie.Mes, out modo),
            _ => false
        };

        static bool Definir(AgrupamentoSerie v, out AgrupamentoSerie destino)
        {
            destino = v;
            return true;
        }
    }
}
