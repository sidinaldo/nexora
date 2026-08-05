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

public interface IServicoSeedDemonstracao
{
    /// <summary>Cria ou REPOVOA o tenant de demonstração.
    ///
    /// IDEMPOTENTE por limpar-e-recriar: rodar duas vezes deixa o banco no mesmo estado, não em
    /// dobro. A alternativa (detectar e sair) deixaria o tenant envelhecer — as datas são
    /// relativas a hoje, e um tenant semeado no mês passado mostraria "última mensagem: 30 dias
    /// atrás" na caixa de entrada, que é exatamente o que uma demonstração não pode mostrar.
    ///
    /// DETERMINÍSTICO: mesma semente, mesmos dados. Captura de tela reproduzível e teste estável.</summary>
    Task<ResumoSeedDemonstracao> SemearAsync(CancellationToken ct);
}
