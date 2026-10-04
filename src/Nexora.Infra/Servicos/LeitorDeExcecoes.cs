using Microsoft.EntityFrameworkCore;
using Nexora.Core.Seguranca;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== AS EXCEÇÕES DE PERMISSÃO SAEM DO BANCO UMA VEZ, NO LOGIN =====================
///
/// Depois disso elas viajam no token, e nenhuma requisição volta ao banco para perguntar o que
/// alguém pode. O custo de autorização continua zero — está medido em
/// `ConexoesDbTests.A_CONFERENCIA_DE_ATIVO_NAO_CUSTA_CONSULTA_NOVA`.
///
/// ⚠️ DOIS CAMINHOS CRIAM SESSÃO, e os dois precisam ler daqui: o login
/// (`ServicoAutenticacao.AutenticarAsync`) e o ACEITE DE CONVITE (`ServicoEquipe`), que já entrega
/// a pessoa logada. Esquecer o segundo faria quem acabou de aceitar o convite entrar com a base do
/// papel e sem as exceções que o dono já tinha marcado — e isso se corrigiria sozinho no login
/// seguinte, o que é a pior forma de um defeito aparecer: intermitente.
/// ============================================================================================</summary>
internal static class LeitorDeExcecoes
{
    /// <summary>O que o dono ligou ou desligou para esta pessoa. `null` quando não há nada — o caso
    /// de quase todo mundo, e o que mantém o token do mesmo tamanho de antes.
    ///
    /// ⚠️ `IgnoreQueryFilters()` MAIS `Where` EXPLÍCITO por empresa. Os dois chamadores rodam SEM
    /// tenant no contexto: no login a empresa é justamente o que se está descobrindo, e no aceite
    /// de convite a rota é anônima. Sem ignorar o filtro, a consulta compara `empresa_id` com 0 e
    /// volta VAZIA em silêncio — a pessoa entraria sem as exceções, sem erro nenhum. E ignorar o
    /// filtro SEM o `Where` seria pior: varreria os tenants. É a armadilha que
    /// `ServicoAutenticacao` documenta no topo.
    ///
    /// ⚠️ NOME DESCONHECIDO É IGNORADO. A coluna é `text` com o nome da API, então uma permissão
    /// removida do enum deixa linhas órfãs — e elas não podem derrubar o login de ninguém.</summary>
    public static async Task<IReadOnlyDictionary<Permissao, bool>?> LerAsync(
        NexoraDbContext db, long empresaId, long usuarioId, CancellationToken ct)
    {
        var linhas = await db.UsuariosPermissoes.IgnoreQueryFilters()
            .Where(p => p.EmpresaId == empresaId && p.UsuarioId == usuarioId)
            .Select(p => new { p.Permissao, p.Concedida })
            .ToListAsync(ct);

        if (linhas.Count == 0) return null;

        var excecoes = new Dictionary<Permissao, bool>();

        foreach (var linha in linhas)
            if (Permissoes.DoNomeDaApi(linha.Permissao) is { } gesto)
                excecoes[gesto] = linha.Concedida;

        return excecoes.Count == 0 ? null : excecoes;
    }
}
