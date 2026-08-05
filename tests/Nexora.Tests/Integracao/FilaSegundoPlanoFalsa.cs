using Nexora.Core;

namespace Nexora.Tests.Integracao;

/// <summary>Fila de segundo plano que CAPTURA o trabalho em vez de executá-lo.
///
/// ===================== POR QUE CAPTURAR, E NÃO RODAR =====================
/// É exatamente o que a fila de verdade faz do ponto de vista de quem chama: aceita e volta na
/// hora. Uma fila falsa que executasse na hora devolveria o custo do envio para dentro da
/// requisição — e o teste de timing passaria a medir o oposto do que ele existe para provar.
///
/// O teste que quer verificar o envio chama `ExecutarPendentesAsync()` explicitamente, e aí
/// decide QUANDO isso acontece.
/// ========================================================================</summary>
public sealed class FilaSegundoPlanoFalsa : IFilaSegundoPlano
{
    private readonly List<Func<IServiceProvider, CancellationToken, Task>> _trabalhos = [];

    public int Enfileirados => _trabalhos.Count;

    public void Enfileirar(Func<IServiceProvider, CancellationToken, Task> trabalho) =>
        _trabalhos.Add(trabalho);

    /// <summary>Roda o que foi enfileirado, com o provedor que o teste montar.</summary>
    public async Task ExecutarPendentesAsync(IServiceProvider provedor)
    {
        foreach (var t in _trabalhos.ToList()) await t(provedor, CancellationToken.None);
        _trabalhos.Clear();
    }
}

/// <summary>Provedor mínimo: devolve o que o teste registrar, e nada mais.
///
/// Um contêiner de DI de verdade aqui exigiria montar metade da aplicação para provar uma
/// chamada de método.</summary>
public sealed class ProvedorFalso(params object[] servicos) : IServiceProvider
{
    public object? GetService(Type tipo) => servicos.FirstOrDefault(tipo.IsInstanceOfType);
}
