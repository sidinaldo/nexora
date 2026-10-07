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
    /// <summary>O que sobrou da configuração depois do PER-1: dados da empresa, janela de
    /// atendimento, feriados, primeiros passos — e o WEBHOOK DE SAÍDA.
    ///
    /// ⚠️ ESTE GESTO NÃO SE DELEGA, e o webhook é o motivo mais forte. Ele manda dados de contato
    /// para uma URL que quem configura escolhe, e regenera o segredo: não é risco de configuração,
    /// é risco de vazamento da base. E, diferente do funil, não existe invariante que limite —
    /// qualquer URL é válida.
    ///
    /// ⚠️ FERIADO FICOU AQUI, e já estava. Existiu um `CadastrarFeriado` separado que dava a
    /// CRIAÇÃO ao gestor — e só ela: apagar e "trabalhamos neste dia" continuavam do dono, e não
    /// havia tela por onde o gestor chegasse. Ele podia fazer a metade que não tem volta.
    ///
    /// E feriado não é anotação de calendário: ele entra no cálculo de tempo útil (o semáforo de
    /// urgência da equipe inteira) e no `MotorFollowUp` (em que DIA a mensagem automática sai).
    /// É alavanca de motor com cara de agenda.</summary>
    ConfigurarEmpresa,

    /// <summary>Ver a equipe, convidar, mudar papel — e, por ver a equipe, escolher o responsável
    /// de um contato.
    ///
    /// ⚠️ NÃO SE DELEGA, nem por exceção. Quem gerencia equipe muda papéis: um vendedor com este
    /// gesto promove um colega a Dono e pede o favor de volta (`ServicoEquipe` só o impede de
    /// mexer no PRÓPRIO papel). Escalada num salto. Dono de férias se resolve promovendo o líder a
    /// Dono de verdade, que é entrega do papel inteiro e some na volta.</summary>
    GerenciarEquipe,

    // ===================== OS CINCO QUE SAÍRAM DE `ConfigurarEmpresa` (PER-1) =====================
    // Pedido um a um: "dar e remover permissão na ausência do dono ou gestor". Eram um gesto só
    // cobrindo dez controllers, e nem o gestor os alcançava — logo não havia meio-termo entre "não
    // mexe em nada" e "é dono".
    //
    // ⚠️ TODOS NASCEM `[Dono]`, e é isso que faz a partição não abrir acesso para ninguém no dia
    // do deploy: `PapeisCom` continua devolvendo `["dono"]` para cada rota repontada, e as 90
    // entradas de `RotasPorPermissaoTests` não mudaram uma linha.
    //
    // ⚠️ DELEGAR A ÁREA INTEIRA (inclusive o apagar) é seguro aqui, e não repete o erro do
    // `CadastrarFeriado`: o destrutivo destas áreas tem INVARIANTE DE NEGÓCIO, que vale para todo
    // mundo — funil padrão não se apaga, funil com negócios não se apaga, etapa com contatos exige
    // destino, etiqueta diz quantos contatos perde. Lá não havia nada disso.
    // ==============================================================================================

    /// <summary>Conexões e canais: adicionar, conectar e remover o número de WhatsApp da empresa.</summary>
    GerenciarConexao,

    /// <summary>Etiquetas: criar, editar e remover. O remover já diz antes quantos contatos
    /// perdem a marca (`ImpactoAsync`).</summary>
    GerenciarEtiquetas,

    /// <summary>Captação: formulários do site e o link/QR. Superfície PÚBLICA da empresa.</summary>
    GerenciarCaptacao,

    /// <summary>Funis e etapas: criar, editar, reordenar, definir o ganho e remover.
    ///
    /// O teto de 5 funis e as duas invariantes do remover (padrão, e com negócios) continuam
    /// valendo para quem recebe este gesto — elas não são permissão.</summary>
    GerenciarFunis,

    /// <summary>A credencial de conversão da Meta — a aba Anúncios de Integrações.
    ///
    /// ⚠️ SÓ ESTA METADE DE "INTEGRAÇÕES" SE DELEGA. A outra é o webhook de saída, que ficou em
    /// `ConfigurarEmpresa` de propósito: manda a base para fora.</summary>
    GerenciarAnuncios,

    ImportarContatos,

    /// <summary>Tira faturamento da contagem: é de quem responde pelo número.</summary>
    CancelarVenda,

    /// <summary>A trilha de auditoria. Vendedor não audita colega.</summary>
    VerHistorico,

    /// <summary>LGPD. Irreversível: o histórico do contato perde o nome para sempre.</summary>
    AnonimizarContato,

    /// <summary>Relatórios e atividades da equipe INTEIRA. Sem esta, cada um vê só o seu.</summary>
    VerNumerosDaEquipe,

    /// <summary>===================== AGIR SOBRE MUITOS DE UMA VEZ (LPA-1) =====================
    ///
    /// Criar lembrete, aplicar etiqueta, reabrir e redistribuir em LOTE, a partir da tela de leads
    /// parados.
    ///
    /// ⚠️ VER NÃO É AGIR, e por isso este gesto não guarda a tela. Quem não o tem abre
    /// `/leads-parados` e enxerga os próprios leads — é a lista de trabalho dele. O que o gesto
    /// fecha é mexer em trinta de uma vez, que é operação de quem coordena.
    ///
    /// Nasce `[Dono, Gestor]` e delegável: o dono de uma equipe de cinco pode entregar isto a um
    /// vendedor sênior sem promovê-lo a dono.</summary>
    AgirEmLote
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
            // Os cinco do PER-1: `[Dono]`, para a partição não mudar o acesso de ninguém.
            [Permissao.GerenciarConexao] = [PapelUsuario.Dono],
            [Permissao.GerenciarEtiquetas] = [PapelUsuario.Dono],
            [Permissao.GerenciarCaptacao] = [PapelUsuario.Dono],
            [Permissao.GerenciarFunis] = [PapelUsuario.Dono],
            [Permissao.GerenciarAnuncios] = [PapelUsuario.Dono],
            [Permissao.ImportarContatos] = [PapelUsuario.Dono, PapelUsuario.Gestor],
            [Permissao.CancelarVenda] = [PapelUsuario.Dono, PapelUsuario.Gestor],
            [Permissao.VerHistorico] = [PapelUsuario.Dono, PapelUsuario.Gestor],
            [Permissao.AnonimizarContato] = [PapelUsuario.Dono, PapelUsuario.Gestor],
            [Permissao.VerNumerosDaEquipe] = [PapelUsuario.Dono, PapelUsuario.Gestor],
            [Permissao.AgirEmLote] = [PapelUsuario.Dono, PapelUsuario.Gestor]
        };

    /// <summary>===================== OS DEZ QUE O DONO LIGA E DESLIGA POR PESSOA (PER-1) =====================
    ///
    /// Em DUAS famílias, e é assim que a tela os agrupa: cinco de CONFIGURAÇÃO (saíram de
    /// `ConfigurarEmpresa`) e cinco de OPERAÇÃO (o que o gestor já tinha).
    ///
    /// ⚠️ OS DOIS QUE FALTAM FALTAM DE PROPÓSITO.
    ///   · `GerenciarEquipe` — quem gerencia equipe muda papéis, e promove um colega a Dono para
    ///     pedir o favor de volta. Escalada num salto.
    ///   · `ConfigurarEmpresa` — ficou com o webhook de saída, que manda a base de contatos para
    ///     uma URL escolhida por quem configura. Nenhuma invariante limita isso: qualquer URL vale.
    ///
    /// ⚠️ É ARRAY, E NÃO `HashSet`, porque a ORDEM é afirmada por extenso em
    /// `PermissoesTests.OS_DEZ_GESTOS_DELEGAVEIS_SAO_ESTES`. Dez comparações de enum num `Contains`
    /// não alocam nada e não aparecem em perfil nenhum — um `HashSet` aqui trocaria um teste
    /// legível por uma economia que ninguém mediria.
    /// ============================================================================================</summary>
    public static readonly Permissao[] Delegaveis =
    [
        Permissao.GerenciarConexao, Permissao.GerenciarEtiquetas, Permissao.GerenciarCaptacao,
        Permissao.GerenciarFunis, Permissao.GerenciarAnuncios,
        Permissao.ImportarContatos, Permissao.CancelarVenda, Permissao.VerHistorico,
        Permissao.AnonimizarContato, Permissao.VerNumerosDaEquipe,
        Permissao.AgirEmLote
    ];

    /// <summary>Os papéis como o TOKEN os carrega (`dono`, `gestor`) — a BASE de cada gesto, antes
    /// de qualquer exceção por pessoa. É o que a política usa para responder, nos testes, quem
    /// entra numa rota.</summary>
    public static IReadOnlyList<string> PapeisCom(Permissao permissao) =>
        Tabela[permissao].Select(NoToken).ToList();

    /// <summary>Pode este gesto? O papel é a BASE; as exceções são o que o dono ligou ou desligou
    /// PARA ESTA PESSOA.
    ///
    /// ⚠️ `excecoes` É OPCIONAL, e isso não é conveniência: `null` significa "nenhuma exceção", que
    /// é o caso de quase todo mundo e o de todo job de fundo. Manter a assinatura antiga válida é
    /// o que deixou os oito pontos de `Exigir`/`Pode` nos serviços sem uma letra de diferença.
    ///
    /// ⚠️ A EXCEÇÃO NÃO ALCANÇA O DONO. Não é só que ele já tem tudo pela tabela — é que tirar um
    /// gesto do dono é como se tranca alguém fora da própria conta, e uma regra aqui vale mais do
    /// que cinco travas espalhadas por quem escreve.
    ///
    /// ⚠️ E NÃO ALCANÇA GESTO INDELEGÁVEL. Uma linha forjada no banco pedindo
    /// `+gerenciar_equipe` para um vendedor é ignorada aqui, no lugar que decide — e não só na
    /// tela que oferece, nem só no serviço que grava.</summary>
    public static bool Pode(string? papel, Permissao permissao,
                            IReadOnlyDictionary<Permissao, bool>? excecoes = null)
    {
        if (papel is null) return false;

        var daTabela = Tabela[permissao]
            .Any(p => NoToken(p).Equals(papel, StringComparison.OrdinalIgnoreCase));

        if (EhDono(papel) || !Delegaveis.Contains(permissao)) return daTabela;

        return excecoes is not null && excecoes.TryGetValue(permissao, out var concedida)
            ? concedida
            : daTabela;
    }

    /// <summary>O que a pessoa pode, com os nomes que a API usa. É o que vai para o painel — no
    /// login e no `GET /auth/permissoes` —, e é o MESMO cálculo que a política faz por gesto.</summary>
    public static IReadOnlyList<string> NaApiPara(
        string? papel, IReadOnlyDictionary<Permissao, bool>? excecoes = null) =>
        Enum.GetValues<Permissao>().Where(p => Pode(papel, p, excecoes)).Select(NaApi).ToList();

    private static bool EhDono(string papel) =>
        NoToken(PapelUsuario.Dono).Equals(papel, StringComparison.OrdinalIgnoreCase);

    public static string NaApi(Permissao permissao) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(permissao.ToString());

    private static readonly IReadOnlyDictionary<string, Permissao> PorNomeDaApi =
        Enum.GetValues<Permissao>().ToDictionary(NaApi);

    /// <summary>O caminho de volta: do nome da API para o gesto, ou `null` se não existe mais.
    ///
    /// ⚠️ `null` EM VEZ DE EXCEÇÃO, de propósito. A exceção gravada é `text` com o nome da API —
    /// legível no banco, e é literalmente o que o painel recebe. O preço é que uma permissão
    /// REMOVIDA do enum deixa linhas órfãs, e elas não podem derrubar o login de ninguém: um nome
    /// que não se reconhece mais é simplesmente ignorado.</summary>
    public static Permissao? DoNomeDaApi(string? nome) =>
        nome is not null && PorNomeDaApi.TryGetValue(nome, out var p) ? p : null;

    private static string NoToken(PapelUsuario papel) => papel.ToString().ToLowerInvariant();
}

/// <summary>A checagem dentro dos serviços — ela vale também quando outro código chama por
/// dentro, sem passar pela rota.</summary>
public static class PermissoesDoContexto
{
    /// <summary>⚠️ PASSA AS EXCEÇÕES DO CONTEXTO, e é só por isto que os oito pontos de
    /// `Exigir`/`Pode` nos serviços não mudaram uma letra no PER-1: a permissão por pessoa entrou
    /// por aqui, num lugar só.</summary>
    public static bool Pode(this IContextoEmpresa contexto, Permissao permissao) =>
        Permissoes.Pode(contexto.Papel, permissao, contexto.ExcecoesDePermissao);

    /// <summary>Recusa com a frase de quem recusou — "Só o dono ou um gestor pode importar
    /// contatos." diz o que falta; "sem permissão" não diz nada.</summary>
    public static void Exigir(this IContextoEmpresa contexto, Permissao permissao, string recusa)
    {
        if (!contexto.Pode(permissao)) throw new RegraDeNegocioException(recusa);
    }
}
