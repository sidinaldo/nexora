using Nexora.Core.Seguranca;

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
    /// NULL fora de requisicao autenticada. O enforcement principal e a politica da rota (`Seguranca.Permissoes`);
    /// isto serve as regras de servico (ex.: nao rebaixar o ultimo dono).</summary>
    string? Papel { get; }

    /// <summary>O que o dono ligou ou desligou PARA ESTA PESSOA, por cima do que o papel dá.
    /// `null` = nenhuma exceção, que é o caso de quase todo mundo e de todo job de fundo.
    ///
    /// ⚠️ VEM DO TOKEN, E NÃO DO BANCO. Uma ida ao banco por requisição foi recusada neste projeto
    /// — está escrito no `ServicoPainel`, que preferiu pegar carona no poll de 45s a cobrar uma
    /// consulta do endpoint mais chamado do sistema. Aqui a conta seria pior: autorização acontece
    /// em TODA requisição.
    ///
    /// ⚠️ SÃO AS EXCEÇÕES, E NÃO A LISTA PRONTA. O token carrega só o que DIVERGE do papel, e a
    /// base continua saindo da tabela de `Permissoes` a cada chamada. Levar a lista pronta
    /// congelaria a tabela: um deploy que mudasse quem pode um gesto não alcançaria nenhuma sessão
    /// aberta por até doze horas — e `Permissoes` promete justamente o contrário.
    ///
    /// ⚠️ O PREÇO, ESCRITO: tirar uma exceção de alguém com o sistema aberto só vale no próximo
    /// login (até 12h). Para cortar acesso AGORA, a alavanca é inativar a pessoa — isso derruba a
    /// sessão no poll seguinte, em até 45 segundos.</summary>
    IReadOnlyDictionary<Permissao, bool>? ExcecoesDePermissao { get; }

    /// <summary>False em login, webhook e job de fundo — que rodam sem tenant e
    /// precisam de .IgnoreQueryFilters() para enxergar as linhas.</summary>
    bool EstaAutenticado { get; }
}
