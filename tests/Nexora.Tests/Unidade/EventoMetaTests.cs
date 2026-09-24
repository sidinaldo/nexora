using System.Text.Json.Nodes;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;

namespace Nexora.Tests.Unidade;

/// <summary>O CORPO DO EVENTO DA META, e o hash que vai dentro dele (INT-4).
///
/// ===================== POR QUE ESTAS REGRAS PRECISAM DE TESTE DURO =====================
/// Nenhuma delas dá erro quando é violada. A Meta responde 200, o evento entra, e o casamento
/// simplesmente não acontece — o cliente vê "0 correspondências" num painel que ele não abre, e
/// conclui que o produto não funciona.
///
/// É a pior classe de defeito que este bloco pode ter: invisível de dentro, irreversível de fora.
/// ======================================================================================</summary>
public class EventoMetaTests
{
    private static readonly Guid Evento = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime Quando = new(2026, 3, 12, 14, 0, 0, DateTimeKind.Utc);

    // ==================================================================== hash
    [Fact]
    public void O_EMAIL_VAI_EM_MINUSCULO_E_SEM_ESPACO()
    {
        // A Meta normaliza assim do lado dela: `Joao@X.com` e `joao@x.com` são a MESMA pessoa, e
        // dois hashes diferentes são duas pessoas diferentes para ela.
        var esperado = HashPessoal.Email("joao@exemplo.com");

        Assert.Equal(esperado, HashPessoal.Email("  JOAO@Exemplo.COM  "));

        // hex minúsculo, 64 caracteres — o formato que ela aceita.
        Assert.Matches("^[0-9a-f]{64}$", esperado!);
    }

    [Fact]
    public void O_TELEFONE_REUSA_O_CANONICALIZADOR_DO_PROJETO()
    {
        // ⚠️ UMA DEFINIÇÃO DE "O MESMO TELEFONE". O formato que o Nexora já guarda
        // (`5584988887777`) É o que a Meta pede; uma segunda normalização aqui criaria duas, e a
        // divergência apareceria como lead que não casa.
        var esperado = HashPessoal.Telefone("5584988887777");

        Assert.Equal(esperado, HashPessoal.Telefone("(84) 98888-7777"));
        Assert.Equal(esperado, HashPessoal.Telefone("84 98888 7777"));
        Assert.Matches("^[0-9a-f]{64}$", esperado!);
    }

    [Fact]
    public void CAMPO_VAZIO_VIRA_AUSENTE__E_NUNCA_O_HASH_DA_STRING_VAZIA()
    {
        // ⚠️ `sha256("")` é a constante `e3b0c442…`. Mandá-la faria TODO lead sem e-mail casar com
        // todo lead sem e-mail do mundo — e o resultado não é "não casou": é casou com a pessoa
        // errada, e a Meta aprendendo com um público que não existe.
        const string HashDoVazio = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        foreach (var vazio in new string?[] { null, "", "   " })
        {
            Assert.Null(HashPessoal.Email(vazio));
            Assert.Null(HashPessoal.Telefone(vazio));
        }

        // E o corpo montado não tem a chave nenhuma — não a tem com o hash do vazio dentro.
        var corpo = Corpo(new FatoDeConversao(TipoConversao.Lead, Evento, Quando));
        var usuario = corpo["data"]![0]!["user_data"]!.AsObject();

        Assert.False(usuario.ContainsKey("em"));
        Assert.False(usuario.ContainsKey("ph"));
        Assert.DoesNotContain(HashDoVazio, corpo.ToJsonString());
    }

    [Fact]
    public void TELEFONE_ILEGIVEL_NAO_VIRA_HASH_DE_LIXO()
    {
        // "quero saber o preço" num campo de telefone é o que o formulário de site recebe todo dia.
        // Hashear isso mandaria para a Meta um identificador que não é de ninguém.
        Assert.Null(HashPessoal.Telefone("quero saber o preco"));
        Assert.Null(HashPessoal.Telefone("123"));
    }

