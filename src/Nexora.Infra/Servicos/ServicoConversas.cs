using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O caminho QUENTE: o vendedor respondendo na caixa de entrada o dia inteiro.
///
/// Roda DENTRO de requisicao autenticada, entao o query filter global vale e nao ha
/// IgnoreQueryFilters aqui — nem deve haver. Pedir uma conversa de outro tenant simplesmente
/// nao encontra.</summary>
public class ServicoConversas(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    EnviadorMensagem enviador,
    TimeProvider relogio) : IServicoConversas
{
    private const int TamanhoPrevia = 120;

    public async Task<RespostaEnviada> ResponderAsync(long conversaId, string texto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(texto))
            throw new RegraDeNegocioException("Escreva uma mensagem antes de enviar.");

        var conversa = await db.Conversas
            .Include(c => c.Contato)
            .Include(c => c.Conexao)
            .FirstOrDefaultAsync(c => c.Id == conversaId, ct)
            ?? throw new RegraDeNegocioException("Conversa não encontrada.");

        if (string.IsNullOrWhiteSpace(conversa.Contato.Telefone))
            throw new RegraDeNegocioException("Este contato não tem telefone — não dá para responder.");

        // FREIO POR CONEXAO. Sem o numero pareado, postar so empilha erro; melhor recusar com
        // mensagem clara para o vendedor reconectar. Diferente do lembrete automatico, aqui NAO
        // se reserva para depois: ele esta olhando a tela e precisa saber agora.
        if (!await enviador.InstanciaConectadaAsync(conversa.Conexao.InstanceName, ct))
            throw new RegraDeNegocioException(
                "O WhatsApp está desconectado. Reconecte o número em Conexão e tente de novo.",
                conflito: true);

        var agora = relogio.GetUtcNow().UtcDateTime;

        // Transacao propria so quando nao ha uma em curso (abrir aninhada lanca). A mensagem e a
        // atualizacao da conversa tem que cair juntas: se a mensagem sai e o aguardando_desde
        // nao zera, o semaforo continua vermelho para uma conversa ja respondida.
        var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            // ATRIBUICAO, NAO FILA: responder conversa sem dono ATRIBUI ao vendedor. E a defesa
            // contra dois responderem por cima. Se ja tem dono (mesmo outro), responder nao
            // rouba — quem quiser assumir usa AssumirAsync, que devolve 409.
            conversa.ResponsavelId ??= UsuarioAtual();
            conversa.AtribuidoEm ??= agora;

            var mensagem = new Mensagem
            {
                EmpresaId = conversa.EmpresaId,
                ConversaId = conversa.Id,
                ContatoId = conversa.ContatoId,
                ConexaoId = conversa.ConexaoId,
                InstanceName = conversa.Conexao.InstanceName,
                Direcao = DirecaoMensagem.Saida,
                Texto = texto,
                TipoMidia = TipoMidia.Nenhum,
                // lembrete_id NULL: mensagem manual NAO entra no teto diario nem no dedupe por
                // lembrete. O vendedor responde quantas vezes precisar.
                LembreteId = null,
                EnviadoPor = UsuarioAtual(),
                // data_disparo e obrigatorio em saida (ck_msg_data_disparo). Para manual e o
                // proprio dia: nao ha reserve-defer aqui.
                DataDisparo = DateOnly.FromDateTime(agora),
                ReservadoEm = agora
            };

            var (mensagemId, resultado) = await enviador.EnviarManualAsync(
                mensagem, conversa.Contato.Telefone, ct);

            // A conversa e atualizada mesmo se o POST falhou: a mensagem EXISTE, aparece na
            // thread, e do ponto de vista de "quem esta esperando resposta" nos ja respondemos.
            conversa.AguardandoDesde = null;
            conversa.NaoLidas = 0;
            conversa.UltimaMensagemEm = agora;
            conversa.UltimaMensagemDirecao = DirecaoMensagem.Saida;
            conversa.UltimaMensagemPrevia = texto.Length <= TamanhoPrevia ? texto : texto[..TamanhoPrevia];

            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);

            var erro = resultado == ResultadoEnvio.Enviada
                ? null
                : await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.Id == mensagemId).Select(m => m.Erro).FirstOrDefaultAsync(ct);

            return new RespostaEnviada(mensagemId, resultado == ResultadoEnvio.Enviada, erro);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    public async Task AssumirAsync(long conversaId, CancellationToken ct)
    {
        var conversa = await db.Conversas.FirstOrDefaultAsync(c => c.Id == conversaId, ct)
            ?? throw new RegraDeNegocioException("Conversa não encontrada.");

        var meuId = UsuarioAtual();
        if (conversa.ResponsavelId is { } dono && dono != meuId)
            throw new RegraDeNegocioException(
                "Esta conversa já está sendo atendida por outro vendedor.", conflito: true);

        if (conversa.ResponsavelId == meuId) return;   // reassumir a propria: no-op

        conversa.ResponsavelId = meuId;
        conversa.AtribuidoEm = relogio.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public async Task LiberarAsync(long conversaId, CancellationToken ct)
    {
        var conversa = await db.Conversas.FirstOrDefaultAsync(c => c.Id == conversaId, ct)
            ?? throw new RegraDeNegocioException("Conversa não encontrada.");

        var meuId = UsuarioAtual();
        if (conversa.ResponsavelId is { } dono && dono != meuId)
            throw new RegraDeNegocioException(
                "Só quem está atendendo pode liberar a conversa.", conflito: true);

        conversa.ResponsavelId = null;
        conversa.AtribuidoEm = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>0 fora de requisicao autenticada — nao deveria acontecer aqui (todo caminho e
    /// [Authorize]), mas NULL e melhor que gravar 0 numa FK.</summary>
    private long? UsuarioAtual() => contexto.UsuarioId == 0 ? null : contexto.UsuarioId;
}
