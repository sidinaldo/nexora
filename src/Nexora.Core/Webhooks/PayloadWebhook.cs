using System.Text.Json;
using System.Text.Json.Serialization;
using Nexora.Core.Entidades;

namespace Nexora.Core.Webhooks;

/// <summary>Os dados de um lead, como saem daqui.
///
/// Os campos de PII são anuláveis porque o modo "só ids" os omite — e omitir é diferente de mandar
/// vazio: `nome: null` diria "este lead não tem nome". `JsonIgnore` quando nulo faz o campo
/// simplesmente não existir no corpo, que é o que o receptor precisa para distinguir os dois.</summary>
public record LeadWebhook(
    long Id,
    long EtapaId,
    string? EtapaNome,
    string? Nome,
    string? Telefone,
    string? Email,
    string Origem,
    string? OrigemDetalhe,
    decimal? Valor,
    long? ResponsavelId,
    long? EtapaAnteriorId,
    string? MotivoPerda);

public record MensagemWebhook(
    long Id,
    long ContatoId,
    long ConversaId,
    string? Texto,
    string? ContatoNome,
    string? ContatoTelefone,
    DateTime RecebidaEm);

/// <summary>O ENVELOPE, e o único lugar que sabe montá-lo.
///
/// ===================== VERSÃO NO CORPO, DESDE O PRIMEIRO DIA =====================
/// `"versao": 1`. Mudar o formato depois quebra a integração de todo cliente que já ligou o
/// webhook — e ele não vai descobrir por um erro, vai descobrir porque o pedido parou de entrar no
/// ERP. Com a versão no corpo, o receptor tem onde ramificar, e nós temos como mudar sem quebrar.
///
/// Versionar custa um campo agora. Não versionar custa uma migração coordenada com cada cliente.
/// =================================================================================
///
/// ===================== O `id` É PARA O RECEPTOR, NÃO PARA NÓS =====================
/// As três tentativas da mesma entrega carregam o MESMO `id`. É o que permite ao receptor
/// processar uma vez e ignorar as repetições — sem ele, um timeout NOSSO (a entrega chegou, a
/// resposta se perdeu) vira pedido duplicado no sistema dele.
/// ==================================================================================</summary>
public static class PayloadWebhook
{
    public const int Versao = 1;

    /// <summary>camelCase, sem indentação, sem escapar acento além do necessário.
    ///
    /// Estas opções fazem PARTE do contrato: o corpo é o que foi assinado, byte a byte. Reserializar
    /// com outra configuração muda o corpo e invalida a assinatura — é por isso que a serialização
    /// acontece UMA vez, aqui, e o texto resultante é guardado.</summary>
    public static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Montar(
        Guid eventoId, EventoWebhook evento, long empresaId, DateTime ocorridoEm, object dados) =>
        JsonSerializer.Serialize(new
        {
            versao = Versao,
            id = eventoId,
            evento = evento.ParaApi(),
            ocorridoEm,
            empresaId,
            dados
        }, Opcoes);

    /// <summary>O lead como ele sai, respeitando o modo "só ids".
    ///
    /// ===================== A DECISÃO É DO CLIENTE, E ELA MORA AQUI =====================
    /// Um lugar só monta o objeto do lead. Se cada evento montasse o seu, bastaria um esquecer o
    /// `somenteIds` para o nome e o telefone saírem para o servidor de terceiro — e essa falha não
    /// aparece em teste de tela nem em log: aparece numa auditoria, depois.
    /// ==================================================================================</summary>
    public static LeadWebhook Lead(
        Contato contato, string? etapaNome, bool somenteIds, long? etapaAnteriorId = null) =>
        new(
            contato.Id,
            contato.EtapaId,
            somenteIds ? null : etapaNome,
            somenteIds ? null : contato.Nome,
            somenteIds ? null : contato.Telefone,
            somenteIds ? null : contato.Email,
            contato.Origem.ToString().ToLowerInvariant(),
            somenteIds ? null : contato.OrigemDetalhe,
            contato.Valor,
            contato.ResponsavelId,
            etapaAnteriorId,
            // O motivo da perda é texto LIVRE escrito pelo vendedor: "cliente sumiu", "o marido
            // não deixou". Pode conter nome de gente, e por isso segue a mesma regra.
            somenteIds ? null : contato.MotivoPerda);

    /// <summary>A mensagem recebida. No modo "só ids" o TEXTO não sai — é o campo mais sensível do
    /// sistema inteiro: a conversa é do cliente do cliente, e ninguém consentiu que ela saísse.</summary>
    public static MensagemWebhook Mensagem(
        long mensagemId, long contatoId, long conversaId, string? texto,
        string? contatoNome, string? contatoTelefone, DateTime recebidaEm, bool somenteIds) =>
        new(
            mensagemId, contatoId, conversaId,
            somenteIds ? null : texto,
            somenteIds ? null : contatoNome,
            somenteIds ? null : contatoTelefone,
            recebidaEm);
}