    // ==================================================================== o corpo
    [Fact]
    public void O_CORPO_TEM_OS_QUATRO_OBRIGATORIOS_DA_META()
    {
        var corpo = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando, Telefone: "5584988887777"));

        var evento = corpo["data"]![0]!;

        Assert.Equal("Lead", (string)evento["event_name"]!);
        Assert.Equal("chat", (string)evento["action_source"]!);
        Assert.Equal(Evento.ToString(), (string)evento["event_id"]!);
        Assert.NotNull(evento["user_data"]);

        // `event_time` em SEGUNDOS desde a época, não milissegundos: em ms a data cai no ano 57.000
        // e a Meta recusa a requisição inteira por evento fora da janela.
        Assert.Equal(new DateTimeOffset(Quando).ToUnixTimeSeconds(), (long)evento["event_time"]!);
    }

    [Fact]
    public void O_CORPO_NAO_LEVA_O_TOKEN_NEM_O_CODIGO_DE_TESTE()
    {
        // ⚠️ DUAS RAZÕES, e as duas importam. O payload guardado aparece NA TELA do cliente, no
        // registro de conversões — token ali é credencial em captura de tela de suporte. E trocar o
        // token não pode invalidar o que já está na fila, o que aconteceria se ele fosse parte do
        // corpo congelado.
        var texto = MontadorEventoMeta.Montar(
            new FatoDeConversao(TipoConversao.Lead, Evento, Quando, Telefone: "5584988887777"));

        Assert.DoesNotContain("access_token", texto);
        Assert.DoesNotContain("test_event_code", texto);
    }

    [Fact]
    public void UM_EVENTO_POR_REQUISICAO()
    {
        // A Meta aceita até 1.000 por chamada, mas UM evento inválido recusa o lote inteiro. Com um
        // por requisição, o lead da padaria não se perde porque o da farmácia veio sem telefone.
        var corpo = Corpo(new FatoDeConversao(TipoConversao.Lead, Evento, Quando));

        Assert.Single(corpo["data"]!.AsArray());
    }

    [Fact]
    public void IP_USER_AGENT_FBP_E_FBC_VAO_EM_CLARO()
    {
        // ⚠️ O ERRO MAIS SILENCIOSO DE TODOS: hasheá-los parece mais seguro, a Meta responde 200, e
        // o casamento com o clique deixa de acontecer sem aviso nenhum.
        var corpo = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando,
            Ip: "203.0.113.7", UserAgent: "Mozilla/5.0 (iPhone)",
            Fbp: "fb.1.1700000000.111", Fbc: "fb.1.1700000000.IwAR-abc"));

        var usuario = corpo["data"]![0]!["user_data"]!;

        Assert.Equal("203.0.113.7", (string)usuario["client_ip_address"]!);
        Assert.Equal("Mozilla/5.0 (iPhone)", (string)usuario["client_user_agent"]!);
        Assert.Equal("fb.1.1700000000.111", (string)usuario["fbp"]!);
        Assert.Equal("fb.1.1700000000.IwAR-abc", (string)usuario["fbc"]!);
    }

    [Fact]
    public void O_EMAIL_E_O_TELEFONE_VAO_EM_ARRAY_DE_UM_ELEMENTO()
    {
        // É o formato da Meta: ela aceita mais de um valor por campo, e um escalar onde ela espera
        // lista é campo ignorado — sem erro.
        var corpo = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando,
            Email: "joao@exemplo.com", Telefone: "5584988887777"));

        var usuario = corpo["data"]![0]!["user_data"]!;

        Assert.Single(usuario["em"]!.AsArray());
        Assert.Single(usuario["ph"]!.AsArray());
        Assert.Equal(HashPessoal.Email("joao@exemplo.com"), (string)usuario["em"]![0]!);
        Assert.Equal(HashPessoal.Telefone("5584988887777"), (string)usuario["ph"]![0]!);
    }

    // ==================================================================== action_source
    [Fact]
    public void A_ORIGEM_DIZ_ONDE_O_FATO_ACONTECEU()
    {
        // `website` exige `event_source_url`; os outros não. Errar aqui é mandar um evento que a
        // Meta recusa por campo obrigatório ausente — ou que ela aceita descrevendo o canal errado.
        Assert.Equal("website",
            MontadorEventoMeta.Origem(TipoConversao.Lead, FonteRastreio.FormularioSite));
        Assert.Equal("chat",
            MontadorEventoMeta.Origem(TipoConversao.Lead, FonteRastreio.AnuncioWhatsapp));

        // Sem rastro: o lead entrou pelo WhatsApp, ou por um formulário antigo. `chat` é o canal
        // real da maioria deste público.
        Assert.Equal("chat", MontadorEventoMeta.Origem(TipoConversao.Lead, null));

        // ⚠️ A COMPRA É `system_generated` SEMPRE. Ela acontece quando um vendedor arrasta um card,
        // dias depois, dentro do CRM — nenhum canal a observou. `website` herdaria a URL da visita
        // original e seria falso: não é lá que a venda aconteceu.
        Assert.Equal("system_generated",
            MontadorEventoMeta.Origem(TipoConversao.Compra, FonteRastreio.FormularioSite));
        Assert.Equal("system_generated",
            MontadorEventoMeta.Origem(TipoConversao.Compra, null));
    }

    [Fact]
    public void A_URL_DA_PAGINA_SO_VAI_EM_EVENTO_DE_SITE()
    {
        var doSite = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando,
            Pagina: "https://cliente.com.br/promo", Fonte: FonteRastreio.FormularioSite));

        Assert.Equal("https://cliente.com.br/promo",
            (string)doSite["data"]![0]!["event_source_url"]!);

        // Na compra, não: `event_source_url` é obrigatório só em `website`, e mandá-lo em
        // `system_generated` é mais um dado saindo daqui sem servir para nada.
        var daCompra = Corpo(new FatoDeConversao(
            TipoConversao.Compra, Evento, Quando, Valor: 900m,
            Pagina: "https://cliente.com.br/promo", Fonte: FonteRastreio.FormularioSite));

        Assert.Null(daCompra["data"]![0]!["event_source_url"]);
    }

    // ==================================================================== o valor
    [Fact]
    public void A_COMPRA_VAI_COM_VALOR_E_EM_REAL()
    {
        // ⚠️ É O DADO QUE MUDA A NATUREZA DO EVENTO. Sem valor, a Meta sabe que houve venda e não
        // sabe quanto — e "otimizar por valor de compra" deixa de existir como opção para o cliente.
        var corpo = Corpo(new FatoDeConversao(
            TipoConversao.Compra, Evento, Quando, Valor: 1450.50m));

        var dados = corpo["data"]![0]!["custom_data"]!;

        Assert.Equal(1450.50m, (decimal)dados["value"]!);
        Assert.Equal("BRL", (string)dados["currency"]!);
        Assert.Equal("Purchase", (string)corpo["data"]![0]!["event_name"]!);
    }

    [Fact]
    public void O_LEAD_NAO_LEVA_VALOR__NEM_QUANDO_ALGUEM_PASSA_UM()
    {
        // Lead com valor seria a Meta otimizando por "gente que pediu orçamento de R$ 900", que é um
        // número que ninguém confirmou. O valor só existe quando a venda fecha.
        var corpo = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando, Valor: 900m));

        Assert.Null(corpo["data"]![0]!["custom_data"]);
    }

    [Fact]
    public void COMPRA_SEM_VALOR_NAO_MANDA_CUSTOM_DATA_VAZIO()
    {
        // `custom_data: { value: 0 }` diria à Meta que a venda foi de zero real — pior que não
        // dizer nada.
        foreach (var valor in new decimal?[] { null, 0m })
            Assert.Null(Corpo(new FatoDeConversao(
                TipoConversao.Compra, Evento, Quando, Valor: valor))["data"]![0]!["custom_data"]);
    }

    private static JsonNode Corpo(FatoDeConversao fato) =>
        JsonNode.Parse(MontadorEventoMeta.Montar(fato))!;
}
