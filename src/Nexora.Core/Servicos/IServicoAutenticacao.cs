using Nexora.Core.Seguranca;

namespace Nexora.Core.Servicos;

/// <summary>Quem esta logado, do ponto de vista de quem emite o token. Nao e a entidade
/// Usuario: e o recorte que o painel precisa.</summary>
public record UsuarioAutenticado(
    long Id, string Nome, string Email, string Papel, long EmpresaId, string EmpresaNome,
    IReadOnlyDictionary<Permissao, bool>? Excecoes = null)
{
    /// <summary>O que esta pessoa pode fazer, com os nomes da API (`importar_contatos`...). O
    /// painel decide o que OFERECER só por esta lista — nunca pelo `Papel`, que continua aqui
    /// para ser MOSTRADO.
    ///
    /// ⚠️ CONTINUA DERIVADA, e é por isso que o PER-1 acrescentou `Excecoes` ao record em vez de
    /// trocar esta lista por um campo. O painel precisa do EFETIVO (papel ± exceções), e ele é
    /// calculado aqui pela MESMA `Permissoes.NaApiPara` que a política usa por gesto — uma
    /// derivação, num lugar só. Virar dado abriria a porta para quem monta o record calcular
    /// diferente, que é exatamente a divergência de camadas que `Permissoes` documenta.
    ///
    /// ⚠️ `Excecoes` É OPCIONAL de propósito: quem não tem exceção nenhuma (quase todo mundo)
    /// monta o record como antes, e os testes que fazem `new UsuarioAutenticado(...)` seguem
    /// valendo sem um toque.</summary>
    public IReadOnlyList<string> Permissoes => Seguranca.Permissoes.NaApiPara(Papel, Excecoes);
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
