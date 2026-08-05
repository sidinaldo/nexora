using System.Text;
using Microsoft.Extensions.Logging;
using Nexora.Core.Email;
using Nexora.Core.Seguranca;

namespace Nexora.Infra.Email;

/// <summary>Remetente de DESENVOLVIMENTO: grava o e-mail em disco em vez de enviar.
///
/// É outra IMPLEMENTAÇÃO da mesma interface, não um `if` no meio do notificador. A diferença
/// importa: com `if`, o caminho de produção nunca roda em dev e o de dev vira código morto em
/// produção — os dois caminhos divergem e ninguém percebe até o dia do deploy.
///
/// Grava `.html` e `.txt` separados: abrir o HTML no navegador mostra como o cliente vai ver, e
/// o `.txt` é a versão que quem bloqueia HTML recebe. Conferir os dois é o ponto.</summary>
public class RemetenteArquivo(OpcoesEmail opcoes, ILogger<RemetenteArquivo> log) : IRemetenteEmail
{
    public async Task EnviarAsync(EmailPronto email, CancellationToken ct)
    {
        var pasta = Path.IsPathRooted(opcoes.PastaArquivo)
            ? opcoes.PastaArquivo
            : Path.Combine(AppContext.BaseDirectory, opcoes.PastaArquivo);

        Directory.CreateDirectory(pasta);

        // Carimbo ordenável + tipo + destinatário sanitizado: a pasta fica legível na listagem,
        // e nomes de arquivo não aceitam '@' nem ':' em todo sistema de arquivos.
        var carimbo = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var alvo = new string(email.Destinatario.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var basePath = Path.Combine(pasta, $"{carimbo}_{email.Tipo}_{alvo}");

        var cabecalho = $"""
            Para: {email.NomeDestinatario} <{email.Destinatario}>
            De: {opcoes.NomeRemetente} <{opcoes.Remetente}>
            Assunto: {email.Assunto}
            Tipo: {email.Tipo}

            """;

        await File.WriteAllTextAsync($"{basePath}.txt", cabecalho + email.Texto, Encoding.UTF8, ct);
        await File.WriteAllTextAsync($"{basePath}.html", email.Html, Encoding.UTF8, ct);

        // O e-mail vai MASCARADO para o log, como em todo o resto do sistema. O caminho do
        // arquivo vai inteiro: é o que faz o desenvolvedor achar a mensagem em dois segundos.
        log.LogInformation(
            "E-mail '{Tipo}' gravado em disco para {Email}: {Arquivo}.html",
            email.Tipo, PoliticaLogin.MascararEmail(email.Destinatario), basePath);
    }
}
