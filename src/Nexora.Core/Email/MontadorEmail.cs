using System.Net;

namespace Nexora.Core.Email;

/// <summary>Monta os três e-mails. FUNÇÃO PURA: sem I/O, sem banco, sem relógio — recebe os
/// dados e devolve assunto, HTML e texto. É o que permite testar o conteúdo sem SMTP.
///
/// ===================== POR QUE O HTML É ASSIM =====================
/// Cliente de e-mail NÃO é browser. Outlook renderiza com o motor do Word; Gmail remove &lt;style&gt;
/// do &lt;head&gt;; muitos ignoram flexbox e grid. Daí as três regras que este arquivo segue sem
/// exceção:
///
///   1. TABELA para layout, não div com flex;
///   2. CSS INLINE em cada elemento, não folha de estilo;
///   3. LARGURA FIXA (600px), não responsivo por media query.
///
/// Parece código de 2005 porque o alvo é o mesmo de 2005. Um e-mail bonito que só abre no Gmail
/// é pior que um simples que abre em todos.
///
/// Nenhuma imagem externa: o logo é TEXTO estilizado. Imagem remota é bloqueada por padrão na
/// maioria dos clientes, e um cabeçalho que aparece quebrado passa impressão de golpe — que é
/// exatamente o oposto do que um e-mail de redefinição de senha precisa passar.
/// ==================================================================</summary>
public static class MontadorEmail
{
    // A paleta da marca, em literal: o e-mail não tem acesso ao CSS do painel.
    private const string Verde = "#14432F";
    private const string Verde2 = "#1D5B3F";
    private const string Creme = "#FBF7EF";
    private const string Linha = "#E6DFD1";
    private const string Texto = "#1B2622";
    private const string TextoFraco = "#6A7A73";

    public static EmailPronto Convite(string email, string nome, string empresaNome, string link)
    {
        var corpo = $"""
            <p style="margin:0 0 16px">Olá, {H(PrimeiroNome(nome))}!</p>
            <p style="margin:0 0 16px">
              Você foi convidado para usar o Nexora com a equipe da
              <strong>{H(empresaNome)}</strong>.
            </p>
            <p style="margin:0 0 16px">
              O Nexora organiza o atendimento por WhatsApp: as conversas ficam numa caixa de
              entrada só da equipe, cada cliente vira um contato no funil, e nenhum fica sem
              resposta.
            </p>
            """;

        var html = Envelope(
            titulo: "Seu acesso ao Nexora",
            corpo: corpo,
            textoBotao: "Criar minha senha",
            link: link,
            rodapePos: "Este convite vale por <strong>7 dias</strong>. Depois disso, peça um novo " +
                       "para quem convidou você.");

        var texto = $"""
            Olá, {PrimeiroNome(nome)}!

            Você foi convidado para usar o Nexora com a equipe da {empresaNome}.

            Crie sua senha e entre por este endereço:
            {link}

            Este convite vale por 7 dias.

            Se você não esperava este convite, ignore esta mensagem.
            """;

        return new EmailPronto(email, nome, $"Seu acesso ao Nexora — {empresaNome}", html, texto, "convite");
    }

    public static EmailPronto ResetSenha(string email, string nome, string link)
    {
        var corpo = $"""
            <p style="margin:0 0 16px">Olá, {H(PrimeiroNome(nome))}!</p>
            <p style="margin:0 0 16px">
              Recebemos um pedido para redefinir a senha da sua conta no Nexora.
            </p>
            """;

        var html = Envelope(
            titulo: "Redefinir sua senha",
            corpo: corpo,
            textoBotao: "Criar nova senha",
            link: link,
            rodapePos: "Este link vale por <strong>2 horas</strong> e só pode ser usado uma vez. " +
                       "Se você não pediu a redefinição, ignore este e-mail — sua senha atual " +
                       "continua valendo.");

        var texto = $"""
            Olá, {PrimeiroNome(nome)}!

            Recebemos um pedido para redefinir a senha da sua conta no Nexora.

            Crie uma nova senha por este endereço:
            {link}

            O link vale por 2 horas e só pode ser usado uma vez.

            Se você não pediu a redefinição, ignore este e-mail — sua senha atual continua
            valendo.
            """;

        return new EmailPronto(email, nome, "Redefinir sua senha do Nexora", html, texto, "reset");
    }

