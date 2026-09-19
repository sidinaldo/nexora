using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nexora.Core.Entidades;

namespace Nexora.Infra.Persistencia;

/// <summary>Preenche criado_em e atualizado_em em todo SaveChanges, para nenhum servico
/// precisar lembrar.
///
/// O Recupera atribui essas colunas a mao em dezenas de pontos; basta um caminho de escrita
/// esquecer e a coluna passa a mentir — sem erro, sem teste que pegue. O interceptor nao
/// esquece.
///
/// LIMITE CONHECIDO: interceptor so ve o que passa pelo EF. Escrita em SQL cru
/// (ExecuteSqlRaw, e o INSERT ... ON CONFLICT do outbox que chega no bloco 4) NAO dispara
/// isto — por isso a migration tambem instala um trigger no banco. Os dois se sobrepoem de
/// proposito: o interceptor cobre criado_em no INSERT, o trigger cobre qualquer UPDATE
/// venha de onde vier.</summary>
public class InterceptorAuditoria(TimeProvider relogio) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> resultado)
    {
        Carimbar(eventData.Context);
        return base.SavingChanges(eventData, resultado);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> resultado, CancellationToken ct = default)
    {
        Carimbar(eventData.Context);
        return base.SavingChangesAsync(eventData, resultado, ct);
    }

    private void Carimbar(DbContext? contexto)
    {
        if (contexto is null) return;

        var agora = relogio.GetUtcNow().UtcDateTime;

        // IEntidadeAuditada herda de IEntidadeCriada, entao este laco pega as duas.
        foreach (var entrada in contexto.ChangeTracker.Entries<IEntidadeCriada>())
        {
            var auditada = entrada.Entity as IEntidadeAuditada;

            switch (entrada.State)
            {
                // ===================== A DATA JÁ VINDA NÃO É SOBRESCRITA (INT-XX) =====================
                // ⚠️ ERA INCONDICIONAL, e isso impedia importar histórico. O lead exportado do
                // Gerenciador de Leads da Meta traz `created_time` — o instante em que a pessoa
                // preencheu o formulário, dias atrás —, e carimbar `agora` por cima faria 600
                // leads antigos entrarem como "hoje": o dashboard diria que foi o melhor dia do
                // ano, e o follow-up trataria contato de três dias como recém-chegado.
                //
                // ⚠️ O `Modified` ABAIXO CONTINUA INTOCADO, e é onde mora a proteção de verdade —
                // o comentário dele explica: objeto desanexado com `CriadoEm` zerado sobrescreveria
                // a data original num UPDATE. Aqui é INSERT, e `default` distingue "ninguém
                // informou" de "informaram de propósito" sem ambiguidade: nenhuma data real é
                // `0001-01-01`.
                // ==============================================================================
                case EntityState.Added:
                    if (entrada.Entity.CriadoEm == default) entrada.Entity.CriadoEm = agora;
                    if (auditada is not null) auditada.AtualizadoEm = agora;
                    break;

                case EntityState.Modified:
                    if (auditada is not null) auditada.AtualizadoEm = agora;
                    // Sem isto, um objeto desanexado com CriadoEm zerado sobrescreveria a
                    // data original no UPDATE.
                    entrada.Property(x => x.CriadoEm).IsModified = false;
                    break;
            }
        }
    }
}
