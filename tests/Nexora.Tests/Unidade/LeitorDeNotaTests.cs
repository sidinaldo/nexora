using Nexora.Core.Texto;

namespace Nexora.Tests.Unidade;

/// <summary>===================== "ISTO É UMA NOTA?" =====================
///
/// ⚠️ ERRAR PARA O LADO DE "É NOTA" É PIOR QUE ERRAR PARA O OUTRO, e é o que esta suíte guarda.
/// Nota inventada entra no relatório; nota BAIXA inventada dispara lembrete para o vendedor e
/// aviso para o dono — o cliente é incomodado por uma reclamação que ele não fez.
///
/// A tabela do prompt está aqui inteira, caso por caso. Os que não vieram dela estão marcados.
/// ==============================================================</summary>
public class LeitorDeNotaTests
{
    private static NotaLida Ler(string texto) => LeitorDeNota.Ler(texto, citouAPesquisa: false);

    // ==================================================================== a tabela do prompt

    [Theory]
    [InlineData("10", 10)]
    [InlineData("nota 9", 9)]
    [InlineData("9!", 9)]
    [InlineData("dou 8 pra vocês", 8)]
    [InlineData("10/10", 10)]
    public void OS_CASOS_QUE_SAO_NOTA(string texto, int esperada)
    {
        var lida = Ler(texto);

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(esperada, lida.Nota);
    }

    [Fact]
    public void O_TEXTO_DEPOIS_DA_NOTA_VIRA_COMENTARIO()
    {
        var lida = Ler("10, adorei o atendimento");

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(10, lida.Nota);
        // Sem a vírgula na frente: ", adorei o atendimento" na tela pareceria texto cortado.
        Assert.Equal("adorei o atendimento", lida.Comentario);
    }

    /// <summary>Comentário em branco na tela é pior que comentário ausente: parece que o cliente
    /// escreveu algo que não apareceu.
    ///
    /// ⚠️ `"10 !"` E `"nota 9 ."` NÃO SÃO ENFEITE. Os três primeiros casos saem pela guarda de
    /// "não há palavra depois do número"; só estes dois chegam ao trecho que transforma sobra de
    /// pontuação em nulo — e sem eles a sabotagem daquele trecho não derrubava nada.</summary>
    [Theory]
    [InlineData("10")]
    [InlineData("nota 9")]
    [InlineData("9!")]
    [InlineData("10 !")]
    [InlineData("nota 9 .")]
    public void NOTA_SEM_TEXTO_NAO_INVENTA_COMENTARIO(string texto)
    {
        var lida = Ler(texto);

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Null(lida.Comentario);
    }

    /// <summary>⚠️ A VÍRGULA SEPARADA DO NÚMERO É OUTRO CAMINHO. Em `"10, adorei"` a vírgula está
    /// DENTRO do token `"10,"` e o comentário já nasce limpo; em `"10 , adorei"` ela é um token
    /// próprio e abre o comentário. Só este caso exercita a limpeza — medi a sabotagem dela e,
    /// sem este teste, ela não derrubava nada.</summary>
    [Fact]
    public void PONTUACAO_SOLTA_NAO_ABRE_O_COMENTARIO()
    {
        var lida = Ler("10 , adorei o atendimento");

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(10, lida.Nota);
        Assert.Equal("adorei o atendimento", lida.Comentario);
    }

    /// <summary>⚠️ 11 ESTÁ FORA DA ESCALA, e "ótimo" não tem número. Os dois são mensagem normal —
    /// a pesquisa continua aberta e o cliente ainda pode responder.</summary>
    [Theory]
    [InlineData("11")]
    [InlineData("ótimo")]
    [InlineData("chego às 10h")]
    public void OS_CASOS_QUE_NAO_SAO_NOTA(string texto)
    {
        var lida = Ler(texto);

        Assert.Equal(LeituraDeNota.NaoEhNota, lida.Resultado);
        Assert.Null(lida.Nota);
    }

