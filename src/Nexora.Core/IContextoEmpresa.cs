namespace Nexora.Core;

/// <summary>Quem e a requisicao atual: a EMPRESA (tenant) e o USUARIO (pessoa) logados.
/// Implementado na Api lendo os claims do JWT.
///
/// O DbContext aplica HasQueryFilter por EmpresaId em todas as entidades do tenant —
/// e o que impede uma query de vazar dados de outra empresa mesmo se o dev esquecer
/// o Where. E a barreira principal do isolamento multi-tenant (nao usamos RLS).
///
/// ARMADILHA (vale para TODOS os blocos, nao so este): fora de uma requisicao
/// autenticada — login, webhook da Evolution, job de fundo — nao ha ninguem no
/// contexto e EmpresaId vale 0. O filtro global entao compara com 0 e a consulta
/// volta VAZIA, EM SILENCIO, SEM ERRO NENHUM. Nesses pontos o filtro precisa ser
/// explicitamente ignorado com .IgnoreQueryFilters() MAIS um Where por empresaId —
/// o IgnoreQueryFilters sozinho abriria a consulta para todos os tenants.
/// Ver EstaAutenticado e o ServicoAutenticacao.</summary>
public interface IContextoEmpresa
{
    /// <summary>Id da empresa (tenant) da requisicao. 0 quando nao ha tenant no contexto.</summary>
    long EmpresaId { get; }

    /// <summary>Id do usuario (pessoa) logado. 0 quando nao ha usuario no contexto.
    /// E quem assume uma conversa e quem registra a acao no historico.</summary>
    long UsuarioId { get; }

    /// <summary>Papel do usuario logado ("dono"/"gestor"/"vendedor"), lido do claim de role.
    /// NULL fora de requisicao autenticada. O enforcement principal e por [Authorize(Roles=...)];
    /// isto serve as regras de servico (ex.: nao rebaixar o ultimo dono).</summary>
    string? Papel { get; }

    /// <summary>False em login, webhook e job de fundo — que rodam sem tenant e
    /// precisam de .IgnoreQueryFilters() para enxergar as linhas.</summary>
    bool EstaAutenticado { get; }
}
