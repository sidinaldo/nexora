using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nexora.Api.Controllers;
using Nexora.Api.Seguranca;
using Nexora.Core;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Tests.Integracao;

namespace Nexora.Tests.Unidade;

/// <summary>A TABELA DE QUEM PODE O QUÊ — a que trava as rotas, os serviços e o que o painel
/// oferece. O teste de rotas prova que nenhuma rota mudou; este prova o que a tabela diz, e o que
/// chega à tela.</summary>
public class PermissoesTests
{
    /// <summary>O dono pode tudo, o gestor pode o que toca a OPERAÇÃO DA EQUIPE (importar,
    /// cancelar venda, ver o histórico, anonimizar, ver a equipe inteira), e o vendedor nada disso.
    ///
    /// ⚠️ `CadastrarFeriado` SAIU DAQUI, e a lista abaixo é o que impede ele de voltar por
    /// distração. Feriado é `ConfigurarEmpresa` — sempre foi, na própria documentação do enum —, e
    /// o que existia era meio acesso: o gestor criava e não apagava, sem tela para nenhum dos
    /// dois.</summary>
    [Fact]
    public void CADA_PAPEL_PODE_O_QUE_JA_PODIA()
    {
        Assert.Equal(Enum.GetValues<Permissao>(), PodeO("dono"));

        Assert.Equal(
        [
            Permissao.ImportarContatos, Permissao.CancelarVenda, Permissao.VerHistorico,
            Permissao.AnonimizarContato, Permissao.VerNumerosDaEquipe
        ], PodeO("gestor"));

        Assert.Empty(PodeO("vendedor"));
    }

    // ============================================================ PER-1 · exceção por pessoa
    /// <summary>⚠️ OS DEZ POR EXTENSO, E OS DOIS QUE FALTAM. `GerenciarEquipe` entrar nesta lista
    /// daria a um vendedor o poder de promover um colega a Dono e pedir o favor de volta;
    /// `ConfigurarEmpresa` daria o webhook de saída, que manda a base de contatos para qualquer
    /// URL. Os dois são indelegáveis por decisão, e esta lista é onde a decisão mora.</summary>
    [Fact]
    public void OS_DEZ_GESTOS_DELEGAVEIS_SAO_ESTES()
    {
        Assert.Equal(
        [
            // Configuração — saíram de `ConfigurarEmpresa`.
            Permissao.GerenciarConexao, Permissao.GerenciarEtiquetas, Permissao.GerenciarCaptacao,
            Permissao.GerenciarFunis, Permissao.GerenciarAnuncios,
            // Operação — o que o gestor já tinha.
            Permissao.ImportarContatos, Permissao.CancelarVenda, Permissao.VerHistorico,
            Permissao.AnonimizarContato, Permissao.VerNumerosDaEquipe
        ], Permissoes.Delegaveis);

        Assert.DoesNotContain(Permissao.GerenciarEquipe, Permissoes.Delegaveis);
        Assert.DoesNotContain(Permissao.ConfigurarEmpresa, Permissoes.Delegaveis);
    }

    /// <summary>A exceção vale SOBRE o papel, nos dois sentidos — é a feature inteira.</summary>
    [Fact]
    public void A_EXCECAO_VALE_SOBRE_O_PAPEL()
    {
        // Concedida a quem não tinha.
        Assert.True(Permissoes.Pode("vendedor", Permissao.CancelarVenda,
            new Dictionary<Permissao, bool> { [Permissao.CancelarVenda] = true }));

        // Revogada de quem tinha.
        Assert.False(Permissoes.Pode("gestor", Permissao.VerNumerosDaEquipe,
            new Dictionary<Permissao, bool> { [Permissao.VerNumerosDaEquipe] = false }));

        // Exceção de OUTRO gesto não encosta neste.
        Assert.False(Permissoes.Pode("vendedor", Permissao.CancelarVenda,
            new Dictionary<Permissao, bool> { [Permissao.VerHistorico] = true }));
    }

    /// <summary>⚠️ O DONO NÃO SE TRANCA FORA DA PRÓPRIA CONTA. Uma exceção negando tudo para ele é
    /// ignorada — e é esta regra que substitui cinco travas espalhadas por quem escreve.</summary>
    [Fact]
    public void O_DONO_NAO_PERDE_PERMISSAO_NEM_POR_EXCECAO()
    {
        var nada = Enum.GetValues<Permissao>().ToDictionary(p => p, _ => false);

        Assert.Equal(
            Enum.GetValues<Permissao>().Select(Permissoes.NaApi),
            Permissoes.NaApiPara("dono", nada));
    }

