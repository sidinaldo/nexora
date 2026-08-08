using Nexora.Api.Csv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Os sete relatórios (bloco 14).
///
/// ⚠️ NÃO HÁ `[Authorize(Roles=)]` aqui, e a ausência é deliberada: a regra de papel não é "pode
/// chamar a rota" — vendedor PODE ver relatório, o dele. O recorte é por LINHA, e por isso mora no
/// serviço (`ServicoRelatorios.ResponsavelEfetivo`). Um atributo aqui daria 403 para o vendedor e
/// tiraria dele uma tela que é legitimamente sua.
///
/// A barra de filtros da tela vira um `FiltroRelatorio` só. Cada rota aplica o que faz sentido
/// para ela e ignora o resto.</summary>
[ApiController]
[Route("api/relatorios")]
[Authorize]
public class RelatoriosController(IServicoRelatorios servico) : ControllerBase
{
    /// <summary>Teto de pontos por resposta. Não é medo do banco — ele agrega isso sem suar —, é
    /// que o gráfico tem ~1000px: mais que isso é um ponto por pixel, ilegível e caro de
    /// serializar. Mesma razão e mesmo número do `DashboardController.Serie`.</summary>
    private const int MaximoPontos = 400;

    /// <summary>As listas da barra de filtros. Uma chamada só, e já recortada por papel.</summary>
    [HttpGet("opcoes")]
    public async Task<IActionResult> Opcoes(CancellationToken ct) =>
        Ok(await servico.OpcoesAsync(ct));

    [HttpGet("vendas")]
    public Task<IActionResult> Vendas([FromQuery] ParametrosRelatorio q, CancellationToken ct) =>
        Executar(q, async f => Ok(await servico.VendasPorPeriodoAsync(f, ct)));

    [HttpGet("vendedores")]
    public Task<IActionResult> Vendedores([FromQuery] ParametrosRelatorio q, CancellationToken ct) =>
        Executar(q, async f => Ok(await servico.DesempenhoVendedoresAsync(f, ct)));

    [HttpGet("origens")]
    public Task<IActionResult> Origens([FromQuery] ParametrosRelatorio q, CancellationToken ct) =>
        Executar(q, async f => Ok(await servico.OrigemLeadsAsync(f, ct)));

    /// <summary>3b (NEG-3): endpoint SEPARADO, e não um campo a mais em `/origens`. São duas
    /// leituras com chave e recorte diferentes — ver `LinhaCanalVenda`.</summary>
    [HttpGet("canais")]
    public Task<IActionResult> Canais([FromQuery] ParametrosRelatorio q, CancellationToken ct) =>
        Executar(q, async f => Ok(await servico.VendasPorCanalAsync(f, ct)));

    [HttpGet("funil")]
    public Task<IActionResult> Funil([FromQuery] ParametrosRelatorio q, CancellationToken ct) =>
        Executar(q, async f => Ok(await servico.FunilNoPeriodoAsync(f, ct)));

    [HttpGet("tempo-resposta")]
    public Task<IActionResult> TempoResposta([FromQuery] ParametrosRelatorio q, CancellationToken ct) =>
        Executar(q, async f => Ok(await servico.TempoRespostaAsync(f, ct)));

    [HttpGet("perdas")]
    public Task<IActionResult> Perdas([FromQuery] ParametrosRelatorio q, CancellationToken ct) =>
        Executar(q, async f => Ok(await servico.MotivosPerdaAsync(f, ct)));

    [HttpGet("recorrentes")]
    public Task<IActionResult> Recorrentes(
        [FromQuery] ParametrosRelatorio q, [FromQuery] int pagina = 1, [FromQuery] int tamanho = 20,
        CancellationToken ct = default) =>
        Executar(q, async f => Ok(await servico.ClientesRecorrentesAsync(f, pagina, tamanho, ct)));

