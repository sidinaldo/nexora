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

    // ================================================ nome e external_id (o que a Meta pediu)
    [Fact]
    public void O_NOME_VIRA_fn_E_ln__MINUSCULOS_E_HASHEADOS()
    {
        // A Meta mede: +15% de qualidade de correspondência para cada um. A normalização é dela —
        // minúsculo, sem pontuação, UTF-8 — e o acento FICA: "José" é "josé", que é como ela guarda
        // o que a própria pessoa digitou no perfil.
        var corpo = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando, Nome: "  Ysia   Braglia  "));

        var usuario = corpo["data"]![0]!["user_data"]!;

        Assert.Equal(HashPessoal.Nome("ysia"), (string)usuario["fn"]![0]!);
        Assert.Equal(HashPessoal.Nome("braglia"), (string)usuario["ln"]![0]!);
    }

    [Fact]
    public void QUEM_TEM_UM_NOME_SO_NAO_GANHA_SOBRENOME()
    {
        // ⚠️ Mandar "Maria" como nome E como sobrenome não é um sobrenome — é ruído, e a Meta casaria
        // contra um campo que não descreve ninguém.
        var usuario = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando, Nome: "Maria"))["data"]![0]!["user_data"]!;

        Assert.NotNull(usuario["fn"]);
        Assert.Null(usuario["ln"]);
    }

    [Fact]
    public void NOME_QUE_E_SO_NUMERO_NAO_VIRA_NOME()
    {
        // O contato criado pelo WhatsApp sem `pushName` recebe o telefone formatado como nome.
        // Hashear "(84) 95278-7173" mandaria para a Meta um "nome" que não é de ninguém.
        var usuario = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando, Nome: "(84) 95278-7173"))["data"]![0]!["user_data"]!;

        Assert.Null(usuario["fn"]);
        Assert.Null(usuario["ln"]);
    }

    [Fact]
    public void A_NORMALIZACAO_DO_NOME_TIRA_PONTUACAO_E_DIGITO__E_MANTEM_ACENTO()
    {
        // ⚠️ ESTE TESTE NASCEU DE UMA SABOTAGEM QUE NAO PEGOU NADA. O outro usava
        // "(84) 95278-7173", e `NomeDePessoa.Primeiro` já descarta pedaço sem letra — então a
        // limpeza do `HashPessoal` nunca era exercitada. Nome com letra E pontuação é o caso que
        // separa as duas responsabilidades.
        Assert.Equal(HashPessoal.Nome("dávila"), HashPessoal.Nome("D'Ávila"));
        Assert.Equal(HashPessoal.Nome("mariaclara"), HashPessoal.Nome("Maria-Clara"));
        Assert.Equal(HashPessoal.Nome("ana"), HashPessoal.Nome("Ana 2"));

        // ⚠️ O ACENTO FICA. É a regra da Meta (UTF-8), e é como ela guarda o que a própria pessoa
        // digitou no perfil: "josé" e "jose" são duas pessoas diferentes para ela.
        Assert.NotEqual(HashPessoal.Nome("José"), HashPessoal.Nome("Jose"));

        // Sem letra nenhuma não é nome.
        Assert.Null(HashPessoal.Nome("---"));
        Assert.Null(HashPessoal.Nome("2026"));
    }

    [Fact]
    public void O_external_id_E_O_MESMO_PARA_A_MESMA_PESSOA__E_MUDA_ENTRE_EMPRESAS()
    {
        // ⚠️ É O ELO QUE NÃO DEPENDE DE ANÚNCIO: amarra o `Lead` e a `Compra` da mesma pessoa mesmo
        // sem `fbc` nenhum — o caso de quem chegou pelo WhatsApp. A Meta mede +28%.
        var daEmpresa7 = HashPessoal.Externo(7, 1002);

        Assert.Equal(daEmpresa7, HashPessoal.Externo(7, 1002));   // estável
        Assert.NotEqual(daEmpresa7, HashPessoal.Externo(8, 1002)); // outra empresa, outro cadastro
        Assert.NotEqual(daEmpresa7, HashPessoal.Externo(7, 1003));

        // ⚠️ NÃO É O ID CRU. Um inteiro pequeno em claro é uma tabela que qualquer um precomputa.
        Assert.DoesNotContain("1002", daEmpresa7);
        Assert.Matches("^[0-9a-f]{64}$", daEmpresa7);

        var usuario = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando, ExternalId: daEmpresa7))["data"]![0]!["user_data"]!;

        Assert.Equal(daEmpresa7, (string)usuario["external_id"]![0]!);
    }

    [Fact]
    public void SEM_NOME_E_SEM_external_id_AS_CHAVES_NEM_APARECEM()
    {
        var usuario = Corpo(new FatoDeConversao(TipoConversao.Lead, Evento, Quando))
            ["data"]![0]!["user_data"]!.AsObject();

        Assert.False(usuario.ContainsKey("fn"));
        Assert.False(usuario.ContainsKey("ln"));
        Assert.False(usuario.ContainsKey("external_id"));
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

        // ⚠️ A COMPRA É `system_generated` SEMPRE — e o teste REAL contra a Graph API confirmou que
        // a Meta aceita e contabiliza assim. Ela acontece quando um vendedor arrasta um card, dias
        // depois, dentro do CRM: nenhum canal a observou.
        Assert.Equal("system_generated",
            MontadorEventoMeta.Origem(TipoConversao.Compra, FonteRastreio.FormularioSite));
        Assert.Equal("system_generated",
            MontadorEventoMeta.Origem(TipoConversao.Compra, null));
    }

    // ============================================ o que o primeiro teste real ensinou (INT-4)
    [Fact]
    public void business_messaging_EXIGE_O_CLIQUE_E_A_PAGINA__OS_DOIS()
    {
        // ⚠️ AS TRÊS RECUSAS DA META, uma de cada vez, no primeiro envio de verdade:
        //   2804063 — `messaging_channel` ausente (eu o pus dentro de `user_data`)
        //   2804066 — nome `Lead` inválido com esta origem
        //   2804116 — falta `page_id` ou `whatsapp_business_account_id` em `user_data`
        //
        // Sem os dois identificadores, `chat` — porque evento recusado vale menos que evento
        // aceito com casamento mais grosso.
        Assert.Equal("chat", MontadorEventoMeta.Origem(
            TipoConversao.Lead, FonteRastreio.AnuncioWhatsapp, ctwaClid: "clique", paginaId: null));

        Assert.Equal("chat", MontadorEventoMeta.Origem(
            TipoConversao.Lead, FonteRastreio.AnuncioWhatsapp, ctwaClid: null, paginaId: "999"));

        Assert.Equal("business_messaging", MontadorEventoMeta.Origem(
            TipoConversao.Lead, FonteRastreio.AnuncioWhatsapp, ctwaClid: "clique", paginaId: "999"));
    }

    [Fact]
    public void COM_business_messaging_O_LEAD_VIRA_LeadSubmitted()
    {
        // A Meta recusa o nome `Lead` com esta origem (`2804066`) e sugere `LeadSubmitted`. É o tipo
        // de detalhe que nenhum dublê pega: só a resposta dela ensina.
        var corpo = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando, Telefone: "5584988887777",
            Fonte: FonteRastreio.AnuncioWhatsapp, CtwaClid: "clique", PaginaId: "778899"));

        var evento = corpo["data"]![0]!;

        Assert.Equal("LeadSubmitted", (string)evento["event_name"]!);
        Assert.Equal("business_messaging", (string)evento["action_source"]!);

        // ⚠️ `messaging_channel` NO EVENTO, não em `user_data` — foi o primeiro erro que ela
        // devolveu, e o campo estava lá dentro o tempo todo.
        Assert.Equal("whatsapp", (string)evento["messaging_channel"]!);
        Assert.Null(evento["user_data"]!["messaging_channel"]);

        Assert.Equal("clique", (string)evento["user_data"]!["ctwa_clid"]!);
        Assert.Equal("778899", (string)evento["user_data"]!["page_id"]!);
    }

    [Fact]
    public void FORA_DO_business_messaging_NADA_DISSO_APARECE()
    {
        // `messaging_channel` e `page_id` em evento de site seriam campos que a Meta ignora — e mais
        // um dado saindo daqui sem servir para nada.
        var corpo = Corpo(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando, Telefone: "5584988887777",
            Fonte: FonteRastreio.FormularioSite, PaginaId: "778899"));

        var evento = corpo["data"]![0]!;

        Assert.Equal("Lead", (string)evento["event_name"]!);
        Assert.Equal("website", (string)evento["action_source"]!);
        Assert.Null(evento["messaging_channel"]);
        Assert.Null(evento["user_data"]!["page_id"]);
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

    // ==================================================================== o rebaixamento
    /* ===================== A OUTRA METADE DA REGRA DO `Origem` =====================
       O `Origem` já aplica "um evento recusado vale menos que um aceito com casamento mais grosso"
       — mas só na IDA, quando o dado está AUSENTE. Quando a Meta RECUSA o clique ou a página, o
       corpo já foi montado e guardado, e sem o rebaixamento a conversão morre.

       Os três subcódigos que caem aqui foram descobertos falando com a Graph API de verdade; a
       documentação não traz nenhum deles.
       ============================================================================= */

    [Fact]
    public void O_REBAIXAMENTO_TIRA_O_CAMINHO_DO_ANUNCIO_E_MANTEM_O_RESTO()
    {
        var original = MontadorEventoMeta.Montar(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando, Telefone: "5584988887777",
            Fonte: FonteRastreio.AnuncioWhatsapp, CtwaClid: "clique", PaginaId: "778899"));

        var evento = JsonNode.Parse(MontadorEventoMeta.RebaixarParaChat(original)!)!["data"]![0]!;

        Assert.Equal("chat", (string)evento["action_source"]!);

        // ⚠️ O NOME VOLTA JUNTO. `LeadSubmitted` só existe no caminho do anúncio, e a Meta recusa um
        // `LeadSubmitted` que não seja `business_messaging` — trocar um sem o outro trocaria uma
        // recusa por outra.
        Assert.Equal("Lead", (string)evento["event_name"]!);

        Assert.Null(evento["messaging_channel"]);
        Assert.Null(evento["user_data"]!["ctwa_clid"]);
        Assert.Null(evento["user_data"]!["page_id"]);

        // ⚠️ O MESMO FATO, E O MESMO ID. A Meta deduplica por `event_name` + `event_id`; inventar um
        // id novo faria o evento contar duas vezes para quem tem pixel no site. E o telefone — que é
        // o que faz o casamento por `chat` funcionar — continua lá.
        Assert.Equal(Evento.ToString(), (string)evento["event_id"]!);
        Assert.Equal(new DateTimeOffset(Quando).ToUnixTimeSeconds(), (long)evento["event_time"]!);
        Assert.Equal(HashPessoal.Telefone("5584988887777"), (string)evento["user_data"]!["ph"]![0]!);
    }

    [Fact]
    public void REBAIXAR_DUAS_VEZES_DEVOLVE_NULO_NA_SEGUNDA()
    {
        // ⚠️ É O QUE IMPEDE O LAÇO. Depois do rebaixamento o corpo é `chat`, então não há o que
        // rebaixar — e o motor trata o nulo como "desisti", em vez de reenfileirar para sempre.
        var original = MontadorEventoMeta.Montar(new FatoDeConversao(
            TipoConversao.Lead, Evento, Quando, Telefone: "5584988887777",
            Fonte: FonteRastreio.AnuncioWhatsapp, CtwaClid: "clique", PaginaId: "778899"));

        var uma = MontadorEventoMeta.RebaixarParaChat(original);
        Assert.NotNull(uma);
        Assert.Null(MontadorEventoMeta.RebaixarParaChat(uma));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("""{"data":[]}""")]
    public void O_QUE_NAO_E_EVENTO_DE_ANUNCIO_NAO_REBAIXA(string? corpo)
    {
        // Nunca lança: o corpo vem da nossa tabela, mas uma linha velha de antes de uma mudança de
        // formato não pode derrubar a rodada inteira.
        Assert.Null(MontadorEventoMeta.RebaixarParaChat(corpo));
    }

    // ==================================================================== a política do erro
    [Theory]
    [InlineData(PoliticaConversao.CliqueInvalido)]
    [InlineData(PoliticaConversao.PaginaAusente)]
    [InlineData(PoliticaConversao.PaginaInvalida)]
    public void OS_SUBCODIGOS_DO_ANUNCIO_REBAIXAM_EM_VEZ_DE_DESISTIR(int subcodigo)
    {
        // Os três vêm com `code: 100`, que sozinho manda desistir — e desistir joga fora uma
        // conversão que sairia perfeitamente bem como `chat`.
        var decisao = PoliticaConversao.Classificar(100, subcodigo);

        Assert.True(decisao.Rebaixar);
        Assert.True(decisao.TentarDeNovo);
        Assert.False(decisao.DesativarCredencial);
    }

    [Fact]
    public void O_CODIGO_100_SEM_SUBCODIGO_CONHECIDO_CONTINUA_DESISTINDO()
    {
        // ⚠️ O CONTROLE. Sem ele, "rebaixar sempre que der 100" passaria — e aí todo corpo malformado
        // viraria uma segunda tentativa inútil, mascarando defeito nosso como degradação.
        foreach (var subcodigo in new int?[] { null, 999999 })
        {
            var decisao = PoliticaConversao.Classificar(100, subcodigo);
            Assert.False(decisao.Rebaixar);
            Assert.False(decisao.TentarDeNovo);
        }
    }

    [Fact]
    public void O_TOKEN_MORTO_CONTINUA_DESATIVANDO_A_CREDENCIAL()
    {
        // O subcódigo entrou na frente do código na classificação; este teste garante que ele não
        // passou na frente do que já decidia certo.
        var decisao = PoliticaConversao.Classificar(190);

        Assert.True(decisao.DesativarCredencial);
        Assert.False(decisao.Rebaixar);
    }

    private static JsonNode Corpo(FatoDeConversao fato) =>
        JsonNode.Parse(MontadorEventoMeta.Montar(fato))!;
}
