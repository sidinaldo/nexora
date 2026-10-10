using System.Net;
using Nexora.Core.Email;
using Nexora.Core.Resumo;

namespace Nexora.Tests.Unidade;

/// <summary>O E-MAIL DO RESUMO DE ONTEM (RES-XX). Funcao pura: o que importa e o que o dono le.</summary>
public class ResumoDiarioEmailTests
{
    private static ResumoDiario Resumo(
        int vendas = 2, decimal valor = 1500m, int naoSairam = 3, IReadOnlyList<MotivoDeFalha>? motivos = null,
        int respostas = 2, string empresa = "Loja Centro") => new(
        Dia: new DateOnly(2026, 8, 6), Empresa: empresa,
        LeadsNovos: 12, Vendas: vendas, ValorVendido: valor,
        AguardandoResposta: 4, LembretesDeHoje: 5,
        AutomaticasEnviadas: 7, AutomaticasNaoEnviadas: naoSairam,
        Motivos: motivos ?? [new("O WhatsApp está desconectado.", 2), new("Passou do prazo sem sair.", 1)],
        RespostasPesquisa: respostas, Promotores: 1, Detratores: 1);

    [Fact]
    public void O_ASSUNTO_DIZ_O_DIA_E_A_EMPRESA()
    {
        var email = MontadorEmail.ResumoDiario("dono@loja.com", "Maria Souza", Resumo(), "https://painel.nexora.app");

        Assert.Equal("Resumo de 06/08 — Loja Centro", email.Assunto);
        Assert.Equal("resumo_diario", email.Tipo);
        Assert.Equal("dono@loja.com", email.Destinatario);
    }

    [Fact]
    public void CADA_NUMERO_ESTA_NO_HTML_E_NO_TEXTO()
    {
        var email = MontadorEmail.ResumoDiario("dono@loja.com", "Maria Souza", Resumo(), "https://painel.nexora.app");

        // O HTML vem escapado (acento vira entidade): decodificado, tem de dizer o mesmo que o texto.
        foreach (var corpo in new[] { WebUtility.HtmlDecode(email.Html), email.Texto })
        {
            Assert.Contains("Bom dia, Maria!", corpo);
            Assert.Contains("1.500,00", corpo);
            Assert.Contains("7 enviadas · 3 não saíram", corpo);
            Assert.Contains("O WhatsApp está desconectado. (2)", corpo);
            Assert.Contains("2 respostas · 1 promotor, 1 detrator", corpo);
            Assert.Contains("https://painel.nexora.app", corpo);
        }
    }

    /// <summary>Dia parado nao vira linha vazia: diz "nenhuma", e o bloco de motivos some.</summary>
    [Fact]
    public void DIA_SEM_VENDA_SEM_FALHA_E_SEM_RESPOSTA_DIZ_NENHUMA()
    {
        var email = MontadorEmail.ResumoDiario("dono@loja.com", "Maria",
            Resumo(vendas: 0, valor: 0, naoSairam: 0, motivos: [], respostas: 0), "https://painel.nexora.app");

        Assert.Contains("Vendas fechadas: nenhuma", email.Texto);
        Assert.Contains("Mensagens automáticas: 7 enviadas", email.Texto);
        Assert.Contains("Pesquisa pós-venda: nenhuma resposta", email.Texto);
        Assert.DoesNotContain("Por que não saíram", email.Html);
    }

    /// <summary>O nome da empresa e texto do cliente: entra escapado no HTML.</summary>
    [Fact]
    public void O_NOME_DA_EMPRESA_ENTRA_ESCAPADO()
    {
        var email = MontadorEmail.ResumoDiario("dono@loja.com", "Maria",
            Resumo(empresa: "Loja <b>Centro</b>"), "https://painel.nexora.app");

        Assert.Contains("Loja &lt;b&gt;Centro&lt;/b&gt;", email.Html);
        Assert.DoesNotContain("<b>Centro</b>", email.Html);
    }
}
