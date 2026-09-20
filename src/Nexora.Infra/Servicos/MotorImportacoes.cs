using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>PEGA UMA IMPORTAÇÃO DA FILA E GRAVA — o arquivo grande demais para um request.
///
/// ===================== ELE RODA O MESMO CÓDIGO DO BOTÃO =====================
/// Nada aqui decide coisa alguma sobre os leads: o mapeamento, o funil, o responsável e o aviso já
/// foram escolhidos e guardados no clique. Este motor faz três coisas — acha, RESERVA e chama.
///
/// A gravação é `ServicoImportacaoMeta.ProcessarAsync`, a mesma do caminho síncrono. Um segundo
/// caminho de gravação, escrito com `IgnoreQueryFilters` para rodar sem tenant, seria a cópia que
/// este projeto passa o tempo desmontando — e a pior delas: a que só roda com arquivo grande, onde
/// ninguém olha.
/// ==========================================================================
///
/// ===================== A RESERVA, E POR QUE AQUI ELA PRECISA EXISTIR =====================
/// Os outros jobs deste projeto convivem com duas instâncias rodando em paralelo: o webhook
/// duplicado é deduplicado pelo receptor, o lembrete repetido é um incômodo. Aqui o efeito seria
/// CONTATO REPETIDO na base do cliente — e a segunda rodada ainda sobrescreveria os contadores da
/// primeira.
///
/// `processando_desde` é a reserva: o `UPDATE ... WHERE processando_desde IS NULL` só acerta linha
/// para UM dos concorrentes, e o outro vai embora sem trabalho. É o mesmo padrão de reserva
/// atômica que uma fila de verdade faria, com a tabela que já existe.
/// =======================================================================================</summary>
public class MotorImportacoes(
    NexoraDbContext db,
    ContextoDeFundo fundo,
    IServicoImportacaoMeta servico,
    TimeProvider relogio,
    ILogger<MotorImportacoes> log)
{
    /// <summary>Processa UMA importação, se houver. Devolve quantas processou (0 ou 1).
    ///
    /// Uma por rodada porque uma importação pode ter 10.000 linhas: segurar a rodada por duas delas
    /// atrasaria a terceira sem ganho nenhum — a próxima passada acontece em segundos.</summary>
    public async Task<int> ExecutarAsync(CancellationToken ct = default)
    {
        // ⚠️ SEM TENANT ATÉ AQUI: o job não é de empresa nenhuma, então esta consulta — e SÓ ela —
        // ignora o filtro global. Ver `ContextoDeFundo`.
        var pendente = await db.Importacoes.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Status == StatusImportacao.Processando && i.ProcessandoDesde == null)
            .OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.EmpresaId, i.UsuarioId })
            .FirstOrDefaultAsync(ct);

        if (pendente is null) return 0;

        // Outra instância pode ter achado a MESMA linha entre a consulta acima e esta reserva —
        // quem não conseguir marcar vai embora sem trabalho, e sem erro: é o desenho funcionando.
        if (!await ReservarAsync(pendente.Id, ct)) return 0;

        // A partir daqui o job É aquela empresa, e o query filter protege como numa requisição.
        fundo.Assumir(pendente.EmpresaId, pendente.UsuarioId);
        db.ChangeTracker.Clear();

        try
        {
            var r = await servico.ProcessarAsync(pendente.Id, ct);

            log.LogInformation(
                "Importação {Id} da empresa {Empresa}: {Importados} importados, {Duplicados} "
              + "duplicados, {Invalidos} inválidos.",
                pendente.Id, pendente.EmpresaId,
                r?.Importados ?? 0, r?.Duplicados ?? 0, r?.Invalidos ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // `ProcessarAsync` já deixou a importação em `erro` com a mensagem — a tela do dono
            // mostra o que houve. Aqui é o registro do nosso lado.
            log.LogError(ex, "A importação {Id} falhou.", pendente.Id);
        }

        return 1;
    }

    /// <summary>⚠️ A RESERVA, NUM COMANDO SÓ: `UPDATE ... WHERE processando_desde IS NULL`. O
    /// banco decide quem ganha, e o perdedor recebe "0 linhas afetadas".
    ///
    /// Ler e depois escrever em dois passos NÃO resolveria: as duas instâncias leriam nulo antes
    /// de qualquer uma escrever, e as duas processariam — criando a mesma pessoa duas vezes na
    /// base do cliente. É por isso que esta linha é uma só.
    ///
    /// Método próprio — e não uma linha no meio do `ExecutarAsync` — para o teste poder chamá-la
    /// duas vezes e ver a segunda falhar: a corrida real não dá para orquestrar num teste de uma
    /// thread só.</summary>
    public async Task<bool> ReservarAsync(long importacaoId, CancellationToken ct = default)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;

        return await db.Importacoes.IgnoreQueryFilters()
            .Where(i => i.Id == importacaoId && i.ProcessandoDesde == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ProcessandoDesde, agora), ct) == 1;
    }
}