    // ==================================================================== exportação
    /// <summary>===================== O CSV É MONTADO NO SERVIDOR =====================
    ///
    /// Não é preferência de arquitetura: o relatório de recorrentes de uma padaria com dois anos
    /// de uso tem milhares de linhas, e montá-las no browser significa buscar todas as páginas,
    /// concatenar em memória e travar a aba. Aqui a consulta roda uma vez e sai como texto.
    ///
    /// `;` e BOM UTF-8 — o que o Excel brasileiro espera. Sem o BOM ele lê "Preço" como "PreÃ§o";
    /// com vírgula como separador, ele joga a linha inteira na primeira coluna.
    ///
    /// O `baixarArquivo` do cliente busca por HttpClient (que carrega o Bearer) e só então vira
    /// download — um `<a href>` direto abriria 401.
    /// ======================================================================</summary>
    [HttpGet("{nome}/csv")]
    public Task<IActionResult> Csv(string nome, [FromQuery] ParametrosRelatorio q, CancellationToken ct) =>
        Executar(q, async f =>
        {
            var (arquivo, linhas) = nome.ToLowerInvariant() switch
            {
                "vendas" => ("vendas", await CsvVendasAsync(f, ct)),
                "vendedores" => ("vendedores", await CsvVendedoresAsync(f, ct)),
                "origens" => ("origens", await CsvOrigensAsync(f, ct)),
                "funil" => ("funil", await CsvFunilAsync(f, ct)),
                "tempo-resposta" => ("tempo-resposta", await CsvTempoAsync(f, ct)),
                "perdas" => ("motivos-de-perda", await CsvPerdasAsync(f, ct)),
                "recorrentes" => ("clientes-recorrentes", await CsvRecorrentesAsync(f, ct)),
                _ => (null, null!)
            };

            if (arquivo is null)
                return BadRequest(new { erro = $"Relatório desconhecido: \"{nome}\"." });

            // BOM, `;` e vírgula decimal moram no `CsvBrasileiro` — ver lá o porquê de cada um.
            return File(CsvBrasileiro.Gerar(linhas), "text/csv; charset=utf-8",
                $"{arquivo}-{f.De:yyyy-MM-dd}-a-{f.Ate:yyyy-MM-dd}.csv");
        });

    private async Task<List<string[]>> CsvVendasAsync(FiltroRelatorio f, CancellationToken ct)
    {
        var r = await servico.VendasPorPeriodoAsync(f, ct);
        List<string[]> linhas =
        [
            ["Período", "Vendas", "Faturamento", "Concluídas", "Valor concluído",
             "Canceladas", "Valor cancelado"]
        ];

        linhas.AddRange(r.Pontos.Select(p => new[]
        {
            p.Periodo.ToString("dd/MM/yyyy"), Num(p.Vendas), Moeda(p.Faturamento),
            Num(p.Concluidas), Moeda(p.ValorConcluido), Num(p.Canceladas), Moeda(p.ValorCancelado)
        }));

        // O TOTAL vai no arquivo. Quem abre a planilha some as colunas na mão e compara com a
        // tela; sem a linha, uma divergência de arredondamento vira suspeita sobre o sistema.
        linhas.Add(["TOTAL", Num(r.Totais.Vendas), Moeda(r.Totais.Faturamento),
                    Num(r.Totais.Concluidas), Moeda(r.Totais.ValorConcluido),
                    Num(r.Totais.Canceladas), Moeda(r.Totais.ValorCancelado)]);
        return linhas;
    }

    private async Task<List<string[]>> CsvVendedoresAsync(FiltroRelatorio f, CancellationToken ct)
    {
        var r = await servico.DesempenhoVendedoresAsync(f, ct);
        List<string[]> linhas = [["Vendedor", "Leads atendidos", "Vendas", "Valor", "Ticket médio", "Conversão"]];
        linhas.AddRange(r.Select(l => new[]
        {
            l.Nome, Num(l.LeadsAtendidos), Num(l.Vendas), Moeda(l.Valor),
            Moeda(l.TicketMedio), Pct(l.Conversao)
        }));
        return linhas;
    }

