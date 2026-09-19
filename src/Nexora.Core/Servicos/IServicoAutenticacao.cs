namespace Nexora.Core.Servicos;

/// <summary>Quem esta logado, do ponto de vista de quem emite o token. Nao e a entidade
/// Usuario: e o recorte que o painel precisa.</summary>
public record UsuarioAutenticado(
    long Id, string Nome, string Email, string Papel, long EmpresaId, string EmpresaNome)
{
    /// <summary>O que esta pessoa pode fazer, com os nomes da API (`importar_contatos`...). O
    /// painel decide o que OFERECER só por esta lista — nunca pelo `Papel`, que continua aqui
    /// para ser MOSTRADO. Derivada do papel pela mesma tabela que trava as rotas: ver
    /// `Seguranca.Permissoes`.</summary>
    public IReadOnlyList<string> Permissoes => Seguranca.Permissoes.NaApiPara(Papel);
}

public interface IServicoAutenticacao
{
    /// <summary>Devolve null se o e-mail nao existe OU se a senha esta errada — de
    /// proposito indistinguivel, para nao revelar quais e-mails estao cadastrados.
    /// Lanca RegraDeNegocioException se o usuario ou a empresa estiverem inativos.
    ///
    /// ARMADILHA: roda SEM tenant no contexto (e justamente o tenant que esta
    /// descobrindo), entao a implementacao PRECISA de .IgnoreQueryFilters().</summary>
    Task<UsuarioAutenticado?> AutenticarAsync(string email, string senha, CancellationToken ct);
}
