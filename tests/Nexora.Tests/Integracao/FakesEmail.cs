using Nexora.Core.Email;

namespace Nexora.Tests.Integracao;

/// <summary>Remetente falso: guarda o que teria sido entregue e pode falhar sob comando.
///
/// É a peça que prova a separação de camadas — o serviço de aplicação recebe `IRemetenteEmail`,
/// e trocar SMTP por isto aqui não exige mudar uma linha de domínio.</summary>
public sealed class RemetenteFalso : IRemetenteEmail
{
    public List<EmailPronto> Enviados { get; } = [];

    /// <summary>Erro a lançar no próximo envio. Simula provedor fora do ar.</summary>
    public Exception? ErroParaLancar { get; set; }

    public Task EnviarAsync(EmailPronto email, CancellationToken ct)
    {
        // Registra ANTES de eventualmente falhar: o teste precisa saber que a tentativa
        // aconteceu, e com que conteúdo, mesmo quando ela estoura.
        Enviados.Add(email);
        if (ErroParaLancar is not null) throw ErroParaLancar;
        return Task.CompletedTask;
    }

    public EmailPronto UltimoDoTipo(string tipo) =>
        Enviados.LastOrDefault(e => e.Tipo == tipo)
        ?? throw new InvalidOperationException($"Nenhum e-mail do tipo '{tipo}' foi enviado.");

    public int Quantos(string tipo) => Enviados.Count(e => e.Tipo == tipo);
}

/// <summary>Notificador falso, para os testes que só precisam saber SE foi chamado — sem
/// exercitar montagem de template nem gravação do registro.</summary>
public sealed class NotificadorEmailFalso : INotificadorEmail
{
    public List<(string Tipo, string Email, string? Token)> Chamadas { get; } = [];

    public Task ConviteAsync(long empresaId, string email, string nome, string empresaNome,
        string token, CancellationToken ct)
    {
        Chamadas.Add(("convite", email, token));
        return Task.CompletedTask;
    }

    public Task ResetSenhaAsync(long? empresaId, string email, string nome, string token,
        CancellationToken ct)
    {
        Chamadas.Add(("reset", email, token));
        return Task.CompletedTask;
    }

    public Task SenhaAlteradaAsync(long empresaId, string email, string nome, CancellationToken ct)
    {
        Chamadas.Add(("senha_alterada", email, null));
        return Task.CompletedTask;
    }

    public int Quantos(string tipo) => Chamadas.Count(c => c.Tipo == tipo);
}
