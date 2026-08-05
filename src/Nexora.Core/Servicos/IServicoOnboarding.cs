namespace Nexora.Core.Servicos;

/// <summary>Um passo do checklist.
///
/// `Concluido` é sempre DERIVADO do estado real — nunca lido de uma flag. É o que impede o
/// painel de mentir: uma empresa cujo WhatsApp caiu volta a ver o passo 1 aceso, porque a
/// pergunta "está conectado?" é feita ao banco, não a um carimbo de "já configurou".</summary>
public record PassoOnboarding(
    string Chave,
    string Titulo,
    string Descricao,
    bool Concluido,
    /// <summary>O passo foi pulado pelo dono. Só o passo da equipe aceita isso.</summary>
    bool Dispensado,
    /// <summary>Rota do painel para onde o passo leva. NULL quando não há o que fazer —
    /// o passo 3 é ESPERA, não ação.</summary>
    string? Rota,
    string? RotuloAcao);

public record Onboarding(
    IReadOnlyList<PassoOnboarding> Passos,
    int Concluidos,
    int Total,
    /// <summary>Todos os passos resolvidos (concluídos ou dispensados).</summary>
    bool Completo,
    /// <summary>O dono fechou o painel.</summary>
    bool Dispensado,
    /// <summary>Se a tela deve aparecer: falta passo E o dono não fechou o painel.</summary>
    bool Mostrar,
    /// <summary>Minutos entre o cadastro da empresa e a primeira mensagem recebida. NULL
    /// enquanto ela não chegou. É métrica INTERNA — a tela não a exibe, e nada promete prazo.</summary>
    int? MinutosAteAPrimeiraMensagem);

public interface IServicoOnboarding
{
    /// <summary>O checklist do tenant logado, calculado na hora a partir do estado.</summary>
    Task<Onboarding> ObterAsync(CancellationToken ct);

    /// <summary>"Convido a equipe depois." Resolve o passo 2 sem cumpri-lo.</summary>
    Task DispensarEquipeAsync(CancellationToken ct);

    /// <summary>Fecha o painel de primeiros passos. Onboarding que prende irrita mais do que
    /// ajuda — sempre dá para sair.</summary>
    Task DispensarAsync(CancellationToken ct);
}
