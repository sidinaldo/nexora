using Nexora.Core.Captacao;
using Nexora.Infra.Captacao;

namespace Nexora.Tests.Unidade;

/// <summary>O FORMATO DO CÓDIGO e o desenho do QR.
///
/// Testes puros, sem banco: é a regra que os dois lados do sistema precisam aplicar igual — quem
/// GERA (a tela de canais) e quem LÊ (o webhook). Se as duas leituras divergirem, o sintoma é um
/// canal que nunca atribui nada, sem erro em lugar nenhum.</summary>
public class CodigoCanalTests
{
    // ==================================================================== formato
    [Fact]
    public void Codigo_gerado_tem_o_tamanho_e_o_alfabeto_declarados()
    {
        // Mil sorteios: o suficiente para uma vogal ou um `l` escaparem se alguém mexer no
        // alfabeto sem pensar no material impresso.
        for (var i = 0; i < 1000; i++)
        {
            var c = CodigoCanal.Gerar();
            Assert.Equal(CodigoCanal.Tamanho, c.Length);
            Assert.All(c, ch => Assert.Contains(ch, CodigoCanal.Alfabeto));
        }
    }

    [Fact]
    public void O_ALFABETO_NAO_TEM_VOGAL_NEM_CARACTERE_AMBIGUO()
    {
        // ===== POR QUE ISTO É TESTE =====
        // Sem vogais, o código não forma palavra — e ele vai IMPRESSO em panfleto de cliente.
        // Sem `l`/`0`/`1`, quem digita à mão não erra o par que mais se confunde.
        //
        // É o tipo de restrição que alguém remove de boa-fé para "ter mais combinações", sem
        // saber por que estava lá.
        Assert.All("aeiou", v => Assert.DoesNotContain(v, CodigoCanal.Alfabeto));
        Assert.All("l01", v => Assert.DoesNotContain(v, CodigoCanal.Alfabeto));
        Assert.Equal(CodigoCanal.Alfabeto.Length, CodigoCanal.Alfabeto.Distinct().Count());
    }