    /// <summary>===================== "10h" É HORA, NÃO NOTA =====================
    /// Dígito colado em letra não é candidato — e é por isso que não existe lista de sufixos para
    /// manter. "10h", "2x", "8gb", "3kg": todos caem pela mesma regra.
    /// ================================================================</summary>
    [Theory]
    [InlineData("chego às 10h")]
    [InlineData("quero 2x do mesmo")]
    [InlineData("o de 8gb")]
    public void DIGITO_COLADO_EM_LETRA_NAO_E_CANDIDATO(string texto)
    {
        Assert.Equal(LeituraDeNota.NaoEhNota, Ler(texto).Resultado);
    }

    /// <summary>"quero 2 unidades" NÃO É NOTA — é o critério de aceite do prompt. Mas tem um
    /// número solto numa frase de três palavras, então vai para o humano em vez de ser jogada
    /// fora: é exatamente o que a `PossivelNota` existe para fazer.</summary>
    [Fact]
    public void QUERO_2_UNIDADES_NAO_E_NOTA_E_VAI_PARA_O_HUMANO()
    {
        var lida = Ler("quero 2 unidades");

        Assert.NotEqual(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(LeituraDeNota.PossivelNota, lida.Resultado);
        Assert.Equal(2, lida.Nota);
    }

    [Fact]
    public void CITAR_A_PESQUISA_E_RESPONDER_7_DA_SETE()
    {
        var lida = LeitorDeNota.Ler("7", citouAPesquisa: true);

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(7, lida.Nota);
    }

    /// <summary>===================== A CITAÇÃO VENCE OS OUTROS CAMINHOS =====================
    ///
    /// ⚠️ O TESTE ACIMA NÃO PROVA NADA SOZINHO, e eu medi: com o caminho da citação DESLIGADO,
    /// `"7"` continua dando 7 pelo caminho normal — o teste passava verde com a regra removida.
    ///
    /// Este usa um texto que o caminho normal NÃO aceitaria: "quero 2 unidades" é `PossivelNota`
    /// quando chega solta. Citando a pesquisa, ela é a resposta àquela mensagem, e o prompt põe a
    /// citação em PRIMEIRO lugar na ordem das regras.
    ///
    /// ⚠️ ISTO É UMA TENSÃO ASSUMIDA, não um descuido: a mesma frase tem dois destinos conforme
    /// citar ou não. A citação é um gesto deliberado sobre AQUELA mensagem, e é o sinal mais forte
    /// que existe aqui — mais forte que a estranheza do texto.
    /// ==========================================================================</summary>
    [Fact]
    public void A_CITACAO_VENCE_O_QUE_SOZINHO_SERIA_SO_UMA_DUVIDA()
    {
        var solta = LeitorDeNota.Ler("quero 2 unidades", citouAPesquisa: false);
        var citada = LeitorDeNota.Ler("quero 2 unidades", citouAPesquisa: true);

        Assert.Equal(LeituraDeNota.PossivelNota, solta.Resultado);
        Assert.Equal(LeituraDeNota.Nota, citada.Resultado);
        Assert.Equal(2, citada.Nota);
    }

    // ==================================================================== além da tabela

    /// <summary>===================== O CASO QUE DERRUBOU "NÚMERO NA PRIMEIRA POSIÇÃO" =====================
    ///
    /// ⚠️ "2 caixas chegaram quebradas" tem o número na posição ZERO, igual a "10, adorei", e é
    /// uma reclamação sobre quantidade. Com a regra "número que abre a mensagem é nota", ela
    /// virava NOTA 2 — detrator, que cria lembrete para o vendedor e avisa o dono por uma
    /// reclamação de entrega que ninguém classificou.
    ///
    /// O que separa é a PONTUAÇÃO depois do número: "10," anuncia que o número acabou; "2 caixas"
    /// usa o número para contar caixas.
    /// ============================================================================================</summary>
    [Fact]
    public void NUMERO_QUE_CONTA_A_PALAVRA_SEGUINTE_NAO_E_NOTA()
    {
        var lida = Ler("2 caixas chegaram quebradas");

        Assert.NotEqual(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(LeituraDeNota.PossivelNota, lida.Resultado);
    }

    /// <summary>E a mesma frase com a vírgula É nota: a pontuação é o sinal, não a posição.</summary>
    [Fact]
    public void O_MESMO_NUMERO_COM_PONTUACAO_E_NOTA()
    {
        var lida = Ler("2, caixas chegaram quebradas");

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(2, lida.Nota);
    }

    /// <summary>===================== NÃO HÁ TETO DE TAMANHO NO CAMINHO DA NOTA =====================
    ///
    /// ⚠️ A PRIMEIRA VERSÃO TINHA UM TETO DE 12 PALAVRAS e ele jogava fora exatamente o que a
    /// pesquisa quer colher: um dez com elogio escrito. O puxador e a pontuação já são sinal forte
    /// o bastante; o tamanho não acrescenta nada.
    /// ======================================================================================</summary>
    [Fact]
    public void ELOGIO_LONGO_DEPOIS_DA_NOTA_CONTINUA_SENDO_NOTA()
    {
        var texto = "10, vocês foram muito atenciosos e entregaram antes do prazo combinado, "
            + "recomendo a todos os meus amigos";

        var lida = Ler(texto);

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(10, lida.Nota);
        Assert.StartsWith("vocês foram muito atenciosos", lida.Comentario);
    }

    /// <summary>⚠️ DOIS NÚMEROS NÃO GERAM DÚVIDA, GERAM RUÍDO. "é nota 2 ou nota 6?" não é
    /// pergunta que o vendedor saiba responder — o prompt diz "apenas se não houver OUTRO número
    /// na frase", e aqui está o caso.</summary>
    [Fact]
    public void DOIS_NUMEROS_NA_FRASE_NAO_VIRAM_POSSIVEL_NOTA()
    {
        Assert.Equal(LeituraDeNota.NaoEhNota, Ler("mandei 2 de 6").Resultado);
    }

    /// <summary>Frase longa com número solto é assunto, não nota em dúvida — o teto de 6 palavras
    /// do prompt.</summary>
    [Fact]
    public void FRASE_LONGA_COM_NUMERO_SOLTO_NAO_VIRA_POSSIVEL_NOTA()
    {
        var curta = "preciso de 3 caixas amanhã";                    // 5 palavras
        var longa = "preciso de 3 caixas amanhã de manhã por favor"; // 8 palavras

        Assert.Equal(LeituraDeNota.PossivelNota, Ler(curta).Resultado);
        Assert.Equal(LeituraDeNota.NaoEhNota, Ler(longa).Resultado);
        Assert.Equal(6, LeitorDeNota.MaximoDePalavrasParaPossivel);
    }

    /// <summary>Zero é nota, e é a mais importante de não perder: é o detrator extremo. Um teste
    /// porque "se (nota)" em vez de "se (nota != null)" trataria 0 como ausência.</summary>
    [Fact]
    public void ZERO_E_UMA_NOTA_VALIDA()
    {
        var lida = Ler("0");

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(0, lida.Nota);
    }

    /// <summary>⚠️ O DIVISOR SÓ VALE CONTRA O TOPO DA ESCALA. "9/10" é nota; "2/3" é uma fração
    /// qualquer, e tratá-la como nota 2 inventaria um detrator a partir de "2/3 do pedido
    /// chegou".</summary>
    [Theory]
    [InlineData("9/10", 9)]
    [InlineData("10/10", 10)]
    public void NOTA_SOBRE_DEZ_E_NOTA(string texto, int esperada)
    {
        var lida = Ler(texto);

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(esperada, lida.Nota);
    }

    /// <summary>⚠️ "2/3" SOZINHO, e não "2/3 do pedido chegou". A primeira versão usava a frase
    /// inteira, e a sabotagem que aceita qualquer denominador NÃO a derrubava: com a frase, o
    /// número cai no caminho da `PossivelNota` e eu só afirmava "não é Nota" — que continua
    /// verdade. Sozinho, o divisor é a última palavra, e aí o caminho da nota decide.</summary>
    [Theory]
    [InlineData("2/3")]
    [InlineData("1/2")]
    public void FRACAO_QUE_NAO_E_SOBRE_DEZ_NAO_E_NOTA(string texto)
    {
        var lida = Ler(texto);

        Assert.Equal(LeituraDeNota.NaoEhNota, lida.Resultado);
        Assert.Null(lida.Nota);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MENSAGEM_VAZIA_NAO_E_NOTA(string? texto)
    {
        Assert.Equal(LeituraDeNota.NaoEhNota, LeitorDeNota.Ler(texto, false).Resultado);
    }

    /// <summary>Áudio, imagem e figurinha chegam sem texto. A pesquisa continua aberta — e NÃO
    /// pode ser marcada como respondida, senão o cliente que mandou um áudio dizendo "dez" perde
    /// a vez sem ninguém ler.</summary>
    [Fact]
    public void MENSAGEM_SEM_TEXTO_DEIXA_A_PESQUISA_ABERTA()
    {
        var lida = LeitorDeNota.Ler(null, citouAPesquisa: true);

        Assert.Equal(LeituraDeNota.NaoEhNota, lida.Resultado);
    }

    /// <summary>Os puxadores, um por um. A lista é curta DE PROPÓSITO: cada palavra aqui é uma
    /// licença para transformar número em nota, e uma lista generosa ("acho", "foi") pegaria
    /// "acho 3 caixas" e "foi 2 dias".</summary>
    [Theory]
    [InlineData("nota 7", 7)]
    [InlineData("dou 10", 10)]
    [InlineData("daria 6 pra ser sincero", 6)]
    [InlineData("minha nota 5", 5)]
    public void OS_PUXADORES_LIBERAM_O_NUMERO_SEM_PONTUACAO(string texto, int esperada)
    {
        var lida = Ler(texto);

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(esperada, lida.Nota);
    }

    [Theory]
    [InlineData("acho 3 caixas suficiente")]
    [InlineData("foi 2 dias de atraso")]
    public void PALAVRA_QUE_NAO_E_PUXADOR_NAO_LIBERA(string texto)
    {
        Assert.NotEqual(LeituraDeNota.Nota, Ler(texto).Resultado);
    }

    /// <summary>===================== O PUXADOR FRACO NAO CONTA A PALAVRA SEGUINTE =====================
    ///
    /// ⚠️ O CASO DA REVISAO. "dou 5 estrelas" e a nota MAXIMA numa escala de cinco, e virava nota 5
    /// — um detrator, com aviso ao dono e a mensagem de desculpas ao cliente. "meu 2 pedidos
    /// chegaram errados" virava nota 2.
    ///
    /// Nao viram nota CERTA; viram DUVIDA, e o vendedor decide. E o lugar desses textos.
    /// ===========================================================================================</summary>
    [Theory]
    [InlineData("dou 5 estrelas", 5)]
    [InlineData("meu 2 pedidos chegaram errados", 2)]
    [InlineData("dei 3 caixas pra ele", 3)]
    [InlineData("minha 1 encomenda atrasou", 1)]
    public void O_PUXADOR_FRACO_SEGUIDO_DE_PALAVRA_VIRA_DUVIDA_E_NAO_NOTA(string texto, int numero)
    {
        var lida = Ler(texto);

        Assert.Equal(LeituraDeNota.PossivelNota, lida.Resultado);
        Assert.Equal(numero, lida.Nota);
    }

    /// <summary>O outro lado, e e ele que impede o conserto de ir longe demais: o FORTE continua
    /// liberando o numero seguido de texto, e o fraco continua liberando com ponte, no fim, ou com
    /// pontuacao.</summary>
    [Theory]
    [InlineData("nota 9 muito bom", 9)]
    [InlineData("notas 10 pro atendimento", 10)]
    [InlineData("minha nota 5 pela demora", 5)]
    [InlineData("dou 8 a vocês", 8)]
    [InlineData("dei 10, adorei", 10)]
    [InlineData("meu 7", 7)]
    public void O_PUXADOR_FORTE_E_O_FRACO_COM_PONTE_CONTINUAM_SENDO_NOTA(string texto, int esperada)
    {
        var lida = Ler(texto);

        Assert.Equal(LeituraDeNota.Nota, lida.Resultado);
        Assert.Equal(esperada, lida.Nota);
    }
}
