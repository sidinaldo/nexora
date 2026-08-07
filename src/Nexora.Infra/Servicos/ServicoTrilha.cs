using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Leitura da trilha (AUD-1). NAO existe escrita aqui: quem grava e o
/// `InterceptorTrilha`, a partir do que os servicos declaram.</summary>
public class ServicoTrilha(NexoraDbContext db, IContextoEmpresa contexto) : IServicoTrilha
{
    public async Task<IReadOnlyList<EventoTrilha>> DoRegistroAsync(
        EntidadeAuditada entidade, long id, int tamanho, CancellationToken ct)
    {
        ExigirDonoOuGestor();

        tamanho = Math.Clamp(tamanho, 1, 200);

        // `ix_auditoria_registro` cobre exatamente este predicado + ordenacao.
        return await db.Auditoria.AsNoTracking()
            .Where(a => a.Entidade == entidade && a.EntidadeId == id)
            .OrderByDescending(a => a.Quando).ThenByDescending(a => a.Id)
            .Take(tamanho)
            .Select(a => new EventoTrilha(
                a.Id, a.Entidade.ToString(), a.EntidadeId, a.Acao.ToString(),
                a.Alteracoes, a.UsuarioId,
                a.Usuario == null ? null : a.Usuario.Nome,
                a.Ator.ToString(), a.Quando))
            .ToListAsync(ct);
    }

    /// <summary>===================== VENDEDOR NAO VE A TRILHA =====================
    /// Ele nao precisa auditar colega, e expor isso azeda o clima da equipe — a ferramenta
    /// passa a ser lida como vigilancia entre pares em vez de prestacao de contas ao dono.
    ///
    /// A checagem fica NO SERVICO, nao so num `[Authorize(Roles=...)]` do controller: assim ela
    /// vale tambem quando outro codigo chamar por dentro, sem passar por HTTP.
    /// ====================================================================</summary>
    private void ExigirDonoOuGestor()
    {
        var papel = contexto.Papel ?? "";
        if (!papel.Equals("dono", StringComparison.OrdinalIgnoreCase)
            && !papel.Equals("gestor", StringComparison.OrdinalIgnoreCase))
        {
            throw new RegraDeNegocioException("Só o dono ou um gestor pode ver o histórico de alterações.");
        }
    }
}
