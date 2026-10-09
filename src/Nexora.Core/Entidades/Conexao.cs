namespace Nexora.Core.Entidades;

/// <summary>Um numero de WhatsApp da empresa — pela Evolution ou pela Cloud API da Meta, conforme
/// o `Canal` (INT-XX). Uma empresa pode ter varios (ARQ-2), ate `empresas.limite_conexoes`.</summary>
public class Conexao : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    public string Nome { get; set; } = null!;

    /// <summary>Evolution ou Cloud API. Escolhido na criacao e nunca trocado (ver `CanalWhatsapp`).</summary>
    public CanalWhatsapp Canal { get; set; } = CanalWhatsapp.Evolution;

    /// <summary>Chave de correlacao com a Evolution e UNICA GLOBALMENTE (nao por empresa).
    ///
    /// O webhook chega dizendo so o nome da instancia, e e por aqui que se descobre o tenant
    /// — antes de qualquer consulta com query filter, que fora de requisicao autenticada
    /// retornaria vazio em silencio. Por isso a busca por instance_name no webhook usa
    /// .IgnoreQueryFilters().
    ///
    /// Na Cloud API nao ha instancia, e o nome e SINTETICO (`cloud-{empresa}-{id}`): o
    /// antiduplicacao das mensagens (`uq_msg_wa_id`) e o envio sao chaveados por ele, e manter
    /// a mesma chave nos dois canais evita mexer no indice mais quente da base.</summary>
    public string InstanceName { get; set; } = null!;

    // ===================== SO DA CLOUD API (INT-XX) =====================
    // Nulos na Evolution. Na Cloud API, os quatro primeiros sao obrigatorios
    // (ck_conexoes_cloud_api).

    /// <summary>O id do numero na Meta. E por ele que o webhook da Meta acha a conexao — e o
    /// tenant —, entao e unico globalmente.</summary>
    public string? PhoneNumberId { get; set; }

    /// <summary>A conta do WhatsApp Business (WABA) que contem o numero. Os templates moram nela.</summary>
    public string? WabaId { get; set; }

    /// <summary>O token de acesso da Graph API, CIFRADO (ver `CifraSegredos`). Nunca volta para
    /// a tela: ela so sabe que esta configurado.</summary>
    public string? AccessTokenCifrado { get; set; }

    /// <summary>O segredo do app da Meta, CIFRADO. Valida a assinatura `X-Hub-Signature-256` de
    /// cada webhook — sem ele, qualquer um que soubesse a URL poderia forjar mensagem.</summary>
    public string? AppSecretCifrado { get; set; }

    /// <summary>O token que a Meta devolve no handshake de verificacao do webhook. Nao e segredo
    /// de acesso — so prova que quem cadastrou a URL foi quem tem a conexao —, por isso fica em
    /// claro e e unico, para achar a conexao por ele.</summary>
    public string? VerifyToken { get; set; }

    /// <summary>Quando a Meta confirmou o webhook pela ultima vez. Nulo = nunca, e o teste da
    /// conexao avisa que as mensagens recebidas nao vao chegar.</summary>
    public DateTime? WebhookVerificadoEm { get; set; }

    /// <summary>Numero real conectado, canonicalizado com DDI e so digitos (5584988887777).
    /// Preenchido pelo webhook connection.update a partir do ownerJid.</summary>
    public string? Numero { get; set; }

    /// <summary>Guarda o numero anterior quando a empresa pareia um chip diferente. O webhook
    /// e assincrono e nao ha usuario no loop para confirmar, entao nao se bloqueia a troca:
    /// grava o novo, guarda o antigo aqui, e a tela avisa depois.</summary>
    public string? NumeroAnterior { get; set; }

    public string? PerfilNome { get; set; }
    public string? PerfilFotoUrl { get; set; }

    public StatusConexao Status { get; set; } = StatusConexao.NaoCriada;
    public DateTime? StatusEm { get; set; }
    public DateTime? ConectadoEm { get; set; }
    public DateTime? DesconectadoEm { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
}