    /// <summary>As DUAS leituras no mesmo arquivo, uma abaixo da outra e com cabeçalho próprio.
    ///
    /// Lado a lado seria mais bonito e estaria errado: as linhas de cima são por origem do lead e
    /// recortadas pela CRIAÇÃO do contato; as de baixo são por campanha e recortadas pelo
    /// FECHAMENTO da venda. Alinhá-las em colunas convidaria a dividir uma pela outra.</summary>
    private async Task<List<string[]>> CsvOrigensAsync(FiltroRelatorio f, CancellationToken ct)
    {
        var r = await servico.OrigemLeadsAsync(f, ct);
        List<string[]> linhas = [["Origem", "Leads", "Vendas", "Valor", "Conversão"]];
        linhas.AddRange(r.Select(l => new[]
        {
            l.Origem, Num(l.Leads), Num(l.Vendas), Moeda(l.Valor), Pct(l.Conversao)
        }));

        var canais = await servico.VendasPorCanalAsync(f, ct);
        if (canais.Count > 0)
        {
            linhas.Add(["", "", "", "", ""]);
            linhas.Add(["Vendas por canal de captação (fechadas no período)", "", "", "", ""]);
            linhas.Add(["Canal", "Vendas", "Valor", "", ""]);
            linhas.AddRange(canais.Select(l => new[]
            {
                l.Canal ?? "Sem canal identificado", Num(l.Vendas), Moeda(l.Valor), "", ""
            }));
        }

        return linhas;
    }

    private async Task<List<string[]>> CsvFunilAsync(FiltroRelatorio f, CancellationToken ct)
    {
        var r = await servico.FunilNoPeriodoAsync(f, ct);
        var agora = r.Agora.ToDictionary(a => a.EtapaId);

        // As duas metades no MESMO arquivo, em colunas separadas e nomeadas: "entrou no período" e
        // "está agora" são perguntas diferentes, e juntá-las numa coluna só é o que produz o
        // rótulo mentiroso que este bloco existe para não repetir.
        List<string[]> linhas = [["Etapa", "Entradas no período", "Contatos agora", "Valor agora"]];
        linhas.AddRange(r.Entradas.Select(e => new[]
        {
            e.Nome, Num(e.Entradas),
            Num(agora.TryGetValue(e.EtapaId, out var a) ? a.Contatos : 0),
            Moeda(agora.TryGetValue(e.EtapaId, out var b) ? b.Valor : 0m)
        }));
        return linhas;
    }

    private async Task<List<string[]>> CsvTempoAsync(FiltroRelatorio f, CancellationToken ct)
    {
        var r = await servico.TempoRespostaAsync(f, ct);
        List<string[]> linhas = [["Vendedor", "Respostas", "Média (min úteis)", "Mediana (min úteis)"]];
        linhas.AddRange(r.Select(l => new[]
        {
            l.Nome, Num(l.Respostas), Dec(l.MediaMinutos), Dec(l.MedianaMinutos)
        }));
        return linhas;
    }

    private async Task<List<string[]>> CsvPerdasAsync(FiltroRelatorio f, CancellationToken ct)
    {
        var r = await servico.MotivosPerdaAsync(f, ct);
        List<string[]> linhas = [["Motivo", "Contatos", "Valor perdido"]];
        linhas.AddRange(r.Select(l => new[] { l.Motivo, Num(l.Contatos), Moeda(l.ValorPerdido) }));
        return linhas;
    }

    private async Task<List<string[]>> CsvRecorrentesAsync(FiltroRelatorio f, CancellationToken ct)
    {
        List<string[]> linhas = [["Cliente", "Telefone", "Compras", "Total", "Última compra"]];

        // ===== O CSV LEVA A LISTA INTEIRA, NÃO A PÁGINA DA TELA =====
        // Exportar 20 de 3.000 é o tipo de erro que ninguém percebe até alguém fechar o mês com o
        // número errado. Pagina no banco em blocos grandes, e para quando a página vem incompleta.
        for (var p = 1; ; p++)
        {
            var bloco = await servico.ClientesRecorrentesAsync(f, p, 200, ct);
            linhas.AddRange(bloco.Itens.Select(l => new[]
            {
                l.Nome, l.Telefone, Num(l.Compras), Moeda(l.Total), l.UltimaEm.ToString("dd/MM/yyyy")
            }));

            if (bloco.Itens.Count < 200) break;
        }

        return linhas;
    }

