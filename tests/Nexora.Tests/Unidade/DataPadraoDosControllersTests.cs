using Nexora.Api.Controllers;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;

namespace Nexora.Tests.Unidade;

/// <summary>===================== A DATA FINAL PADRÃO É HOJE NA EMPRESA (AUD-XX) =====================
///
/// O gráfico do dashboard e os relatórios, sem data final no pedido, usavam
/// `DateTime.UtcNow`: das 21h à meia-noite de Brasília, "até amanhã". O "hoje" agora vem de
/// `IHojeDaEmpresa` — aqui, um dublê com uma data que nunca seria a de UTC, para o teste não
/// passar por coincidência.
/// =========================================================================================</summary>
public class DataPadraoDosControllersTests
{
    private static readonly DateOnly HojeNaEmpresa = new(2020, 3, 10);

    private sealed class HojeFixo : IHojeDaEmpresa
    {
        public Task<DateOnly> HojeAsync(CancellationToken ct) => Task.FromResult(HojeNaEmpresa);
    }

    private sealed class SerieGravada : IServicoSerie
    {
        public DateOnly De { get; private set; }
        public DateOnly Ate { get; private set; }

        public Task<SerieTemporalDto> ObterAsync(
            DateOnly de, DateOnly ate, AgrupamentoSerie agrupamento, CancellationToken ct)
        {
            De = de;
            Ate = ate;
            return Task.FromResult(new SerieTemporalDto(de, ate, "dia", [], 0));
        }
    }

    /// <summary>Só o relatório de vendas é chamado; o resto não entra neste teste.</summary>
    private sealed class RelatoriosGravados : IServicoRelatorios
    {
        public FiltroRelatorio? Filtro { get; private set; }

        public Task<RelatorioVendas> VendasPorPeriodoAsync(FiltroRelatorio filtro, CancellationToken ct)
        {
            Filtro = filtro;
            return Task.FromResult<RelatorioVendas>(null!);
        }

        public Task<OpcoesRelatorio> OpcoesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinhaVendedor>> DesempenhoVendedoresAsync(FiltroRelatorio f, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinhaOrigem>> OrigemLeadsAsync(FiltroRelatorio f, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinhaCanalVenda>> VendasPorCanalAsync(FiltroRelatorio f, CancellationToken ct) => throw new NotImplementedException();
        public Task<RelatorioFunil> FunilNoPeriodoAsync(FiltroRelatorio f, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinhaTempoResposta>> TempoRespostaAsync(FiltroRelatorio f, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinhaMotivoPerda>> MotivosPerdaAsync(FiltroRelatorio f, CancellationToken ct) => throw new NotImplementedException();
        public Task<PaginaComTotal<LinhaClienteRecorrente>> ClientesRecorrentesAsync(FiltroRelatorio f, int pagina, int tamanho, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public async Task O_GRAFICO_DO_DASHBOARD_TERMINA_HOJE_NA_EMPRESA()
    {
        var serie = new SerieGravada();
        var controller = new DashboardController(null!, serie, null!, new HojeFixo());

        await controller.Serie(null, null, null, default);

        Assert.Equal(HojeNaEmpresa, serie.Ate);
        Assert.Equal(HojeNaEmpresa.AddDays(-29), serie.De);
    }

    [Fact]
    public async Task OS_RELATORIOS_TERMINAM_HOJE_NA_EMPRESA()
    {
        var relatorios = new RelatoriosGravados();
        var controller = new RelatoriosController(relatorios, null!, new HojeFixo());

        await controller.Vendas(new ParametrosRelatorio(), default);

        Assert.NotNull(relatorios.Filtro);
        Assert.Equal(HojeNaEmpresa, relatorios.Filtro!.Ate);
        Assert.Equal(HojeNaEmpresa.AddDays(-29), relatorios.Filtro.De);
    }
}
