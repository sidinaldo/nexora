namespace Nexora.Core.Servicos;

public record ResumoSeedDemonstracao(
    long EmpresaId,
    string EmailDono,
    string Senha,
    int Usuarios,
    int Contatos,
    int Conversas,
    int Mensagens,
    int Lembretes,
    int Ganhos,
    int Perdidos);

/// <summary>Volume e janela do seed de demonstração.
///
/// ===================== POR QUE PARÂMETRO E NÃO UM SEGUNDO SEMEADOR =====================
/// A tentação, quando aparece o pedido "quero uma base grande", é escrever um gerador separado.
/// Dois semeadores divergem: um ganha a regra nova do funil configurável, o outro não, e a base
/// grande passa a produzir estado que o produto real nunca produziria — sem ninguém notar, porque
/// os testes de coerência rodam contra o pequeno.
///
/// Um só, com volume e janela como entrada. As proporções (quanto ganha, quanto perde, a forma do
/// funil, as cores do semáforo) são as MESMAS em qualquer escala.
/// =======================================================================================</summary>
/// <param name="Contatos">Quantos contatos criar. O resto sai daí: ~2/3 ganham conversa, cada
/// conversa tem de 6 a 14 mensagens, e os lembretes são uma fração dos contatos vivos.</param>
/// <param name="Dias">Janela para trás a partir de hoje. É ela que dá densidade ao gráfico de
/// série temporal: 2000 contatos em 120 dias são ~17 por dia, uma linha com forma.</param>
public record OpcoesSeedDemonstracao(int Contatos = 60, int Dias = 120)
{
    /// <summary>Tetos de sanidade. O de cima não é capricho: cada contato arrasta conversa e
    /// mensagens, então 20 mil contatos seriam ~140 mil linhas numa requisição HTTP só.</summary>
    public const int MinimoContatos = 20;
    public const int MaximoContatos = 20_000;
    public const int MinimoDias = 7;
    public const int MaximoDias = 730;

    /// <summary>Recorta para a faixa aceitável em vez de recusar.
    ///
    /// Quem pede 999999 contatos quer "muitos", não quer um 400 — e o número exato nunca é o
    /// ponto num dado de demonstração. O resumo devolvido diz o que de fato foi criado.</summary>
    public OpcoesSeedDemonstracao Saneada() => new(
        Math.Clamp(Contatos, MinimoContatos, MaximoContatos),
        Math.Clamp(Dias, MinimoDias, MaximoDias));
}

public interface IServicoSeedDemonstracao
{
    /// <summary>Cria ou REPOVOA o tenant de demonstração.
    ///
    /// IDEMPOTENTE por limpar-e-recriar: rodar duas vezes deixa o banco no mesmo estado, não em
    /// dobro. A alternativa (detectar e sair) deixaria o tenant envelhecer — as datas são
    /// relativas a hoje, e um tenant semeado no mês passado mostraria "última mensagem: 30 dias
    /// atrás" na caixa de entrada, que é exatamente o que uma demonstração não pode mostrar.
    ///
    /// DETERMINÍSTICO: mesma semente e mesmas opções, mesmos dados. Captura de tela reproduzível
    /// e teste estável.</summary>
    /// <param name="opcoes">Volume e janela. `null` usa o padrão de `OpcoesSeedDemonstracao`.</param>
    Task<ResumoSeedDemonstracao> SemearAsync(OpcoesSeedDemonstracao? opcoes, CancellationToken ct);
}
