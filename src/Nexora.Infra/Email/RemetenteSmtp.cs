using System.Net;
using System.Net.Mail;
using System.Text;
using Nexora.Core.Email;

namespace Nexora.Infra.Email;

/// <summary>Entrega por SMTP.
///
/// ===================== POR QUE SMTP GENÉRICO =====================
/// Nenhum SDK de provedor. SMTP funciona igual em Resend, Amazon SES, Brevo, Mailgun, Zoho e no
/// servidor do próprio cliente — trocar de fornecedor é trocar host, usuário e senha no
/// user-secrets, sem tocar em código nem em dependência.
///
/// Um SDK traria webhooks de entrega e estatísticas, que a fase 1 não usa, em troca de
/// acoplamento a um fornecedor. Quando esses recursos fizerem falta, é outra implementação de
/// IRemetenteEmail e uma linha de DI.
/// =================================================================
///
/// ===================== SOBRE O SmtpClient DA BCL =====================
/// A documentação da Microsoft recomenda MailKit para desenvolvimento novo. Fiquei com o
/// `System.Net.Mail.SmtpClient` mesmo assim, e o motivo é o escopo: uma tentativa, sem retry, sem
/// OAuth, contra um relay que fala STARTTLS na 587. É exatamente o que a BCL faz bem, e evita uma
/// dependência nova num projeto que tem poucas.
///
/// O LIMITE REAL: ele NÃO fala SMTPS implícito (porta 465) — `EnableSsl` nele significa STARTTLS.
/// Provedor que só oferece 465 não funciona aqui, e a saída é MailKit. Está registrado no
/// relatório do bloco.
/// =====================================================================</summary>
public class RemetenteSmtp(OpcoesEmail opcoes) : IRemetenteEmail
{
    public async Task EnviarAsync(EmailPronto email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opcoes.Host))
            throw new InvalidOperationException(
                "Email:Host não configurado. Em dev use Email:Provedor=arquivo.");

        using var cliente = new SmtpClient(opcoes.Host, opcoes.Porta)
        {
            EnableSsl = opcoes.UsarSsl,
            Timeout = opcoes.TimeoutSegundos * 1000,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        // Sem usuário configurado, não autentica: relay interno e MailHog/Mailpit local não
        // pedem credencial, e mandar credencial vazia faz o servidor recusar a conexão.
        if (!string.IsNullOrWhiteSpace(opcoes.Usuario))
        {
            cliente.UseDefaultCredentials = false;
            cliente.Credentials = new NetworkCredential(opcoes.Usuario, opcoes.Senha);
        }

        using var mensagem = new MailMessage
        {
            From = new MailAddress(opcoes.Remetente, opcoes.NomeRemetente, Encoding.UTF8),
            Subject = email.Assunto,
            SubjectEncoding = Encoding.UTF8,
            // O CORPO É O TEXTO PURO, e o HTML entra como vista ALTERNATIVA. É a ordem que o
            // padrão multipart/alternative espera: cliente que não renderiza HTML cai na versão
            // de texto em vez de mostrar a marcação crua.
            Body = email.Texto,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };

        mensagem.To.Add(new MailAddress(email.Destinatario, email.NomeDestinatario, Encoding.UTF8));
        mensagem.AlternateViews.Add(
            AlternateView.CreateAlternateViewFromString(email.Html, Encoding.UTF8, "text/html"));

        // SendMailAsync ignora o CancellationToken (limitação do SmtpClient da BCL); quem corta é
        // o Timeout acima. Registrado para ninguém contar com o cancelamento aqui.
        ct.ThrowIfCancellationRequested();
        await cliente.SendMailAsync(mensagem);
    }
}