    /// <summary>⚠️ GESTO INDELEGÁVEL IGNORA A EXCEÇÃO NO LUGAR QUE DECIDE. Uma linha forjada no
    /// banco não vira acesso só porque a tela e o serviço de gravação foram contornados.</summary>
    [Fact]
    public void GESTO_INDELEGAVEL_IGNORA_A_EXCECAO()
    {
        var tudo = new Dictionary<Permissao, bool>
        {
            [Permissao.GerenciarEquipe] = true,
            [Permissao.ConfigurarEmpresa] = true
        };

        Assert.False(Permissoes.Pode("vendedor", Permissao.GerenciarEquipe, tudo));
        Assert.False(Permissoes.Pode("vendedor", Permissao.ConfigurarEmpresa, tudo));
        Assert.False(Permissoes.Pode("gestor", Permissao.ConfigurarEmpresa, tudo));
    }

    /// <summary>Sem exceção nenhuma, a resposta é a de sempre — o caminho de quase todo mundo, e o
    /// de todo job de fundo.</summary>
    [Fact]
    public void SEM_EXCECAO_VALE_O_PAPEL()
    {
        Assert.Equal(Permissoes.NaApiPara("gestor"), Permissoes.NaApiPara("gestor", null));
        Assert.Equal(
            Permissoes.NaApiPara("vendedor"),
            Permissoes.NaApiPara("vendedor", new Dictionary<Permissao, bool>()));
    }

    /// <summary>Toda permissão está na tabela. Uma que faltasse derrubaria a aplicação no boot —
    /// `PoliticasDePermissao.Registrar` pede os papéis de cada uma.</summary>
    [Fact]
    public void TODA_PERMISSAO_TEM_PAPEIS()
    {
        foreach (var p in Enum.GetValues<Permissao>())
            Assert.NotEmpty(Permissoes.PapeisCom(p));
    }

    /// <summary>O token carrega `dono`; um serviço antigo comparava sem caixa. Os dois têm de
    /// continuar valendo, e sem papel nenhum (job de fundo) não se pode nada.</summary>
    [Fact]
    public void O_PAPEL_SE_COMPARA_SEM_CAIXA_E_SEM_PAPEL_NAO_PODE()
    {
        Assert.True(Permissoes.Pode("Dono", Permissao.ConfigurarEmpresa));
        Assert.False(Permissoes.Pode(null, Permissao.ImportarContatos));
        Assert.False(Permissoes.Pode("", Permissao.ImportarContatos));
    }

    /// <summary>⚠️ OS NOMES SÃO CONTRATO COM O PAINEL: `auth.pode('importar_contatos')`. Mudar o
    /// nome de um membro do enum esconderia o botão sem erro nenhum — por isso eles estão escritos
    /// por extenso aqui, e o tipo `Permissao` do painel repete a mesma lista.</summary>
    [Fact]
    public void OS_NOMES_QUE_O_PAINEL_LE()
    {
        Assert.Equal(
        [
            // Os dois que NÃO se delegam.
            "configurar_empresa", "gerenciar_equipe",
            // Os cinco que saíram de `configurar_empresa` no PER-1.
            "gerenciar_conexao", "gerenciar_etiquetas", "gerenciar_captacao", "gerenciar_funis",
            "gerenciar_anuncios",
            // Os cinco de operação.
            "importar_contatos", "cancelar_venda", "ver_historico", "anonimizar_contato",
            "ver_numeros_da_equipe"
        ], Enum.GetValues<Permissao>().Select(Permissoes.NaApi));
    }

    /// <summary>O login leva a lista junto do usuário — derivada do papel pela mesma tabela.</summary>
    [Fact]
    public void O_LOGIN_LEVA_AS_PERMISSOES_DO_PAPEL()
    {
        var gestor = new UsuarioAutenticado(1, "Ana", "a@a.com", "gestor", 7, "Loja");

        var json = JsonSerializer.Serialize(
            new LoginResponse("t", DateTime.UtcNow, gestor), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"permissoes\":[\"importar_contatos\",", json);
        Assert.DoesNotContain("configurar_empresa", json);
    }

