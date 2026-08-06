using System.Net;
using System.Text.Json;
using Nexora.Core.Entidades;
using Nexora.Core.Webhooks;

namespace Nexora.Tests.Unidade;

/// <summary>AS REGRAS PURAS DO WEBHOOK DE SAÍDA (INT-3).
///
/// Assinatura, política de retry, formato do payload e o guarda de SSRF. Nada aqui toca em banco
/// ou rede — e é justamente por isso que estas são as regras que precisam estar certas: quando
/// falham, falham em silêncio do outro lado da internet, no servidor de outra empresa.</summary>
public class WebhookSaidaTests
{
    private const string Segredo = "segredo-de-teste-0123456789abcdef";

    // ==================================================================== assinatura
    [Fact]
    public void A_ASSINATURA_CONFERE_CONTRA_O_CORPO_EXATO()
    {
        var corpo = """{"versao":1,"evento":"lead.criado"}""";
        const long ts = 1780000000;

        var assinatura = AssinaturaWebhook.Calcular(Segredo, ts, corpo);

        Assert.StartsWith("sha256=", assinatura);
        Assert.True(AssinaturaWebhook.Confere(Segredo, ts, corpo, assinatura));

        // ===== O QUE A ASSINATURA TEM QUE PEGAR =====
        // Um byte a mais no corpo é o ataque inteiro: "venda.fechada, R$ 400" virando R$ 40.000
        // no ERP do cliente. Se a assinatura não mudar com o corpo, ela não protege nada.
        Assert.False(AssinaturaWebhook.Confere(Segredo, ts, corpo + " ", assinatura));
        Assert.False(AssinaturaWebhook.Confere(Segredo, ts, corpo.Replace("1", "2"), assinatura));
    }

    [Fact]
    public void O_TIMESTAMP_ENTRA_NA_ASSINATURA_E_E_O_QUE_FECHA_O_REPLAY()
    {
        // ===== POR QUE NÃO ASSINAR SÓ O CORPO =====
        // Assinando só o corpo, quem capturou uma entrega válida a reenvia amanhã com qualquer
        // timestamp e a assinatura continua conferindo — o receptor não teria como distinguir.
        // Com `{timestamp}.{corpo}` o par fica amarrado.
        var corpo = """{"versao":1}""";

        var assinatura = AssinaturaWebhook.Calcular(Segredo, 1780000000, corpo);

        Assert.False(AssinaturaWebhook.Confere(Segredo, 1780000001, corpo, assinatura));
        Assert.Contains("1780000000.", AssinaturaWebhook.BaseAssinada(1780000000, corpo));
    }

    [Fact]
    public void Segredo_errado_nao_confere()
    {
        var corpo = """{"versao":1}""";
        var assinatura = AssinaturaWebhook.Calcular(Segredo, 1, corpo);

        Assert.False(AssinaturaWebhook.Confere("outro-segredo", 1, corpo, assinatura));
        Assert.False(AssinaturaWebhook.Confere(Segredo, 1, corpo, null));
        Assert.False(AssinaturaWebhook.Confere(Segredo, 1, corpo, ""));
    }

    [Fact]
    public void Segredo_gerado_tem_entropia_e_nao_repete()
    {
        var segredos = Enumerable.Range(0, 100).Select(_ => AssinaturaWebhook.GerarSegredo()).ToList();

        Assert.All(segredos, s => Assert.Equal(64, s.Length));   // 32 bytes em hex
        Assert.Equal(100, segredos.Distinct().Count());
    }

