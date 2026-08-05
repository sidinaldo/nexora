namespace Nexora.Core.Servicos;

/// <summary>Dados para abrir uma conta no Nexora.</summary>
/// <param name="InstanceName">Nome da instancia na Evolution. NULL = a aplicacao deriva um
/// deterministico a partir do id da empresa; o cliente nao deveria precisar inventar isso.</param>
public record NovaEmpresa(
    string Nome,
    string? Documento,
    string NomeDono,
    string EmailDono,
    string Senha,
    string? NomeConexao = null,
    string? InstanceName = null);

public interface IServicoCadastroEmpresa
{
    /// <summary>Cria a empresa (tenant) + o usuario DONO + as 5 etapas do funil + a conexao,
    /// tudo numa transacao. Devolve o id da empresa.
    ///
    /// Empresa sem etapa e empresa quebrada: o kanban nao renderiza e Contato.EtapaId e NOT
    /// NULL. Por isso o seed nao e um passo separado que alguem pode esquecer.
    ///
    /// ARMADILHA: roda SEM tenant no contexto — a empresa ainda nao existe. As checagens de
    /// unicidade precisam de .IgnoreQueryFilters().</summary>
    Task<long> CadastrarAsync(NovaEmpresa nova, CancellationToken ct);
}
