namespace Nexora.Core.Email;

/// <summary>O que o convite/reset/aviso gera. Já montado: quem monta é o MontadorEmail, quem
/// entrega é o remetente. HTML e texto puro sempre juntos — cliente de e-mail que bloqueia HTML
/// (ou leitor de tela) precisa da versão texto, e mandar só HTML é o caminho curto para cair na
/// pasta de spam.</summary>
public record EmailPronto(
    string Destinatario,
    string NomeDestinatario,
    string Assunto,
    string Html,
    string Texto,
    /// <summary>convite · reset · senha_alterada. Vai para o registro de envios.</summary>
    string Tipo);

/// <summary>O TRANSPORTE. Uma tentativa, sem retry — quem decide o que fazer com a falha é o
/// notificador, e a decisão é: registrar e seguir.
///
/// Fica no Core para o serviço de aplicação nunca conhecer o provedor. Trocar SMTP por API HTTP
/// (Resend, SES, Brevo) é escrever outra implementação desta interface e mudar uma linha de DI —
/// nenhum serviço de domínio muda.</summary>
public interface IRemetenteEmail
{
    /// <summary>Lança se não conseguir entregar. Quem chama TRATA — ver INotificadorEmail.</summary>
    Task EnviarAsync(EmailPronto email, CancellationToken ct);
}
