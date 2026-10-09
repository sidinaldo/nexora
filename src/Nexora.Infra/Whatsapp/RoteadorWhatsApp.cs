using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.CloudApi;
using Nexora.Infra.Evolution;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Whatsapp;

/// <summary>===================== UM CONTRATO, DOIS CANAIS (INT-XX) =====================
///
/// Todo o sistema fala com o WhatsApp por `IClienteWhatsApp`, sempre pelo `instanceName` da
/// conexao: o envio (`EnviadorMensagem`), os motores de follow-up e NPS, o verificador de 5 minutos.
/// Este roteador fica atras do contrato e escolhe o canal pela conexao — e nenhum deles precisa
/// saber que existe mais de um.
///
/// A conexao e achada pelo `instance_name`, que e unico globalmente (na Cloud API ele e sintetico,
/// `cloud-{empresa}-{id}`). SEM TENANT NO CONTEXTO, de proposito: quem chama pode ser o webhook ou um
/// job, e a propria chave ja identifica a empresa.
///
/// Instancia que nao esta no banco segue para a Evolution, como sempre foi — e o caso da conexao
/// recem-criada antes do primeiro save, e o de quem chama com um nome antigo.
/// ================================================================================</summary>
public class RoteadorWhatsApp(
    NexoraDbContext db,
    ClienteEvolution evolution,
    IClienteCloudApi cloud,
    CifraSegredos cifra) : IClienteWhatsApp
{
    private sealed record RotaCloud(string PhoneNumberId, string WabaId, string Token);

    /// <summary>A rota da Cloud API, ou nulo quando a conexao e da Evolution.</summary>
    private async Task<RotaCloud?> CloudAsync(string instanceName, CancellationToken ct)
    {
        var conexao = await db.Conexoes.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.InstanceName == instanceName)
            .Select(c => new { c.Canal, c.PhoneNumberId, c.WabaId, c.AccessTokenCifrado })
            .FirstOrDefaultAsync(ct);

        if (conexao == null || conexao.Canal != CanalWhatsapp.CloudApi) return null;

        var token = cifra.Decifrar(conexao.AccessTokenCifrado!, FinalidadeSegredo.AccessToken);
        return new RotaCloud(conexao.PhoneNumberId!, conexao.WabaId!, token);
    }

    // ==================================================================== mensagens
    public async Task<string> EnviarTextoAsync(
        string instanceName, string telefone, string texto, CancellationToken ct)
    {
        var rota = await CloudAsync(instanceName, ct);
        if (rota == null) return await evolution.EnviarTextoAsync(instanceName, telefone, texto, ct);
        throw AindaNao();
    }

    public async Task<string> EnviarMidiaAsync(
        string instanceName, string telefone, string base64, string mediatype, string mimeType,
        string fileName, string? legenda, CancellationToken ct)
    {
        var rota = await CloudAsync(instanceName, ct);
        if (rota == null)
            return await evolution.EnviarMidiaAsync(
                instanceName, telefone, base64, mediatype, mimeType, fileName, legenda, ct);
        throw AindaNao();
    }

    public async Task<string> EnviarAudioAsync(
        string instanceName, string telefone, string base64, CancellationToken ct)
    {
        var rota = await CloudAsync(instanceName, ct);
        if (rota == null) return await evolution.EnviarAudioAsync(instanceName, telefone, base64, ct);
        throw AindaNao();
    }

    public async Task<MidiaRecebida?> ObterMidiaAsync(
        string instanceName, string waMessageId, string mensagemJson, CancellationToken ct)
    {
        var rota = await CloudAsync(instanceName, ct);
        if (rota == null) return await evolution.ObterMidiaAsync(instanceName, waMessageId, mensagemJson, ct);
        throw AindaNao();
    }

    // ==================================================================== a conexao
    public async Task<string> StatusInstanciaAsync(string instanceName, CancellationToken ct)
    {
        var rota = await CloudAsync(instanceName, ct);
        if (rota == null) return await evolution.StatusInstanciaAsync(instanceName, ct);
        return await cloud.EstadoAsync(rota.PhoneNumberId, rota.Token, ct);
    }

    public async Task<DetalhesInstancia?> ObterDetalhesInstanciaAsync(string instanceName, CancellationToken ct)
    {
        var rota = await CloudAsync(instanceName, ct);
        if (rota == null) return await evolution.ObterDetalhesInstanciaAsync(instanceName, ct);

        // O numero e o nome verificado sao o equivalente do `ownerJid` e do perfil: e o que a
        // conferencia usa para preencher `conexoes.numero` e o nome que a tela mostra.
        try
        {
            var numero = await cloud.LerNumeroAsync(rota.PhoneNumberId, rota.Token, ct);
            return new DetalhesInstancia(numero.Numero, numero.NomeVerificado, null, "open");
        }
        catch (IntegracaoWhatsAppException)
        {
            return null;
        }
    }

    /// <summary>A Cloud API nao tem QR nem pareamento: o numero e conectado na Meta. O
    /// `ServicoConexoes` ja recusa antes; esta e a rede de seguranca.</summary>
    public async Task<RespostaQr> ConectarInstanciaAsync(
        string instanceName, string? numeroPareamento, CancellationToken ct)
    {
        var rota = await CloudAsync(instanceName, ct);
        if (rota == null) return await evolution.ConectarInstanciaAsync(instanceName, numeroPareamento, ct);
        throw SemQr();
    }

    public async Task DesconectarInstanciaAsync(string instanceName, CancellationToken ct)
    {
        var rota = await CloudAsync(instanceName, ct);
        if (rota == null)
        {
            await evolution.DesconectarInstanciaAsync(instanceName, ct);
            return;
        }
        throw SemQr();
    }

    /// <summary>Na Cloud API nao ha instancia para apagar: o numero continua na conta da Meta do
    /// cliente, e e la que ele o remove se quiser.</summary>
    public async Task RemoverInstanciaAsync(string instanceName, CancellationToken ct)
    {
        var rota = await CloudAsync(instanceName, ct);
        if (rota == null) await evolution.RemoverInstanciaAsync(instanceName, ct);
    }

    private static RegraDeNegocioException SemQr() =>
        new("Conexão da API oficial não usa QR code: o número é conectado na conta da Meta.", conflito: true);

    private static IntegracaoWhatsAppException AindaNao() =>
        new("O envio pela API oficial ainda não está disponível nesta versão.");
}