    // ==================================================================== retry
    [Fact]
    public void O_BACKOFF_E_1_5_30_E_PARA_NA_TERCEIRA()
    {
        // Os três espaçamentos cobrem três falhas distintas e reais: o deploy do cliente, a queda
        // curta e a manutenção. Depois disso não é mais intermitência — é o sistema fora do ar, e
        // esperar não resolve.
        Assert.Equal(TimeSpan.FromMinutes(1), PoliticaEntrega.EsperaApos(1));
        Assert.Equal(TimeSpan.FromMinutes(5), PoliticaEntrega.EsperaApos(2));

        // Terceira falha: NÃO há próxima. É o que impede um receptor quebrado de virar uma fila
        // que só cresce.
        Assert.Null(PoliticaEntrega.EsperaApos(3));
        Assert.Null(PoliticaEntrega.EsperaApos(4));
        Assert.Equal(3, PoliticaEntrega.MaximoTentativas);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, true)]
    [InlineData(204, true)]
    // 202 é a resposta CORRETA de um receptor assíncrono ("aceitei, processo depois"). Exigir 200
    // quebraria justamente quem faz certo.
    [InlineData(202, true)]
    [InlineData(301, false)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(500, false)]
    public void Qualquer_2xx_conta_como_aceito(int codigo, bool esperado) =>
        Assert.Equal(esperado, PoliticaEntrega.Aceitou(codigo));

    [Fact]
    public void O_timeout_e_curto()
    {
        // Receptor que passa de 10s não vai melhorar esperando 60 — e a espera segura um slot da
        // rodada que outros eventos precisam.
        Assert.Equal(TimeSpan.FromSeconds(10), PoliticaEntrega.Timeout);
        Assert.Equal(30, PoliticaEntrega.DiasDeRetencao);
    }

    // ==================================================================== SSRF
    [Theory]
    [InlineData("https://webhook.cliente.com.br/nexora")]
    [InlineData("https://n8n.exemplo.com/webhook/abc?x=1")]
    [InlineData("https://8.8.8.8/hook")]                       // IP público literal: passa
    public void URL_publica_em_https_passa_no_formato(string url) =>
        Assert.True(ValidadorUrlWebhook.ValidarFormato(url).Ok);

    [Theory]
    // http: o payload e a assinatura viajariam em claro — e assinatura em claro se copia.
    [InlineData("http://webhook.cliente.com.br/nexora")]
    [InlineData("ftp://cliente.com.br")]
    [InlineData("//cliente.com.br")]
    [InlineData("")]
    [InlineData("nao é url")]
    public void URL_sem_https_e_recusada(string url) =>
        Assert.False(ValidadorUrlWebhook.ValidarFormato(url).Ok);

    [Theory]
    [InlineData("https://localhost/hook")]
    [InlineData("https://algo.localhost/hook")]
    [InlineData("https://servidor.local/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://127.9.9.9/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://172.16.0.1/hook")]
    [InlineData("https://172.31.255.254/hook")]
    [InlineData("https://192.168.1.1/hook")]
    // ===== O ENDEREÇO QUE MAIS IMPORTA =====
    // 169.254.169.254 é o serviço de METADADOS da nuvem. Sem esta linha, o cliente aponta o
    // webhook para lá e o Nexora entrega as credenciais da própria infraestrutura para ele —
    // autenticado pelo simples fato de a requisição sair de dentro.
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://100.64.0.1/hook")]                    // CGNAT
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fd00::1]/hook")]                     // unique local IPv6
    [InlineData("https://[::ffff:10.0.0.1]/hook")]             // IPv4 privado vestido de IPv6
    public void URL_INTERNA_E_RECUSADA(string url)
    {
        var r = ValidadorUrlWebhook.ValidarFormato(url);
        Assert.False(r.Ok, $"{url} deveria ser recusada");
        Assert.Contains("interno", r.Motivo);
    }

    [Fact]
    public void A_LISTA_DE_IPS_RECUSA_POR_EXCLUSAO_E_ACEITA_O_RESTO()
    {
        // Por exclusão, não por inclusão: enumerar o que é público erraria para o lado perigoso a
        // cada faixa nova que a IANA reservar.
        Assert.True(ValidadorUrlWebhook.EhPublico(IPAddress.Parse("8.8.8.8")));
        Assert.True(ValidadorUrlWebhook.EhPublico(IPAddress.Parse("172.32.0.1")));   // fora do /12
        Assert.True(ValidadorUrlWebhook.EhPublico(IPAddress.Parse("2606:4700::1")));

        Assert.False(ValidadorUrlWebhook.EhPublico(IPAddress.Parse("0.0.0.0")));
        Assert.False(ValidadorUrlWebhook.EhPublico(IPAddress.Parse("224.0.0.1")));   // multicast
        Assert.False(ValidadorUrlWebhook.EhPublico(IPAddress.Parse("172.20.0.1")));  // dentro do /12
    }

    [Fact]
    public async Task NOME_PUBLICO_QUE_RESOLVE_PARA_IP_PRIVADO_E_RECUSADO()
    {
        // ===== POR QUE VALIDAR NA ENTREGA, E NÃO SÓ NO CADASTRO =====
        // O formato passa: `https://` e um nome comum. Quem decide o destino é o DNS, e a zona é
        // do CLIENTE — ele cadastra apontando para um IP público e troca depois, sem tocar no
        // Nexora. Validar só na entrada é validar um valor que o outro lado pode mudar.
        var dns = new DnsFalso { ["webhook.cliente.com"] = ["10.0.0.5"] };

        Assert.True(ValidadorUrlWebhook.ValidarFormato("https://webhook.cliente.com/hook").Ok);

        var r = await ValidadorUrlWebhook.ValidarAsync("https://webhook.cliente.com/hook", dns, default);
        Assert.False(r.Ok);
        Assert.Contains("interno", r.Motivo);
    }

    [Fact]
    public async Task Nome_com_um_IP_publico_e_um_privado_e_recusado()
    {
        // O truque: o cliente mostra o IP público na demonstração, e o servidor pode escolher o
        // outro na hora do POST. TODOS precisam ser públicos.
        var dns = new DnsFalso { ["duplo.cliente.com"] = ["8.8.8.8", "192.168.0.10"] };

        Assert.False((await ValidadorUrlWebhook.ValidarAsync(
            "https://duplo.cliente.com/hook", dns, default)).Ok);
    }

    [Fact]
    public async Task Nome_que_nao_resolve_e_recusado()
    {
        var dns = new DnsFalso();   // não conhece ninguém

        var r = await ValidadorUrlWebhook.ValidarAsync("https://sumiu.cliente.com/hook", dns, default);
        Assert.False(r.Ok);
        Assert.Contains("resolver", r.Motivo);
    }

    [Fact]
    public async Task Nome_publico_de_verdade_passa()
    {
        var dns = new DnsFalso { ["webhook.cliente.com"] = ["203.0.113.10"] };
        Assert.True((await ValidadorUrlWebhook.ValidarAsync(
            "https://webhook.cliente.com/hook", dns, default)).Ok);
    }

    // ==================================================================== payload
    [Fact]
    public void O_PAYLOAD_TEM_VERSAO_ID_E_TIPO()
    {
        var id = Guid.NewGuid();
        var quando = new DateTime(2026, 8, 6, 13, 0, 0, DateTimeKind.Utc);

        var json = PayloadWebhook.Montar(id, EventoWebhook.VendaFechada, 7, quando, new { x = 1 });
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;

        // Versão desde o primeiro dia: mudar o formato depois quebra a integração de todo cliente
        // que já ligou, e ele descobre porque o pedido parou de entrar — não por um erro.
        Assert.Equal(1, raiz.GetProperty("versao").GetInt32());
        Assert.Equal(id, raiz.GetProperty("id").GetGuid());
        Assert.Equal("venda.fechada", raiz.GetProperty("evento").GetString());
        Assert.Equal(7, raiz.GetProperty("empresaId").GetInt64());
        Assert.True(raiz.TryGetProperty("ocorridoEm", out _));
        Assert.True(raiz.TryGetProperty("dados", out _));
    }

    [Fact]
    public void Os_nomes_dos_eventos_sao_os_do_contrato()
    {
        // O nome vai num `switch` do lado do cliente. `ToString().ToLower()` daria `leadcriado`, e
        // mudar isso depois quebraria a integração de quem já ligou.
        Assert.Equal("lead.criado", EventoWebhook.LeadCriado.ParaApi());
        Assert.Equal("lead.movido", EventoWebhook.LeadMovido.ParaApi());
        Assert.Equal("venda.fechada", EventoWebhook.VendaFechada.ParaApi());
        Assert.Equal("venda.perdida", EventoWebhook.VendaPerdida.ParaApi());
        Assert.Equal("mensagem.recebida", EventoWebhook.MensagemRecebida.ParaApi());
        Assert.Equal("webhook.teste", EventoWebhook.Teste.ParaApi());
    }

    [Fact]
    public void MODO_SO_IDS_NAO_MANDA_NOME_NEM_TELEFONE()
    {
        var contato = new Contato
        {
            Id = 42, EmpresaId = 7, Nome = "Marcos Antunes", Telefone = "5584988887777",
            Email = "marcos@exemplo.com", EtapaId = 3, Valor = 1500m,
            Origem = OrigemLead.Whatsapp, OrigemDetalhe = "Panfleto Julho",
            MotivoPerda = "o marido não deixou"
        };

        var json = JsonSerializer.Serialize(
            PayloadWebhook.Lead(contato, "Proposta", somenteIds: true), PayloadWebhook.Opcoes);

        // ===== O QUE NÃO PODE SAIR =====
        // Nome, telefone, e-mail, o rótulo da origem e o motivo da perda — este último é texto
        // LIVRE escrito pelo vendedor, e costuma ter nome de gente dentro.
        Assert.DoesNotContain("Marcos", json);
        Assert.DoesNotContain("5584988887777", json);
        Assert.DoesNotContain("marcos@exemplo.com", json);
        Assert.DoesNotContain("Proposta", json);
        Assert.DoesNotContain("Panfleto Julho", json);
        Assert.DoesNotContain("marido", json);

        // O que SAI: os ids e o que não identifica ninguém. Sem isso o modo seria inútil.
        Assert.Contains("\"id\":42", json);
        Assert.Contains("\"etapaId\":3", json);
        Assert.Contains("\"valor\":1500", json);
    }

    [Fact]
    public void Com_PII_o_lead_sai_completo()
    {
        // O contra-teste: sem ele, um `PayloadWebhook.Lead` que omitisse tudo sempre passaria no
        // teste acima e o modo normal ficaria vazio.
        var contato = new Contato
        {
            Id = 42, Nome = "Marcos Antunes", Telefone = "5584988887777",
            EtapaId = 3, Origem = OrigemLead.Whatsapp
        };

        var json = JsonSerializer.Serialize(
            PayloadWebhook.Lead(contato, "Proposta", somenteIds: false), PayloadWebhook.Opcoes);

        Assert.Contains("Marcos Antunes", json);
        Assert.Contains("5584988887777", json);
        Assert.Contains("Proposta", json);
    }

    [Fact]
    public void SO_IDS_TAMBEM_SEGURA_O_TEXTO_DA_MENSAGEM()
    {
        // O campo mais sensível do sistema inteiro: a conversa é do cliente do cliente, e ninguém
        // do outro lado consentiu que ela saísse para um servidor de terceiro.
        var json = JsonSerializer.Serialize(
            PayloadWebhook.Mensagem(
                9, 42, 5, "meu CPF é 111.222.333-44", "Marcos", "5584988887777",
                DateTime.UtcNow, somenteIds: true),
            PayloadWebhook.Opcoes);

        Assert.DoesNotContain("CPF", json);
        Assert.DoesNotContain("Marcos", json);
        Assert.DoesNotContain("5584988887777", json);
        Assert.Contains("\"contatoId\":42", json);
    }

    // ==================================================================== assinatura de eventos
    [Fact]
    public void O_EVENTO_DE_TESTE_NAO_E_ASSINAVEL()
    {
        // Ele existe só para o botão. Se `Assina` devolvesse true, um `webhook.teste` sairia
        // sozinho junto com os eventos reais — e o receptor criaria um lead fantasma.
        var webhook = new WebhookSaida
        {
            EmLeadCriado = true, EmLeadMovido = true, EmVendaFechada = true,
            EmVendaPerdida = true, EmMensagemRecebida = true
        };

        Assert.False(webhook.Assina(EventoWebhook.Teste));
        Assert.True(webhook.Assina(EventoWebhook.LeadCriado));
    }

    [Fact]
    public void mensagem_recebida_nasce_DESMARCADO()
    {
        // É o de maior volume de longe — uma conversa ativa gera dezenas por dia — e a maioria não
        // precisa dele. Ligado por padrão, a primeira integração do cliente viraria uma enxurrada.
        var novo = new WebhookSaida();

        Assert.False(novo.EmMensagemRecebida);
        Assert.True(novo.EmLeadCriado);
        Assert.True(novo.EmVendaFechada);
    }
}

/// <summary>DNS de mentira: nome → endereços. O que ele permite provar é a checagem da ENTREGA —
/// um domínio que passou no cadastro e depois passou a apontar para dentro.</summary>
public sealed class DnsFalso : IResolvedorDns
{
    private readonly Dictionary<string, IPAddress[]> _zonas = new(StringComparer.OrdinalIgnoreCase);

    public string[] this[string host] { set => _zonas[host] = [.. value.Select(IPAddress.Parse)]; }

    public Task<IPAddress[]> ResolverAsync(string host, CancellationToken ct) =>
        Task.FromResult(_zonas.GetValueOrDefault(host, []));
}
