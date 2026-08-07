using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A caixa de entrada, do lado da leitura.
///
/// TUDO acontece no SQL: filtro, ordenacao, cursor e corte. O ServicoInbox do Recupera
/// materializa todos os tickets do status em memoria antes de paginar (e o comentario de la
/// reconhece que "Resolvidas cresce"). Aqui isso nao se repete.</summary>
public class ServicoCaixa(NexoraDbContext db, IContextoEmpresa contexto) : IServicoCaixa
{
    /// <summary>A projeção da linha da caixa, num lugar só.
    ///
    /// A lista e a busca por id devolvem o MESMO `ConversaResumo`. Escrita duas vezes, ela já
    /// divergiria no primeiro campo novo — e o sintoma seria a conversa aberta pelo Meu Dia
    /// mostrando um dado a menos que a mesma linha na lista, sem ninguém entender por quê.
    ///
    /// `Expression` e não um método: o EF precisa TRADUZIR isto para SQL. Um método comum seria
    /// executado em memória, e a página inteira viria do banco antes do corte.</summary>
    private static readonly System.Linq.Expressions.Expression<Func<Conversa, ConversaResumo>> Resumo =
        c => new ConversaResumo(
            c.Id, c.ContatoId, c.Contato.Nome, c.Contato.Telefone,
            c.UltimaMensagemPrevia,
            c.UltimaMensagemDirecao == null ? null : c.UltimaMensagemDirecao.ToString()!.ToLower(),
            c.UltimaMensagemEm, c.AguardandoDesde, c.NaoLidas,
            c.Status.ToString().ToLower(),
            c.ResponsavelId, c.Responsavel == null ? null : c.Responsavel.Nome,
            c.Contato.EtapaId, c.Contato.Etapa.Nome);

    /// <summary>Uma conversa pelo id. O query filter global faz o isolamento: id de outra
    /// empresa não casa e o retorno é `null` — que o controller traduz em 404.</summary>
    public async Task<ConversaResumo?> ConversaAsync(long conversaId, CancellationToken ct) =>
        await db.Conversas.AsNoTracking()
            .Where(c => c.Id == conversaId)
            .Select(Resumo)
            .FirstOrDefaultAsync(ct);

    public async Task<PaginaCursor<ConversaResumo>> ConversasAsync(
        FiltroConversa filtro, string? busca, DateTime? cursorEm, long? cursorId, int tamanho,
        CancellationToken ct)
    {
        tamanho = Math.Clamp(tamanho, 1, 100);
        var meuId = contexto.UsuarioId;

        // O query filter global ja restringe ao tenant.
        var q = db.Conversas.AsNoTracking();

        q = filtro switch
        {
            FiltroConversa.Aguardando => q.Where(c => c.Status == StatusConversa.Aberta && c.AguardandoDesde != null),
            FiltroConversa.Minhas => q.Where(c => c.Status == StatusConversa.Aberta && c.ResponsavelId == meuId),
            FiltroConversa.NaoAtribuidas => q.Where(c => c.Status == StatusConversa.Aberta && c.ResponsavelId == null),
            FiltroConversa.Resolvidas => q.Where(c => c.Status == StatusConversa.Resolvida),
            _ => q.Where(c => c.Status == StatusConversa.Aberta)
        };

        if (!string.IsNullOrWhiteSpace(busca))
        {
            var b = busca.Trim().ToLower();
            q = q.Where(c => c.Contato.Nome.ToLower().Contains(b) || c.Contato.Telefone.Contains(b));
        }

        // CURSOR por VALOR, no par exato da ordenacao. O `<` composto e traduzido para SQL e usa
        // o indice ix_conversas_lista.
        if (cursorEm is { } ce)
        {
            var cid = cursorId ?? long.MaxValue;
            q = q.Where(c => c.UltimaMensagemEm < ce || (c.UltimaMensagemEm == ce && c.Id < cid));
        }

        var itens = await q
            .OrderByDescending(c => c.UltimaMensagemEm)
            .ThenByDescending(c => c.Id)
            .Take(tamanho + 1)   // +1 sonda se ha proxima pagina
            .Select(Resumo)
            .ToListAsync(ct);

        var temMais = itens.Count > tamanho;
        return new PaginaCursor<ConversaResumo>(itens.Take(tamanho).ToList(), temMais);
    }

    public async Task<PaginaCursor<MensagemDto>> MensagensAsync(
        long conversaId, long? antesDeId, int tamanho, CancellationToken ct)
    {
        tamanho = Math.Clamp(tamanho, 1, 200);

        var q = db.Mensagens.AsNoTracking().Where(m => m.ConversaId == conversaId);
        if (antesDeId is { } cursor) q = q.Where(m => m.Id < cursor);

        // As `tamanho` mais NOVAS antes do cursor, +1 para saber se ha mais antigas.
        var desc = await q
            .OrderByDescending(m => m.Id)
            .Take(tamanho + 1)
            .Select(m => new MensagemDto(
                m.Id, m.Direcao.ToString().ToLower(), m.Texto, m.Ack,
                m.EnviadaEm, m.RecebidaEm, m.ExpiradaEm, m.Erro,
                m.TipoMidia.ToString().ToLower(), m.MidiaNome, m.MidiaMime, m.MidiaBytes,
                m.MidiaDuracaoSegundos,
                m.EnviadoPor, m.UsuarioEnviou == null ? null : m.UsuarioEnviou.Nome,
                m.LembreteId != null,
                m.RecuperadaEm))
            .ToListAsync(ct);

        var temMais = desc.Count > tamanho;
        // Take volta a IEnumerable -> Reverse e o do LINQ (nao o in-place de List): ascendente.
        var mensagens = desc.Take(tamanho).Reverse().ToList();
        return new PaginaCursor<MensagemDto>(mensagens, temMais);
    }

    public async Task MarcarLidaAsync(long conversaId, CancellationToken ct)
    {
        // ExecuteUpdate: o filtro global vale, entao conversa de outro tenant afeta 0 linhas.
        // NAO toca aguardando_desde — ler nao e responder, e o semaforo mede resposta.
        var afetadas = await db.Conversas
            .Where(c => c.Id == conversaId && c.NaoLidas > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.NaoLidas, 0), ct);

        if (afetadas == 0 && !await db.Conversas.AnyAsync(c => c.Id == conversaId, ct))
            throw new RegraDeNegocioException("Conversa não encontrada.");
    }
}