    /// <summary>A sessão que já estava aberta pergunta ao servidor — e a resposta sai do papel do
    /// TOKEN, o mesmo que as rotas conferem.
    ///
    /// ⚠️ O DONO SE COMPARA COM O ENUM INTEIRO, e não com uma lista escrita à mão. Os nomes por
    /// extenso são do `OS_NOMES_QUE_O_PAINEL_LE`, que existe para isso; repeti-los aqui fez este
    /// teste quebrar na partição de `ConfigurarEmpresa` sem nada ter dado errado — eram sete nomes
    /// cravados e passaram a ser doze.</summary>
    [Fact]
    public void A_SESSAO_ABERTA_PERGUNTA_E_RECEBE_DO_PAPEL_DO_TOKEN()
    {
        var controller = new AuthController(null!, null!);

        Assert.Equal(
            Enum.GetValues<Permissao>().Select(Permissoes.NaApi),
            controller.Permissoes(new ContextoMutavel { Papel = "dono" }));

        Assert.Empty(controller.Permissoes(new ContextoMutavel { Papel = "vendedor" }));
    }

    /// <summary>⚠️ A POLÍTICA AVALIADA PELO ASP.NET DE VERDADE, e não lida por reflexão.
    ///
    /// Nenhum teste deste projeto sobe a API por HTTP, então o teste de rotas só prova que cada
    /// rota APONTA para a política certa. Este prova que a política registrada DECIDE certo: o
    /// `IAuthorizationService` real, com as opções do `Program.cs`, contra o claim de papel que o
    /// `GeradorToken` põe no token.</summary>
    [Theory]
    [InlineData("dono", nameof(Permissao.ConfigurarEmpresa), true)]
    [InlineData("gestor", nameof(Permissao.ConfigurarEmpresa), false)]
    [InlineData("gestor", nameof(Permissao.AnonimizarContato), true)]
    [InlineData("vendedor", nameof(Permissao.AnonimizarContato), false)]
    public async Task A_POLITICA_REGISTRADA_DECIDE_PELO_PAPEL_DO_TOKEN(
        string papel, string politica, bool entra)
    {
        var autorizacao = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(PoliticasDePermissao.Registrar)
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();

        var usuario = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, papel)], authenticationType: "teste"));

        var r = await autorizacao.AuthorizeAsync(usuario, null, politica);

        Assert.Equal(entra, r.Succeeded);
    }

    /// <summary>⚠️ O CLAIM NÃO PODE DERRUBAR NEM LIBERAR NADA QUANDO VEM TORTO. Valor sem sinal,
    /// vazio ou com o nome de uma permissão que não existe mais é ignorado — a pessoa cai para a
    /// base do papel, que é o comportamento de sempre. Um token malformado virando acesso seria o
    /// pior defeito possível nesta feature.</summary>
    [Fact]
    public void O_CLAIM_DE_EXCECAO_SE_LE_NOS_DOIS_SENTIDOS_E_IGNORA_LIXO()
    {
        Assert.Null(ClaimsDoToken.ExcecoesDe(Cracha("gestor")));

        var lidas = ClaimsDoToken.ExcecoesDe(Cracha("vendedor",
            "+cancelar_venda",        // concede
            "-ver_historico",         // revoga
            "gerenciar_funis",        // sem sinal
            "+",                      // sem nome
            "",                       // vazio
            "+gesto_que_nao_existe"   // nome que o enum não conhece
        ));

        Assert.Equal(new Dictionary<Permissao, bool>
        {
            [Permissao.CancelarVenda] = true,
            [Permissao.VerHistorico] = false
        }, lidas);
    }

    /// <summary>⚠️ A POLÍTICA DE VERDADE, COM A EXCEÇÃO NO TOKEN. O irmão do teste de cima decide
    /// pelo papel; este decide pela pessoa. Os dois juntos são o que tampa o buraco de
    /// `PapeisDaRota`, que só sabe dizer para qual GESTO a rota aponta.</summary>
    [Theory]
    // Concedida a quem o papel não dava.
    [InlineData("vendedor", nameof(Permissao.AnonimizarContato), "+anonimizar_contato", true)]
    [InlineData("vendedor", nameof(Permissao.GerenciarEtiquetas), "+gerenciar_etiquetas", true)]
    // Revogada de quem o papel dava.
    [InlineData("gestor", nameof(Permissao.AnonimizarContato), "-anonimizar_contato", false)]
    // O dono não se tranca fora.
    [InlineData("dono", nameof(Permissao.AnonimizarContato), "-anonimizar_contato", true)]
    [InlineData("dono", nameof(Permissao.ConfigurarEmpresa), "-configurar_empresa", true)]
    // Indelegável não se concede, nem com o claim na mão.
    [InlineData("vendedor", nameof(Permissao.GerenciarEquipe), "+gerenciar_equipe", false)]
    [InlineData("vendedor", nameof(Permissao.ConfigurarEmpresa), "+configurar_empresa", false)]
    public async Task A_POLITICA_DECIDE_PELA_EXCECAO_DO_TOKEN(
        string papel, string politica, string excecao, bool entra)
    {
        var autorizacao = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(PoliticasDePermissao.Registrar)
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();

        var r = await autorizacao.AuthorizeAsync(Cracha(papel, excecao), null, politica);

        Assert.Equal(entra, r.Succeeded);
    }

    /// <summary>===================== A TELA E A ROTA LEEM A MESMA LISTA =====================
    ///
    /// As duas camadas já divergiram neste projeto: a importação aceitava o gestor no servidor, e a
    /// tela, escrita com `ehDono`, escondia dele o botão. Com permissão por pessoa há mais o que
    /// divergir, então a afirmação passa a ser célula por célula: para cada papel, cada exceção
    /// possível e cada gesto, o que o `GET /auth/permissoes` devolve tem de ser EXATAMENTE o que a
    /// política deixa passar.
    ///
    /// ⚠️ A VOLTA É COMPLETA, PELO `GeradorToken` E PELO `ValidateToken`, e isso não é zelo: se o
    /// `GeradorToken` parar de emitir o claim de exceção, todo mundo volta em silêncio à base do
    /// papel, nenhuma exceção vale nada — e NENHUM teste que monta o crachá à mão quebraria,
    /// porque a base é o caminho de fallback. Este é o único teste que pega isso.
    ///
    /// ⚠️ `ValidateToken`, E NÃO `ReadJwtToken`. O `ReadJwtToken` (que `LoginDbTests` usa) não
    /// aplica o mapeamento de claims da ENTRADA, então `FindFirstValue(ClaimTypes.Role)` sobre o
    /// resultado dele pode voltar nulo — o papel foi escrito com o nome curto na serialização.
    /// ==============================================================================</summary>
    [Fact]
    public void O_QUE_A_TELA_RECEBE_E_O_QUE_A_ROTA_DEIXA_PASSAR_SAO_A_MESMA_LISTA()
    {
        var autorizacao = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(PoliticasDePermissao.Registrar)
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();

        var divergencias = new List<string>();

        foreach (var papel in new[] { "dono", "gestor", "vendedor" })
        foreach (var excecoes in CadaExcecaoPossivel())
        {
            // O crachá sai do token DE VERDADE — emitido e validado.
            var cracha = ChegouNoServidor(new UsuarioAutenticado(
                1, "Ana", "ana@x.com", papel, 7, "Loja", excecoes));

            var daTela = new AuthController(null!, null!).Permissoes(
                new ContextoDeCracha(cracha));

            foreach (var gesto in Enum.GetValues<Permissao>())
            {
                var naTela = daTela.Contains(Permissoes.NaApi(gesto));
                var naRota = autorizacao
                    .AuthorizeAsync(cracha, null, gesto.ToString())
                    .GetAwaiter().GetResult().Succeeded;

                if (naTela != naRota)
                    divergencias.Add(
                        $"{papel} + [{Rotulo(excecoes)}] / {Permissoes.NaApi(gesto)}: " +
                        $"tela={naTela}, rota={naRota}");
            }
        }

        Assert.True(divergencias.Count == 0,
            "a tela e a rota discordam: " + string.Join("; ", divergencias));
    }

    /// <summary>===================== A EXCEÇÃO SOBREVIVE À IDA E VOLTA DO TOKEN =====================
    ///
    /// ⚠️ ESTE TESTE EXISTE PORQUE O DE PARIDADE NÃO BASTAVA, e isso foi DESCOBERTO sabotando.
    /// Apaguei a emissão do claim no `GeradorToken` e a suíte inteira passou — inclusive a
    /// paridade. O motivo, óbvio depois: sem exceção no token, a tela e a rota leem o mesmo
    /// nada e CONCORDAM perfeitamente. Paridade prova acordo, não prova que a exceção funciona.
    ///
    /// Aqui a afirmação é outra: o que o dono marcou tem de chegar ao outro lado da serialização.
    /// É a única coisa que cai se o claim deixar de ser emitido — e sem ela a feature inteira
    /// poderia ser desligada em silêncio por uma linha removida.
    /// ============================================================================================</summary>
    [Fact]
    public void A_EXCECAO_SOBREVIVE_A_IDA_E_VOLTA_DO_TOKEN()
    {
        var cracha = ChegouNoServidor(new UsuarioAutenticado(
            1, "Rafael", "rafael@x.com", "vendedor", 7, "Loja",
            new Dictionary<Permissao, bool>
            {
                [Permissao.CancelarVenda] = true,      // concedida
                [Permissao.GerenciarEtiquetas] = true  // concedida
            }));

        // Chegou no claim, com sinal e nome da API.
        Assert.Equal(
            new Dictionary<Permissao, bool>
            {
                [Permissao.CancelarVenda] = true,
                [Permissao.GerenciarEtiquetas] = true
            },
            ClaimsDoToken.ExcecoesDe(cracha));

        // E vale: o vendedor passa a poder os dois, e continua sem o resto.
        var daTela = new AuthController(null!, null!).Permissoes(new ContextoDeCracha(cracha));

        Assert.Equal(["cancelar_venda", "gerenciar_etiquetas"], daTela.Order());
    }

    /// <summary>O outro sentido — revogar também tem de atravessar o token. Um gestor sem
    /// `ver_numeros_da_equipe` sai do login com as outras quatro e sem essa.</summary>
    [Fact]
    public void A_REVOGACAO_SOBREVIVE_A_IDA_E_VOLTA_DO_TOKEN()
    {
        var cracha = ChegouNoServidor(new UsuarioAutenticado(
            2, "Beatriz", "bia@x.com", "gestor", 7, "Loja",
            new Dictionary<Permissao, bool> { [Permissao.VerNumerosDaEquipe] = false }));

        var daTela = new AuthController(null!, null!).Permissoes(new ContextoDeCracha(cracha));

        Assert.Equal(
            ["anonimizar_contato", "cancelar_venda", "importar_contatos", "ver_historico"],
            daTela.Order());
    }

    /// <summary>Nenhuma exceção, e depois cada gesto concedido e cada gesto revogado, um por vez.
    /// Inclui os INDELEGÁVEIS de propósito: é onde se prova que a exceção neles não vale.</summary>
    private static IEnumerable<IReadOnlyDictionary<Permissao, bool>?> CadaExcecaoPossivel()
    {
        yield return null;

        foreach (var gesto in Enum.GetValues<Permissao>())
        {
            yield return new Dictionary<Permissao, bool> { [gesto] = true };
            yield return new Dictionary<Permissao, bool> { [gesto] = false };
        }
    }

    private static string Rotulo(IReadOnlyDictionary<Permissao, bool>? excecoes) =>
        excecoes is null
            ? "sem exceção"
            : string.Join(",", excecoes.Select(e =>
                (e.Value ? "+" : "-") + Permissoes.NaApi(e.Key)));

    /// <summary>Emite o token e o valida, como o JwtBearer faria — é o que faz o claim de exceção
    /// passar pela serialização de verdade em vez de ser montado à mão.</summary>
    private static ClaimsPrincipal ChegouNoServidor(UsuarioAutenticado usuario)
    {
        var opcoes = new OpcoesJwt { Chave = new string('k', 48) };
        var (token, _) = new GeradorToken(opcoes).Gerar(usuario);

        return new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = opcoes.Emissor,
            ValidAudience = opcoes.Audiencia,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opcoes.Chave)),
            ClockSkew = TimeSpan.FromMinutes(1)
        }, out _);
    }

    /// <summary>Um `IContextoEmpresa` que lê do crachá, igual ao `ContextoEmpresaHttp` — e pela
    /// MESMA `ClaimsDoToken`, que é o que impede as duas camadas de lerem o claim diferente.</summary>
    private sealed class ContextoDeCracha(ClaimsPrincipal cracha) : IContextoEmpresa
    {
        public long EmpresaId => 7;
        public long UsuarioId => 1;
        public string? Papel => ClaimsDoToken.PapelDe(cracha);
        public IReadOnlyDictionary<Permissao, bool>? ExcecoesDePermissao =>
            ClaimsDoToken.ExcecoesDe(cracha);
        public bool EstaAutenticado => true;
    }

    /// <summary>O crachá que o `GeradorToken` produz: o papel, mais uma linha por exceção.</summary>
    private static ClaimsPrincipal Cracha(string papel, params string[] excecoes) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, papel),
                .. excecoes.Select(e => new Claim(ClaimsDoToken.TipoExcecoes, e))
            ],
            authenticationType: "teste"));

    private static List<Permissao> PodeO(string papel) =>
        Enum.GetValues<Permissao>().Where(p => Permissoes.Pode(papel, p)).ToList();
}
