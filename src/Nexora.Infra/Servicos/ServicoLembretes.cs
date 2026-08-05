using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Lembretes manuais do vendedor. Roda autenticado — o query filter global vale, e não
/// há IgnoreQueryFilters aqui nem deve haver.</summary>
public class ServicoLembretes(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    TimeProvider relogio) : IServicoLembretes
{
    public async Task<IReadOnlyList<LembreteDto>> DoContatoAsync(long contatoId, CancellationToken ct) =>
        await db.Lembretes.AsNoTracking()
            .Where(l => l.ContatoId == contatoId)
            .OrderByDescending(l => l.DataAlvo).ThenByDescending(l => l.Id)
            .Select(l => new LembreteDto(
                l.Id, l.ContatoId, l.Contato.Nome, l.ConversaId,
                l.Origem.ToString().ToLower(), l.Status.ToString().ToLower(),
                l.DataAlvo, l.HoraAlvo, l.Titulo, l.Observacao, l.EnviaMensagem,
                l.ResponsavelId, l.Responsavel == null ? null : l.Responsavel.Nome, l.ConcluidoEm))
            .ToListAsync(ct);

    public async Task<long> CriarAsync(NovoLembrete novo, CancellationToken ct)
    {
        var titulo = (novo.Titulo ?? "").Trim();
        if (titulo.Length == 0) throw new RegraDeNegocioException("Dê um título ao lembrete.");

        if (novo.EnviaMensagem && string.IsNullOrWhiteSpace(novo.TextoMensagem))
            throw new RegraDeNegocioException("Escreva a mensagem que será enviada.");

        // O query filter garante que o contato é do tenant.
        var contato = await db.Contatos.AsNoTracking()
            .Where(c => c.Id == novo.ContatoId)
            .Select(c => new { c.Id })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Contato não encontrado.");

        // A conversa do contato (1:1). Um lembrete que envia mensagem precisa dela para saber
        // por qual conexão sair.
        var conversaId = await db.Conversas.AsNoTracking()
            .Where(c => c.ContatoId == contato.Id).Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);

        if (novo.EnviaMensagem && conversaId is null)
            throw new RegraDeNegocioException(
                "Este contato ainda não tem conversa — não há por onde enviar a mensagem.");

        var lembrete = new Lembrete
        {
            EmpresaId = contexto.EmpresaId,
            ContatoId = contato.Id,
            ConversaId = conversaId,
            Origem = OrigemLembrete.Manual,
            Status = StatusLembrete.Pendente,
            DataAlvo = novo.DataAlvo,
            HoraAlvo = novo.HoraAlvo,
            Titulo = titulo,
            Observacao = novo.Observacao,
            EnviaMensagem = novo.EnviaMensagem,
            TextoMensagem = novo.EnviaMensagem ? novo.TextoMensagem : null,
            // Quem cria assume: o lembrete aparece no Meu Dia DELE.
            ResponsavelId = contexto.UsuarioId == 0 ? null : contexto.UsuarioId,
            CriadoPor = contexto.UsuarioId == 0 ? null : contexto.UsuarioId
        };

        db.Lembretes.Add(lembrete);
        await db.SaveChangesAsync(ct);
        return lembrete.Id;
    }

    public Task ConcluirAsync(long id, CancellationToken ct) =>
        MudarStatusAsync(id, StatusLembrete.Concluido, ct);

    public Task CancelarAsync(long id, CancellationToken ct) =>
        MudarStatusAsync(id, StatusLembrete.Cancelado, ct);

    private async Task MudarStatusAsync(long id, StatusLembrete status, CancellationToken ct)
    {
        var lembrete = await db.Lembretes.FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new RegraDeNegocioException("Lembrete não encontrado.");

        if (lembrete.Status != StatusLembrete.Pendente)
            throw new RegraDeNegocioException("Este lembrete já foi concluído ou cancelado.", conflito: true);

        lembrete.Status = status;
        lembrete.ConcluidoEm = relogio.GetUtcNow().UtcDateTime;
        lembrete.ConcluidoPor = contexto.UsuarioId == 0 ? null : contexto.UsuarioId;
        await db.SaveChangesAsync(ct);
    }
}
