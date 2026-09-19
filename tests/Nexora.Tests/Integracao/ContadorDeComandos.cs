using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Nexora.Tests.Integracao;

/// <summary>Conta os comandos SQL que um contexto manda ao banco.
///
/// Existe para os testes de LOTE provarem uma coisa que nenhum outro teste enxerga: que a operação
/// não pergunta ao banco uma vez por linha. "Resolvi uma vez, fora do laço" dito num comentário não
/// vale nada — foi exatamente o comentário da importação, e ela fazia 2.000 consultas.</summary>
public sealed class ContadorDeComandos : DbCommandInterceptor
{
    private readonly List<string> _comandos = [];

    public IReadOnlyList<string> Comandos => _comandos;

    /// <summary>Quantos comandos citam a tabela — `FROM etapas_funil`, `JOIN etapas_funil`...</summary>
    public int QueTocam(string tabela) =>
        _comandos.Count(c => c.Contains(tabela, StringComparison.OrdinalIgnoreCase));

    public void Zerar() => _comandos.Clear();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _comandos.Add(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        _comandos.Add(command.CommandText);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}