    // ==================================================================== extração
    [Theory]
    [InlineData("Olá! Tenho interesse. #k7m2", "k7m2")]
    [InlineData("#k7m2", "k7m2")]
    [InlineData("oi #k7m2 tudo bem?", "k7m2")]
    [InlineData("bom dia!!! #k7m2.", "k7m2")]           // pontuação colada depois não atrapalha
    [InlineData("Olá! Tenho interesse. #K7M2", "k7m2")] // caixa alta: a pessoa digitou à mão
    public void Codigo_e_encontrado_no_texto(string texto, string esperado) =>
        Assert.Equal([esperado], CodigoCanal.Extrair(texto));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("oi, quero um orçamento")]
    [InlineData("k7m2")]                    // SEM `#` não é código: seria atribuir por acidente
    [InlineData("#k7m")]                    // curto demais
    [InlineData("#k7m2x")]                  // longo demais — hashtag de campanha, não código
    [InlineData("#k7m2abcd")]
    [InlineData("#aeio")]                   // fora do alfabeto (vogais)
    [InlineData("#promo")]
    public void Texto_sem_codigo_valido_nao_devolve_nada(string? texto) =>
        Assert.Empty(CodigoCanal.Extrair(texto));

    [Fact]
    public void DOIS_CODIGOS_SAEM_NA_ORDEM_DO_TEXTO()
    {
        // A pessoa colou a mensagem de um amigo e escreveu a dela. O PRIMEIRO é o caminho que ela
        // percorreu — deixar o banco escolher tornaria a atribuição dependente da ordem física
        // das linhas, que ninguém controla.
        Assert.Equal(["k7m2", "b3nx"], CodigoCanal.Extrair("veio do #k7m2, o outro era #b3nx"));
    }

    [Fact]
    public void Codigo_repetido_conta_uma_vez_so() =>
        Assert.Equal(["k7m2"], CodigoCanal.Extrair("#k7m2 #k7m2 #k7m2"));

    [Fact]
    public void Mensagem_com_muitas_hashtags_nao_vira_consulta_gigante()
    {
        // Texto colado ou script. O corte existe para uma mensagem hostil não custar uma consulta
        // por hashtag.
        var texto = string.Join(' ', Enumerable.Range(0, 200).Select(_ => $"#{CodigoCanal.Gerar()}"));
        Assert.True(CodigoCanal.Extrair(texto).Count <= 5);
    }

    // ==================================================================== o texto do link
    [Fact]
    public void O_TEXTO_TEM_FRASE_NATURAL_ANTES_DO_CODIGO()
    {
        // ===== A DECISÃO QUE MAIS AFETA A TAXA DE ATRIBUIÇÃO =====
        // "Olá! Tenho interesse. #k7m2" tem muito mais chance de ser enviado inteiro que um código
        // solto: a pessoa lê uma saudação que faz sentido, reconhece como sua, e manda. Um campo
        // com só `#k7m2` parece lixo e é apagado — e aí não há atribuição nenhuma.
        var texto = CodigoCanal.TextoDoLink("k7m2");

        Assert.EndsWith("#k7m2", texto);
        Assert.True(texto.IndexOf('#') > 10, "o código tem que vir DEPOIS de uma frase de verdade");
        Assert.Equal([("k7m2")], CodigoCanal.Extrair(texto));   // e o que sai é lido de volta
    }

    // ==================================================================== o QR desenhado
    [Fact]
    public void O_QR_GERADO_DECODIFICA_DE_VOLTA_PARA_O_MESMO_LINK()
    {
        // ===== O TESTE QUE SUBSTITUI (EM PARTE) O CELULAR =====
        // Um leitor INDEPENDENTE (ZXing) lê o PNG que o endpoint devolve e tem que devolver o link
        // exato. É isto que pega o `#` não escapado na URL: o WhatsApp receberia a frase truncada
        // e o código nunca chegaria — o link "funcionaria" e a atribuição nunca aconteceria.
        var link = "https://wa.me/5584988887777?text="
                 + Uri.EscapeDataString(CodigoCanal.TextoDoLink("k7m2"));

        var png = new GeradorQrCoder().Png(link);

        Assert.Equal(link, LeitorPngQr.Ler(png));
    }

    [Fact]
    public void O_SVG_gerado_e_um_SVG_e_muda_com_o_conteudo()
    {
        var gerador = new GeradorQrCoder();

        var a = gerador.Svg("https://wa.me/5584988887777?text=a");
        var b = gerador.Svg("https://wa.me/5584988887777?text=b");

        Assert.StartsWith("<svg", a.TrimStart());
        Assert.Contains("viewBox", a);
        // Sem esta segunda asserção, um gerador que devolvesse sempre a mesma imagem passaria.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void O_PNG_gerado_tem_assinatura_de_PNG()
    {
        var png = new GeradorQrCoder().Png("https://wa.me/5584988887777?text=oi");

        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47], [.. png.Take(4)]);

        // E escala com o parâmetro: PNG de tela e PNG de apresentação não são o mesmo arquivo.
        var maior = new GeradorQrCoder().Png("https://wa.me/5584988887777?text=oi", 24);
        Assert.True(maior.Length > png.Length);
    }

    // ==================================================================== a mensagem editavel
    /// <summary>===================== A FRASE E DO CLIENTE; O CODIGO E NOSSO =====================
    ///
    /// O dono do canal escreve a frase que faz sentido para a campanha dele. O que ele NAO decide
    /// e se o codigo vai junto: sem codigo nao ha atribuicao, e um canal que nao atribui e um
    /// canal que nao serve para nada — que e exatamente o problema que ele existe para resolver.
    ///
    /// Por isso `TextoDoLink` ACRESCENTA, e nao substitui.
    /// ==================================================================================</summary>
    [Fact]
    public void MENSAGEM_PROPRIA_LEVA_O_CODIGO_NO_FIM()
    {
        var texto = CodigoCanal.TextoDoLink("k7m2", "Vi o cartaz na loja e quero o desconto");

        Assert.StartsWith("Vi o cartaz na loja e quero o desconto", texto);
        Assert.EndsWith("#k7m2", texto);

        // E o codigo continua legivel para quem le de volta — e o mesmo caminho do webhook.
        Assert.Equal(["k7m2"], CodigoCanal.Extrair(texto));
    }

    [Fact]
    public void SEM_MENSAGEM_PROPRIA_usa_a_frase_padrao()
    {
        foreach (var vazia in new[] { null, "", "   " })
        {
            var texto = CodigoCanal.TextoDoLink("k7m2", vazia);

            Assert.Contains("interesse", texto, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("#k7m2", texto);
        }
    }

    /// <summary>Quem escreve a frase pode acabar digitando o codigo tambem — a tela mostra o texto
    /// final, e copiar de la para o campo e o caminho natural. Duas vezes seria feio e, pior,
    /// `Extrair` devolveria o mesmo codigo duas vezes.</summary>
    [Fact]
    public void CODIGO_JA_ESCRITO_NA_FRASE_NAO_E_DUPLICADO()
    {
        var texto = CodigoCanal.TextoDoLink("k7m2", "Quero o desconto #k7m2");

        Assert.Equal(1, texto.Split("#k7m2").Length - 1);
    }

    [Fact]
    public void A_FRASE_E_APARADA_nas_pontas()
    {
        Assert.StartsWith("Oi", CodigoCanal.TextoDoLink("k7m2", "   Oi   "));
        Assert.EndsWith("#k7m2", CodigoCanal.TextoDoLink("k7m2", "   Oi   "));
    }

    /// <summary>O teto existe pela MESMA razao do codigo curto: texto pre-preenchido longo parece
    /// spam, e a pessoa apaga tudo antes de enviar — levando o codigo junto.</summary>
    [Fact]
    public void O_LIMITE_E_DECLARADO_E_CABE_UMA_FRASE_DE_VERDADE()
    {
        Assert.Equal(120, CodigoCanal.LimiteMensagem);
    }
}
