using System.Data.Common;
using System.Text.RegularExpressions;
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

    /// <summary>Quantos comandos citam a tabela — `FROM etapas_funil`, `JOIN etapas_funil`...
    ///
    /// ⚠️ PALAVRA INTEIRA, E NÃO SUBSTRING. Era `Contains`, e o PER-1 criou `usuarios_permissoes`:
    /// a partir dali `QueTocam("usuarios")` passaria a contar os comandos da tabela NOVA também, e
    /// `A_CONFERENCIA_DE_ATIVO_NAO_CUSTA_CONSULTA_NOVA` — que afirma `usuarios == 1` no endpoint
    /// mais chamado do sistema — mediria outra coisa sem ninguém notar.
    ///
    /// O `_` conta como caractere de palavra na borda do regex, então a borda em volta de
    /// `usuarios` NÃO casa dentro de `usuarios_permissoes`. É a correção exata, e nenhum uso
    /// existente muda de número.</summary>
    public int QueTocam(string tabela) =>
        _comandos.Count(c => Regex.IsMatch(
            c, Borda + Regex.Escape(tabela) + Borda, RegexOptions.IgnoreCase));

    /// <summary>Comandos que citam as DUAS tabelas — para descontar a subconsulta que mora dentro
    /// de outra consulta, e não é uma ida ao banco a mais.</summary>
    public int QueTocamAsDuas(string tabela, string outra) =>
        _comandos.Count(c =>
            Regex.IsMatch(c, Borda + Regex.Escape(tabela) + Borda, RegexOptions.IgnoreCase)
            && Regex.IsMatch(c, Borda + Regex.Escape(outra) + Borda, RegexOptions.IgnoreCase));

    /// <summary>A borda de palavra do regex, escrita como constante porque um `\b` dentro de
    /// string interpolada atravessa camadas de escaping e já chegou aqui como o CARACTERE de
    /// backspace — um regex que não casava com nada, e o teste de custo passou a medir zero.</summary>
    private const string Borda = @"\b";

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
