using Microsoft.Extensions.Logging;
using Nexora.Core.Email;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Email;

/// <summary>Monta, entrega e REGISTRA. É o único lugar que junta as três coisas.
///
/// ===================== O CONTRATO: NUNCA LANÇA =====================
/// Nenhum método aqui propaga exceção. Se o provedor estiver fora, o convite continua válido, o
/// token continua no banco e o link continua na tela do dono — o fallback manual, que era o
/// único caminho antes deste bloco, não sai do produto.
///
/// A alternativa (deixar subir) faria o usuário NÃO ser criado porque o e-mail falhou: a
/// dependência menos confiável do sistema derrubando a operação mais importante dele.
///
/// O preço é a falha ser silenciosa para quem chamou — e é por isso que toda tentativa vira
/// linha em `emails_enviados`, com o erro. Sem esse registro, "não recebi" é indepurável.
/// ===================================================================</summary>
public class NotificadorEmail(
    IRemetenteEmail remetente,
    NexoraDbContext db,
    OpcoesEmail opcoes,
    TimeProvider relogio,
    ILogger<NotificadorEmail> log) : INotificadorEmail
{
    public Task ConviteAsync(
        long empresaId, string email, string nome, string empresaNome, string token,
        CancellationToken ct) =>
        DespacharAsync(empresaId,
            MontadorEmail.Convite(email, nome, empresaNome, Link("convite", token)), ct);

    public Task ResetSenhaAsync(
        long? empresaId, string email, string nome, string token, CancellationToken ct) =>
        DespacharAsync(empresaId,
            MontadorEmail.ResetSenha(email, nome, Link("redefinir", token)), ct);

    public Task SenhaAlteradaAsync(long empresaId, string email, string nome, CancellationToken ct)
    {
        // A hora vai no fuso de BRASÍLIA, não em UTC: quem recebe "alterada às 14:32" consegue
        // dizer se foi ele. "17:32Z" não ajuda ninguém a reconhecer a própria ação.
        var quando = Core.Tempo.FusoDeNegocio
            .AgoraNo(relogio, Core.Tempo.FusoDeNegocio.Resolver(Core.Tempo.FusoDeNegocio.PadraoBrasil))
            .ToString("dd/MM/yyyy 'às' HH:mm");

        return DespacharAsync(empresaId, MontadorEmail.SenhaAlterada(email, nome, quando), ct);
    }

    /// <summary>Uma tentativa, resultado gravado, exceção engolida.
    ///
    /// O registro é gravado em AMBOS os caminhos e por último, para o INSERT nunca ser o motivo
    /// de a operação de negócio falhar.</summary>
    private async Task DespacharAsync(long? empresaId, EmailPronto email, CancellationToken ct)
    {
        var mascarado = PoliticaLogin.MascararEmail(email.Destinatario);
        bool sucesso;
        string? erro = null;

        try
        {
            await remetente.EnviarAsync(email, ct);
            sucesso = true;
            log.LogInformation("E-mail '{Tipo}' enviado para {Email}.", email.Tipo, mascarado);
        }
        catch (Exception ex)
        {
            sucesso = false;
            // Só a mensagem, cortada: a coluna é para diagnóstico humano, e stack trace de SMTP
            // não ajuda a responder "por que não chegou".
            erro = ex.Message.Length <= 500 ? ex.Message : ex.Message[..500];
            log.LogError(ex,
                "Falha ao enviar e-mail '{Tipo}' para {Email}. A operação segue — o link " +
                "continua válido.", email.Tipo, mascarado);
        }

        await RegistrarAsync(empresaId, email, sucesso, erro, ct);
    }

    private async Task RegistrarAsync(
        long? empresaId, EmailPronto email, bool sucesso, string? erro, CancellationToken ct)
    {
        try
        {
            db.EmailsEnviados.Add(new EmailEnviado
            {
                EmpresaId = empresaId,
                Destinatario = email.Destinatario,
                Tipo = email.Tipo,
                Assunto = email.Assunto,
                EnviadoEm = relogio.GetUtcNow().UtcDateTime,
                Sucesso = sucesso,
                Erro = erro
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Nem o REGISTRO pode derrubar a operação. Se o banco recusar esta linha, o
            // convite já foi criado e o e-mail já foi (ou não) entregue — abortar agora
            // desfaria trabalho útil por causa de um log.
            log.LogError(ex, "Não foi possível registrar o envio de e-mail '{Tipo}'.", email.Tipo);
        }
    }

    /// <summary>O link do painel. A base vem das opções — nunca chumbada: o convite tem que
    /// apontar para o domínio de quem hospeda, não para localhost.</summary>
    private string Link(string rota, string token) =>
        $"{opcoes.BaseUrlPainel.TrimEnd('/')}/{rota}/{Uri.EscapeDataString(token)}";
}
