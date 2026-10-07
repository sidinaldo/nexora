using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Controllers;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;

namespace Nexora.Tests.Unidade;

/// <summary>A origem chega à listagem de contatos com o nome da API (`meta_ads`), e o enum é
/// `MetaAds` (AUD-XX). Valor inventado é recusado com 400, e não ignorado em silêncio — filtro que
/// some sem aviso mostraria a lista inteira com cara de filtrada.</summary>
public class OrigemNaListaDeContatosTests
{
    /// <summary>Grava a origem que a listagem recebeu. A interface tem dezenas de métodos; só a
    /// listagem interessa aqui, e qualquer outra chamada falha alto.</summary>
    public class GravadorDeContatos : DispatchProxy
    {
        public OrigemLead? Origem { get; private set; }
        public bool Chamado { get; private set; }

        protected override object? Invoke(MethodInfo? metodo, object?[]? args)
        {
            if (metodo?.Name != nameof(IServicoContatos.ListarAsync))
            {
                throw new NotImplementedException(metodo?.Name);
            }

            Chamado = true;
            Origem = (OrigemLead?)args![4];
            return Task.FromResult<PaginaContatos>(null!);
        }
    }

    private static (ContatosController Controller, GravadorDeContatos Gravador) Montar()
    {
        var servico = DispatchProxy.Create<IServicoContatos, GravadorDeContatos>();
        return (new ContatosController(servico, null!), (GravadorDeContatos)(object)servico);
    }

    [Theory]
    [InlineData("meta_ads", OrigemLead.MetaAds)]
    [InlineData("instagram", OrigemLead.Instagram)]
    [InlineData("WhatsApp", OrigemLead.Whatsapp)]
    public async Task O_NOME_DA_API_CASA_COM_O_ENUM(string texto, OrigemLead esperada)
    {
        var (controller, gravador) = Montar();

        await controller.Listar(origem: texto);

        Assert.Equal(esperada, gravador.Origem);
    }

    [Fact]
    public async Task SEM_ORIGEM_NAO_HA_FILTRO()
    {
        var (controller, gravador) = Montar();

        await controller.Listar();

        Assert.True(gravador.Chamado);
        Assert.Null(gravador.Origem);
    }

    [Theory]
    [InlineData("tiktok")]
    [InlineData("7")]
    public async Task ORIGEM_INVENTADA_E_RECUSADA(string texto)
    {
        var (controller, gravador) = Montar();

        var resposta = await controller.Listar(origem: texto);

        Assert.IsType<BadRequestObjectResult>(resposta);
        Assert.False(gravador.Chamado);
    }
}
