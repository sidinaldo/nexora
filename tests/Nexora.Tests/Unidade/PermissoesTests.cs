using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Api.Controllers;
using Nexora.Api.Seguranca;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Tests.Integracao;

namespace Nexora.Tests.Unidade;

/// <summary>A TABELA DE QUEM PODE O QUÊ — a que trava as rotas, os serviços e o que o painel
/// oferece. O teste de rotas prova que nenhuma rota mudou; este prova o que a tabela diz, e o que
/// chega à tela.</summary>
public class PermissoesTests
{
    /// <summary>A troca não mudou ninguém de lugar: o dono pode tudo, o gestor pode o que já podia
    /// (importar, cancelar venda, ver o histórico, anonimizar, cadastrar feriado, ver a equipe
    /// inteira), e o vendedor nada disso.</summary>
    [Fact]
    public void CADA_PAPEL_PODE_O_QUE_JA_PODIA()
    {
        Assert.Equal(Enum.GetValues<Permissao>(), PodeO("dono"));

        Assert.Equal(
        [
            Permissao.ImportarContatos, Permissao.CancelarVenda, Permissao.VerHistorico,
            Permissao.AnonimizarContato, Permissao.CadastrarFeriado, Permissao.VerNumerosDaEquipe
        ], PodeO("gestor"));

        Assert.Empty(PodeO("vendedor"));
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
            "configurar_empresa", "gerenciar_equipe", "importar_contatos", "cancelar_venda",
            "ver_historico", "anonimizar_contato", "cadastrar_feriado", "ver_numeros_da_equipe"
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
    /// TOKEN, o mesmo que as rotas conferem.</summary>
    [Fact]
    public void A_SESSAO_ABERTA_PERGUNTA_E_RECEBE_DO_PAPEL_DO_TOKEN()
    {
        var controller = new AuthController(null!, null!);

        Assert.Equal(["configurar_empresa", "gerenciar_equipe", "importar_contatos", "cancelar_venda",
                      "ver_historico", "anonimizar_contato", "cadastrar_feriado", "ver_numeros_da_equipe"],
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

    private static List<Permissao> PodeO(string papel) =>
        Enum.GetValues<Permissao>().Where(p => Permissoes.Pode(papel, p)).ToList();
}
