using Microsoft.Extensions.Logging;

namespace Nexora.Tests.Integracao;

/// <summary>Um `ILogger` que guarda o que foi registrado, para o teste poder afirmar que o caminho
/// normal **não** registrou erro.
///
/// ===================== POR QUE ISTO PRECISOU EXISTIR =====================
/// O rastro do lead é gravado com `ON CONFLICT DO NOTHING` e dentro de um `try/catch`. Com o
/// `NullLogger`, tirar o `ON CONFLICT` não derrubava teste nenhum: a segunda gravação estourava,
/// o `catch` engolia, e o resultado observável ficava idêntico.
///
/// Só que não é idêntico. Sem o `ON CONFLICT`, toda pessoa que preenche o formulário duas vezes
/// vira um erro no log — e log cheio de alarme falso é log que ninguém lê no dia do alarme
/// verdadeiro. A afirmação que faltava era "repetir é fluxo normal, não erro".
/// =========================================================================</summary>
public sealed class LoggerQueGuarda<T> : ILogger<T>
{
    private readonly List<(LogLevel Nivel, string Mensagem)> _linhas = [];

    public IReadOnlyList<(LogLevel Nivel, string Mensagem)> Linhas => _linhas;

    public IEnumerable<string> Erros =>
        _linhas.Where(l => l.Nivel >= LogLevel.Error).Select(l => l.Mensagem);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => Vazio.Instancia;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _linhas.Add((logLevel, formatter(state, exception)));

    private sealed class Vazio : IDisposable
    {
        public static readonly Vazio Instancia = new();
        public void Dispose() { }
    }
}