    /// <summary>Aviso de senha trocada. SEM LINK e SEM BOTÃO — um "não fui eu, clique aqui" seria
    /// o próprio vetor de phishing que este aviso combate. Quem não reconhece a troca é orientado
    /// a procurar quem administra a conta.</summary>
    public static EmailPronto SenhaAlterada(string email, string nome, string quando)
    {
        var corpo = $"""
            <p style="margin:0 0 16px">Olá, {H(PrimeiroNome(nome))}!</p>
            <p style="margin:0 0 16px">
              A senha da sua conta no Nexora foi alterada em <strong>{H(quando)}</strong>.
            </p>
            <p style="margin:0 0 16px">
              Se foi você, não precisa fazer nada.
            </p>
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%"
                   style="margin:0 0 8px">
              <tr>
                <td style="background:{Creme};border:1px solid {Linha};border-radius:8px;
                           padding:14px 16px;font-size:14px;color:{Texto}">
                  <strong>Não foi você?</strong> Procure agora quem administra a conta da sua
                  empresa no Nexora e peça uma nova redefinição de senha.
                </td>
              </tr>
            </table>
            """;

        var html = Envelope("Sua senha foi alterada", corpo, textoBotao: null, link: null,
            rodapePos: null);

        var texto = $"""
            Olá, {PrimeiroNome(nome)}!

            A senha da sua conta no Nexora foi alterada em {quando}.

            Se foi você, não precisa fazer nada.

            NÃO FOI VOCÊ? Procure agora quem administra a conta da sua empresa no Nexora e peça
            uma nova redefinição de senha.
            """;

        return new EmailPronto(email, nome, "Sua senha do Nexora foi alterada", html, texto, "senha_alterada");
    }

    // ==================================================================== envelope
    /// <summary>O esqueleto comum. Tabela aninhada e largura fixa de 600px — o padrão que Outlook,
    /// Gmail e Apple Mail renderizam igual.</summary>
    private static string Envelope(
        string titulo, string corpo, string? textoBotao, string? link, string? rodapePos)
    {
        var botao = textoBotao is null || link is null ? "" : $"""
            <table role="presentation" cellpadding="0" cellspacing="0" border="0"
                   style="margin:8px 0 20px">
              <tr>
                <td style="background:{Verde};border-radius:8px">
                  <a href="{H(link)}"
                     style="display:inline-block;padding:13px 26px;color:#FFFFFF;
                            font-size:15px;font-weight:600;text-decoration:none">{H(textoBotao)}</a>
                </td>
              </tr>
            </table>
            <p style="margin:0 0 20px;font-size:13px;color:{TextoFraco};line-height:1.5">
              Se o botão não funcionar, copie e cole este endereço no navegador:<br />
              <span style="color:{Verde2};word-break:break-all">{H(link)}</span>
            </p>
            """;

        var pos = rodapePos is null ? "" : $"""
            <p style="margin:0 0 8px;font-size:13px;color:{TextoFraco};line-height:1.6">{rodapePos}</p>
            """;

        return $"""
            <!DOCTYPE html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width,initial-scale=1" />
              <title>{H(titulo)}</title>
            </head>
            <body style="margin:0;padding:0;background:{Creme};
                         font-family:-apple-system,'Segoe UI',Roboto,Arial,sans-serif;color:{Texto}">
              <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%"
                     style="background:{Creme};padding:28px 12px">
                <tr>
                  <td align="center">
                    <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="600"
                           style="width:600px;max-width:600px;background:#FFFFFF;
                                  border:1px solid {Linha};border-radius:12px">
                      <tr>
                        <td style="padding:22px 28px;border-bottom:1px solid {Linha}">
                          <!-- Logo em TEXTO: imagem remota é bloqueada por padrão e um cabeçalho
                               quebrado passa impressão de golpe. -->
                          <span style="font-size:19px;font-weight:700;color:{Verde};
                                       letter-spacing:-0.02em">nexora</span>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:26px 28px 8px">
                          <h1 style="margin:0 0 18px;font-size:20px;font-weight:600;color:{Verde}">
                            {H(titulo)}
                          </h1>
                          <div style="font-size:15px;line-height:1.6;color:{Texto}">{corpo}</div>
                          {botao}
                          {pos}
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:16px 28px 22px;border-top:1px solid {Linha};
                                   font-size:12px;color:{TextoFraco};line-height:1.6">
                          Você recebeu este e-mail porque alguém usou seu endereço no Nexora.
                          Não é preciso responder — esta caixa não é monitorada.
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    /// <summary>Escapa para HTML. Nome de pessoa e nome de empresa vêm de entrada do usuário, e
    /// entram no corpo — sem escapar, um nome com `&lt;` quebra o layout no melhor caso e injeta
    /// marcação no pior.</summary>
    private static string H(string? valor) => WebUtility.HtmlEncode(valor ?? "");

    private static string PrimeiroNome(string? nomeCompleto) =>
        string.IsNullOrWhiteSpace(nomeCompleto)
            ? "tudo bem"
            : nomeCompleto.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
}