    // ==================================================================== apoio
    /// <summary>A validação que vale para todas as rotas, num lugar só.</summary>
    private async Task<IActionResult> Executar(
        ParametrosRelatorio q, Func<FiltroRelatorio, Task<IActionResult>> acao)
    {
        var fim = q.Ate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var inicio = q.De ?? fim.AddDays(-29);   // padrão: 30 dias

        if (inicio > fim)
            return BadRequest(new { erro = "A data inicial não pode ser depois da final." });

        if (!TentarEnum<AgrupamentoSerie>(q.Agrupamento, AgrupamentoSerie.Dia, out var agrupamento))
            return BadRequest(new { erro = "Agrupamento inválido. Use dia, semana ou mes." });

        if (!TentarEnum<OrigemLead>(q.Origem, null, out var origem))
            return BadRequest(new { erro = $"Origem inválida: \"{q.Origem}\"." });

        if (!TentarEnum<StatusVenda>(q.Status, null, out var status))
            return BadRequest(new { erro = $"Status de venda inválido: \"{q.Status}\"." });

        var pontos = agrupamento switch
        {
            AgrupamentoSerie.Semana => (fim.DayNumber - inicio.DayNumber) / 7,
            AgrupamentoSerie.Mes => (fim.DayNumber - inicio.DayNumber) / 28,
            _ => fim.DayNumber - inicio.DayNumber
        };

        if (pontos > MaximoPontos)
            return BadRequest(new
            {
                erro = $"O período pede mais de {MaximoPontos} pontos. Escolha um intervalo menor "
                     + "ou agrupe por semana ou mês."
            });

        return await acao(new FiltroRelatorio(
            inicio, fim, agrupamento!.Value, q.ResponsavelId, origem, q.EtapaId, status,
            q.MotivoPerda, q.ValorMin, q.ValorMax));
    }

    /// <summary>Texto -> enum, com nulo aceito. `false` = veio texto e ele não existe — que é
    /// erro do cliente e merece 400, não um filtro ignorado em silêncio.</summary>
    private static bool TentarEnum<T>(string? texto, T? padrao, out T? valor) where T : struct, Enum
    {
        valor = padrao;
        if (string.IsNullOrWhiteSpace(texto)) return true;

        if (!Enum.TryParse<T>(texto, ignoreCase: true, out var achado)) return false;

        valor = achado;
        return true;
    }

    // Os formatos e o escape vivem no `CsvBrasileiro`: os dois lados (servidor e `download.ts`)
    // precisam produzir o MESMO arquivo, e duas cópias da regra divergem no dia em que uma muda.
    private static string Num(int v) => CsvBrasileiro.Num(v);
    private static string Moeda(decimal v) => CsvBrasileiro.Moeda(v);
    private static string Dec(double v) => CsvBrasileiro.Dec(v);
    private static string Pct(double v) => CsvBrasileiro.Pct(v);
}

/// <summary>A barra de filtros da tela, como vem na query string. Os enums chegam como TEXTO e são
/// validados no controller — `[FromQuery]` sobre enum devolveria 400 genérico do model binder, sem
/// dizer qual valor estava errado.</summary>
public record ParametrosRelatorio(
    DateOnly? De = null,
    DateOnly? Ate = null,
    string? Agrupamento = null,
    long? ResponsavelId = null,
    string? Origem = null,
    long? EtapaId = null,
    string? Status = null,
    string? MotivoPerda = null,
    decimal? ValorMin = null,
    decimal? ValorMax = null);
