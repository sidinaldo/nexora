using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Nexora.Tests.Unidade;

/// <summary>===================== NENHUMA ROTA MUDOU DE DONO NA TROCA =====================
///
/// As rotas escreviam o papel à mão (`[Authorize(Roles = "dono")]`) e passaram a dizer o GESTO
/// (`[Authorize(Policy = nameof(Permissao.ConfigurarEmpresa))]`), com os papéis saindo da tabela
/// de `Permissoes`. É uma troca de ONDE a regra mora, não de QUEM pode — e a única forma de provar
/// isso é comparar rota a rota com a fotografia tirada ANTES.
///
/// A lista abaixo é essa fotografia: cada rota restrita, com os papéis que a acessavam. Toda rota
/// fora dela é "qualquer um autenticado". O teste falha nos dois sentidos — rota restrita que abriu
/// e rota aberta que fechou —, e é o que protege a próxima mudança na tabela de passar sem ninguém
/// perceber que ela mexeu numa rota que não devia.
///
/// ⚠️ MUDOU A TABELA DE PROPÓSITO? Atualize a linha aqui no MESMO commit. O diff desta lista é o
/// resumo, legível, de quem ganhou ou perdeu acesso a quê.
/// ======================================================================================</summary>
public class RotasPorPermissaoTests
{
    private static readonly Dictionary<string, string> Restritas = new()
    {
        ["AuthController.Login"] = "SEM-AUTHORIZE",
        ["CadastroController.Criar"] = "anonimo",
        ["CanaisController.Alternar"] = "dono",
        ["CanaisController.Atualizar"] = "dono",
        ["CanaisController.Criar"] = "dono",
        ["CanaisController.Listar"] = "dono",
        ["CanaisController.Png"] = "dono",
        ["CanaisController.Remover"] = "dono",
        ["CanaisController.Svg"] = "dono",
        ["CapturaController.Receber"] = "anonimo",
        ["ConexoesController.Conectar"] = "dono",
        ["ConexoesController.Criar"] = "dono",
        ["ConexoesController.Desconectar"] = "dono",
        ["ConexoesController.Listar"] = "dono",
        ["ConexoesController.Obter"] = "dono",
        ["ConexoesController.Parear"] = "dono",
        ["ConexoesController.ReconhecerTroca"] = "dono",
        ["ConexoesController.Remover"] = "dono",
        ["ConexoesController.Renomear"] = "dono",
        ["ConexoesController.Saude"] = "dono",
        ["ConexoesController.Status"] = "dono",
        ["ConfiguracaoController.AtualizarAtendimento"] = "dono",
        ["ConfiguracaoController.AtualizarDados"] = "dono",
        ["ContatosController.Anonimizar"] = "dono,gestor",
        ["ConviteController.Aceitar"] = "anonimo",
        ["ConviteController.Info"] = "anonimo",
        ["DemonstracaoController.Semear"] = "anonimo",
        ["DevController.Limpar"] = "dono",
        ["DevController.Semear"] = "dono",
        ["DevController.SemearConversas"] = "dono",
        ["EquipeController.Atualizar"] = "dono",
        ["EquipeController.Convidar"] = "dono",
        ["EquipeController.Listar"] = "dono",
        ["EquipeController.Reenviar"] = "dono",
        ["EquipeController.ResetSenha"] = "dono",
        ["EtapasController.Atualizar"] = "dono",
        ["EtapasController.Criar"] = "dono",
        ["EtapasController.DefinirGanho"] = "dono",
        ["EtapasController.Listar"] = "dono",
        ["EtapasController.Remover"] = "dono",
        ["EtapasController.Reordenar"] = "dono",
        ["EtiquetasController.Atualizar"] = "dono",
        ["EtiquetasController.Criar"] = "dono",
        ["EtiquetasController.Remover"] = "dono",
        ["FeriadosController.Criar"] = "dono,gestor",
        ["FeriadosController.Ignorar"] = "dono",
        ["FeriadosController.Reativar"] = "dono",
        ["FeriadosController.Remover"] = "dono",
        ["FormulariosController.Alternar"] = "dono",
        ["FormulariosController.Atualizar"] = "dono",
        ["FormulariosController.Criar"] = "dono",
        ["FormulariosController.Listar"] = "dono",
        ["FormulariosController.Regerar"] = "dono",
        ["OnboardingController.Dispensar"] = "dono",
        ["OnboardingController.DispensarEquipe"] = "dono",
        ["PipelinesController.Atualizar"] = "dono",
        ["PipelinesController.Criar"] = "dono",
        ["PipelinesController.DefinirPadrao"] = "dono",
        ["PipelinesController.Remover"] = "dono",
        ["RedefinicaoController.Info"] = "anonimo",
        ["RedefinicaoController.Redefinir"] = "anonimo",
        ["RedefinicaoController.Solicitar"] = "anonimo",
        ["WebhookController.Evolution"] = "SEM-AUTHORIZE",
        ["WebhooksSaidaController.Obter"] = "dono",
        ["WebhooksSaidaController.Reenviar"] = "dono",
        ["WebhooksSaidaController.Regerar"] = "dono",
        ["WebhooksSaidaController.Remover"] = "dono",
        ["WebhooksSaidaController.Salvar"] = "dono",
        ["WebhooksSaidaController.Testar"] = "dono",
    };

