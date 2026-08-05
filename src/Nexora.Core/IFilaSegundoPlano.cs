namespace Nexora.Core;

/// <summary>Trabalho que sai do caminho da requisição.
///
/// ===================== PARA QUE ISTO EXISTE =====================
/// O "esqueci minha senha" precisa responder em tempo CONSTANTE, exista a conta ou não — senão o
/// tempo de resposta vira um verificador de contas. O piso de 250 ms resolvia a diferença entre
/// gravar um token e não gravar nada; não resolvia o envio SMTP, que num relay lento passa do
/// piso e reabre a janela.
///
/// Tirando o envio daqui, a requisição volta sempre no mesmo tempo e o e-mail sai depois.
/// ===============================================================
///
/// ===================== O QUE ISTO NÃO É =====================
/// NÃO é fila com retry e backoff. Uma tentativa, o resultado registrado em `emails_enviados`, e
/// acabou. A rede de segurança já existe e é outra: o link de redefinição continua visível na
/// tela para quem tem a chave de administração — quem não recebeu o e-mail tem por onde seguir.
///
/// Uma fila com repetição precisaria de persistência, deduplicação e um lugar para as mensagens
/// mortas — é a outbox de `mensagens`, que existe porque WhatsApp duplicado é dano real. E-mail
/// de reset que não sai tem custo baixo e caminho alternativo.
/// ============================================================
///
/// O trabalho recebe um `IServiceProvider` porque roda FORA do escopo da requisição: o
/// `DbContext` daquele escopo já foi descartado quando ele executa, e usá-lo daria
/// `ObjectDisposedException` num caminho que ninguém observa.</summary>
public interface IFilaSegundoPlano
{
    /// <summary>Enfileira e volta na hora. Nunca lança: falhar ao enfileirar não pode derrubar a
    /// requisição que já fez o que importava (gravar o token).</summary>
    void Enfileirar(Func<IServiceProvider, CancellationToken, Task> trabalho);
}
