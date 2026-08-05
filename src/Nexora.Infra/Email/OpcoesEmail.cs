namespace Nexora.Infra.Email;

/// <summary>Seção "Email" do appsettings. As CREDENCIAIS vêm de user-secrets em dev e de
/// variável de ambiente em produção — `Usuario` e `Senha` NUNCA entram no appsettings versionado.
///
///   dotnet user-secrets set "Email:Senha" "..." --project src/Nexora.Api</summary>
public class OpcoesEmail
{
    /// <summary>"smtp" ou "arquivo". `arquivo` é o padrão porque um clone limpo do repositório
    /// tem que subir e funcionar sem ninguém configurar SMTP — e porque errar para o lado de
    /// NÃO enviar é sempre mais barato que errar para o lado de enviar.</summary>
    public string Provedor { get; set; } = "arquivo";

    public string Remetente { get; set; } = "nao-responda@nexora.local";
    public string NomeRemetente { get; set; } = "Nexora";

    /// <summary>Base do painel para montar os links. NÃO é chumbada em lugar nenhum: o link do
    /// convite tem que apontar para o domínio de quem hospeda.</summary>
    public string BaseUrlPainel { get; set; } = "http://localhost:4200";

    // ---- SMTP ----
    public string Host { get; set; } = "";
    public int Porta { get; set; } = 587;
    /// <summary>STARTTLS na 587. Na 465 (SMTPS implícito) o SmtpClient da BCL não funciona — ver
    /// a nota em RemetenteSmtp.</summary>
    public bool UsarSsl { get; set; } = true;
    public string Usuario { get; set; } = "";
    public string Senha { get; set; } = "";

    /// <summary>Segundos até desistir de uma tentativa. Curto de propósito: o envio acontece
    /// dentro da requisição, e o dono não pode ficar 100s olhando um botão girando porque o
    /// provedor está lento.</summary>
    public int TimeoutSegundos { get; set; } = 15;

    // ---- arquivo (desenvolvimento) ----
    public string PastaArquivo { get; set; } = "emails-dev";
}