    [Fact]
    public void CADA_ROTA_ACEITA_OS_MESMOS_PAPEIS_DE_ANTES()
    {
        var atuais = Rotas().Where(r => r.Value != "autenticado").ToDictionary();

        var abriram = Restritas.Keys.Except(atuais.Keys).Order().ToList();
        var fecharam = atuais.Keys.Except(Restritas.Keys).Order().ToList();
        var mudaram = Restritas.Keys.Intersect(atuais.Keys)
            .Where(k => Restritas[k] != atuais[k])
            .Select(k => $"{k}: era {Restritas[k]}, agora {atuais[k]}")
            .Order().ToList();

        Assert.True(abriram.Count == 0, "rotas que ABRIRAM para qualquer autenticado: " + string.Join("; ", abriram));
        Assert.True(fecharam.Count == 0, "rotas que FECHARAM: " + string.Join("; ", fecharam.Select(k => $"{k} ({atuais[k]})")));
        Assert.True(mudaram.Count == 0, "rotas que mudaram de papel: " + string.Join("; ", mudaram));
    }

    /// <summary>⚠️ NENHUMA ROTA ESCREVE PAPEL À MÃO. Um `Roles = "dono"` novo passaria longe da
    /// tabela — e o painel, que só lê a tabela, ofereceria (ou esconderia) o gesto errado.</summary>
    [Fact]
    public void NENHUMA_ROTA_ESCREVE_O_PAPEL_A_MAO()
    {
        var aMao = Acoes()
            .Where(a => a.Atributos.OfType<AuthorizeAttribute>().Any(x => x.Roles is not null))
            .Select(a => a.Nome).ToList();

        Assert.True(aMao.Count == 0, "use `Policy = nameof(Permissao.X)`: " + string.Join("; ", aMao));
    }

    // ==================================================================== apoio
    private static IEnumerable<(string Nome, List<object> Atributos)> Acoes()
    {
        var asm = typeof(Nexora.Api.Controllers.ContatosController).Assembly;

        foreach (var t in asm.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!m.GetCustomAttributes().OfType<HttpMethodAttribute>().Any()) continue;
            yield return ($"{t.Name}.{m.Name}",
                t.GetCustomAttributes(true).Concat(m.GetCustomAttributes(true)).ToList());
        }
    }

    /// <summary>Quem acessa cada rota — o cálculo mora em `PapeisDaRota`, o mesmo que os testes de
    /// cada tela usam para perguntar "isto é só do dono?".</summary>
    private static Dictionary<string, string> Rotas() =>
        Acoes().ToDictionary(a => a.Nome, a => PapeisDaRota.Calcular(a.Atributos));
}
