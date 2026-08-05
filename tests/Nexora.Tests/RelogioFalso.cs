namespace Nexora.Tests;

/// <summary>TimeProvider controlavel. Escrito a mao (em vez de puxar o pacote
/// Microsoft.Extensions.TimeProvider.Testing) porque o unico recurso necessario e "avancar
/// o relogio" — testar bloqueio de 15 minutos nao pode custar 15 minutos de espera.</summary>
public sealed class RelogioFalso(DateTimeOffset inicio) : TimeProvider
{
    private DateTimeOffset _agora = inicio;

    public override DateTimeOffset GetUtcNow() => _agora;

    public void Avancar(TimeSpan quanto) => _agora = _agora.Add(quanto);
}
