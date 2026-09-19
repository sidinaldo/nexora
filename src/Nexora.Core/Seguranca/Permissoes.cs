using System.Text.Json;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;

namespace Nexora.Core.Seguranca;

/// <summary>O que alguém pode FAZER no Nexora — nomeado pelo gesto, e não pelo papel.
///
/// Sai da API em snake_case (`importar_contatos`), e é por esses nomes que o painel decide o que
/// oferecer: ele pergunta "posso importar?", nunca "sou dono?".</summary>
public enum Permissao
{
    /// <summary>Configuração da empresa: conexão, canais, formulários, funis e etapas, etiquetas,
    /// feriados, integrações, janela de atendimento, primeiros passos.</summary>
    ConfigurarEmpresa,

    /// <summary>Ver a equipe, convidar, mudar papel — e, por ver a equipe, escolher o responsável
    /// de um contato.</summary>
    GerenciarEquipe,

    ImportarContatos,

    /// <summary>Tira faturamento da contagem: é de quem responde pelo número.</summary>
    CancelarVenda,

    /// <summary>A trilha de auditoria. Vendedor não audita colega.</summary>
    VerHistorico,

    /// <summary>LGPD. Irreversível: o histórico do contato perde o nome para sempre.</summary>
    AnonimizarContato,

    CadastrarFeriado,

    /// <summary>Relatórios e atividades da equipe INTEIRA. Sem esta, cada um vê só o seu.</summary>
    VerNumerosDaEquipe
}

/// <summary>===================== QUEM PODE O QUÊ, NUM LUGAR SÓ =====================
///
/// ⚠️ A MESMA REGRA ESTAVA EM TRÊS CAMADAS, e elas já discordaram. Os `[Authorize(Roles=...)]`
/// dos controllers, quatro cópias de `ExigirDonoOuGestor` nos serviços, e uns vinte e cinco
/// `ehDono()`/`podeGerenciar()` no painel. O caso que custou: a importação aceitava o gestor no
/// servidor, e a tela, escrita com `ehDono`, escondia dele o botão.
///
/// Agora as três leem esta tabela:
///   · as rotas, por política — `[Authorize(Policy = nameof(Permissao.X))]`, registrada no
///     `Program.cs` a partir de `PapeisCom`;
///   · os serviços, por `IContextoEmpresa.Exigir`;
///   · o painel, pela lista `Permissoes` que o login devolve — e que ele só consulta.
///
/// Mudar quem pode um gesto é mudar UMA linha aqui, e as três camadas mudam juntas.
/// ================================================================================</summary>
public static class Permissoes
{
    private static readonly IReadOnlyDictionary<Permissao, PapelUsuario[]> Tabela =
        new Dictionary<Permissao, PapelUsuario[]>
        {
            [Permissao.ConfigurarEmpresa] = [PapelUsuario.Dono],
            [Permissao.GerenciarEquipe] = [PapelUsuario.Dono],
            [Permissao.ImportarContatos] = [PapelUsuario.Dono, PapelUsuario.Gestor],
            [Permissao.CancelarVenda] = [PapelUsuario.Dono, PapelUsuario.Gestor],
            [Permissao.VerHistorico] = [PapelUsuario.Dono, PapelUsuario.Gestor],
            [Permissao.AnonimizarContato] = [PapelUsuario.Dono, PapelUsuario.Gestor],
            [Permissao.CadastrarFeriado] = [PapelUsuario.Dono, PapelUsuario.Gestor],
            [Permissao.VerNumerosDaEquipe] = [PapelUsuario.Dono, PapelUsuario.Gestor]
        };

    /// <summary>Os papéis como o TOKEN os carrega (`dono`, `gestor`) — é o que o
    /// `RequireRole` da política compara, e a comparação dele é exata.</summary>
    public static IReadOnlyList<string> PapeisCom(Permissao permissao) =>
        Tabela[permissao].Select(NoToken).ToList();

    public static bool Pode(string? papel, Permissao permissao) =>
        papel is not null
        && Tabela[permissao].Any(p => NoToken(p).Equals(papel, StringComparison.OrdinalIgnoreCase));

    /// <summary>O que o papel pode, com os nomes que a API usa. É o que vai para o painel.</summary>
    public static IReadOnlyList<string> NaApiPara(string? papel) =>
        Enum.GetValues<Permissao>().Where(p => Pode(papel, p)).Select(NaApi).ToList();

    public static string NaApi(Permissao permissao) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(permissao.ToString());

    private static string NoToken(PapelUsuario papel) => papel.ToString().ToLowerInvariant();
}

/// <summary>A checagem dentro dos serviços — ela vale também quando outro código chama por
/// dentro, sem passar pela rota.</summary>
public static class PermissoesDoContexto
{
    public static bool Pode(this IContextoEmpresa contexto, Permissao permissao) =>
        Permissoes.Pode(contexto.Papel, permissao);

    /// <summary>Recusa com a frase de quem recusou — "Só o dono ou um gestor pode importar
    /// contatos." diz o que falta; "sem permissão" não diz nada.</summary>
    public static void Exigir(this IContextoEmpresa contexto, Permissao permissao, string recusa)
    {
        if (!contexto.Pode(permissao)) throw new RegraDeNegocioException(recusa);
    }
}
