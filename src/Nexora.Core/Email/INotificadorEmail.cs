namespace Nexora.Core.Email;

/// <summary>Os três e-mails da fase 1. É o que os serviços de aplicação chamam.
///
/// ===================== NENHUM MÉTODO AQUI LANÇA =====================
/// Criar usuário e mandar e-mail são coisas diferentes. Se o provedor estiver fora, o convite
/// continua válido, o token continua no banco e o link continua visível na tela do dono — que
/// pode copiar e mandar por fora, exatamente como fazia antes deste bloco existir.
///
/// Deixar a exceção subir faria o oposto: o usuário não seria criado porque o e-mail falhou. É a
/// dependência menos confiável do sistema derrubando a operação mais importante dele.
///
/// O preço: falha de envio é SILENCIOSA para quem chamou. Por isso toda tentativa é gravada em
/// `emails_enviados` com o erro — sem esse registro, "o cliente diz que não recebeu" é
/// indepurável.
/// ====================================================================</summary>
public interface INotificadorEmail
{
    /// <summary>Convite para entrar na equipe. Validade de 7 dias (a mesma do token).</summary>
    Task ConviteAsync(long empresaId, string email, string nome, string empresaNome,
        string token, CancellationToken ct);

    /// <summary>Link de redefinição de senha. Validade de 2h.</summary>
    Task ResetSenhaAsync(long? empresaId, string email, string nome, string token,
        CancellationToken ct);

    /// <summary>Aviso de que a senha mudou. SEM LINK.
    ///
    /// É a defesa mais barata contra conta invadida sem o dono perceber: quem não trocou a senha
    /// descobre na hora. Sem link porque um "não fui eu, clique aqui" seria justamente o vetor de
    /// phishing que este aviso existe para combater.</summary>
    Task SenhaAlteradaAsync(long empresaId, string email, string nome, CancellationToken ct);
}
