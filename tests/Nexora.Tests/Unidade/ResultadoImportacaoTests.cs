using Nexora.Core.Entidades;
using Nexora.Core.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>"N de 900 linhas…" vem pronto do servidor (AUD-XX, #25): as linhas processadas são as
/// importadas, as duplicadas e as inválidas.</summary>
public class ResultadoImportacaoTests
{
    [Fact]
    public void AS_PROCESSADAS_SAO_AS_TRES_CONTAGENS()
    {
        var r = new ResultadoImportacao(1, 900, 500, 30, 7, StatusImportacao.Processando);
        Assert.Equal(537, r.Processadas);
    }
}
