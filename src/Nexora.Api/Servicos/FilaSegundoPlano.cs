using System.Threading.Channels;
using Nexora.Core;

namespace Nexora.Api.Servicos;

/// <summary>A fila em memória, e o serviço que a drena.
///
/// ===================== EM MEMÓRIA, E ISSO É UMA ESCOLHA =====================
/// Se o processo cair com item na fila, o e-mail não sai. É aceitável AQUI e só aqui: o único
/// uso é o "esqueci minha senha", cuja rede de segurança é o link visível na tela para quem tem
/// a chave de administração. O usuário que não recebeu pede de novo.
///
/// O que NÃO pode viver em memória é o envio de WhatsApp — e não vive: a tabela `mensagens` é a
/// outbox, com o protocolo grava → dispara → confirma, porque mensagem duplicada (ou perdida)
/// para o cliente final é dano real.
/// ============================================================================
///
/// Capacidade limitada com `DropWrite`: se a fila encher, o item NOVO é descartado e o log
/// registra. A alternativa (esperar) devolveria a lentidão para a requisição — que é exatamente
/// o que esta fila existe para evitar.</summary>
public class FilaSegundoPlano(ILogger<FilaSegundoPlano> log) : IFilaSegundoPlano
{
    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _canal =
        Channel.CreateBounded<Func<IServiceProvider, CancellationToken, Task>>(
            new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            });

    public ChannelReader<Func<IServiceProvider, CancellationToken, Task>> Leitor => _canal.Reader;

    public void Enfileirar(Func<IServiceProvider, CancellationToken, Task> trabalho)
    {
        // `TryWrite` e não `WriteAsync`: enfileirar não pode bloquear quem chamou. Falhar aqui
        // também não pode derrubar a requisição — ela já fez o que importava.
        if (!_canal.Writer.TryWrite(trabalho))
            log.LogWarning("Fila de segundo plano cheia; trabalho descartado.");
    }
}

/// <summary>Drena a fila, um item por vez, cada um no SEU escopo de injeção.
///
/// Escopo próprio porque o trabalho roda depois de a requisição terminar: o `DbContext` daquele
/// escopo já foi descartado, e o `NotificadorEmail` precisa de um vivo para registrar em
/// `emails_enviados`.
///
/// UM POR VEZ (`SingleReader`), de propósito: são e-mails transacionais esparsos, e serializar
/// evita abrir várias conexões SMTP ao mesmo tempo — o tipo de coisa que provedor trata como
/// abuso.</summary>
public class ProcessadorSegundoPlano(
    FilaSegundoPlano fila,
    IServiceScopeFactory escopos,
    ILogger<ProcessadorSegundoPlano> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken parada)
    {
        await foreach (var trabalho in fila.Leitor.ReadAllAsync(parada))
        {
            try
            {
                using var escopo = escopos.CreateScope();
                await trabalho(escopo.ServiceProvider, parada);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // UMA tentativa. O erro fica no log e, no caso do e-mail, também em
                // `emails_enviados` — o notificador registra a falha antes de propagar.
                //
                // O catch é largo porque uma exceção aqui derrubaria o BackgroundService, e com
                // ele todo envio seguinte: uma falha de um e-mail não pode calar a fila inteira.
                Registrar(ex);
            }
        }
    }

    /// <summary>Logar dentro do catch pode lançar (o EventLog do Windows chega a dar
    /// `ObjectDisposedException` durante o desligamento). Se isso acontecer, o erro vai para o
    /// stderr — nunca de volta para o laço.</summary>
    private void Registrar(Exception ex)
    {
        try { log.LogError(ex, "Trabalho de segundo plano falhou."); }
        catch { Console.Error.WriteLine($"[segundo plano] {ex}"); }
    }
}
