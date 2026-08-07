using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;

namespace Nexora.Infra.Persistencia;

/// <summary>===================== A TRILHA, MONTADA NO MESMO SaveChanges (AUD-1) =====================
///
/// Roda DEPOIS do `InterceptorAuditoria` (que carimba `criado_em`/`atualizado_em`) e antes de o
/// comando ir ao banco. Le as declaracoes do `ColetorAuditoria`, casa cada uma com a entrada
/// correspondente no ChangeTracker, monta o diff e ACRESCENTA as linhas de `auditoria` ao proprio
/// contexto.
///
/// Acrescentar no ChangeTracker durante o `SavingChanges` faz as linhas entrarem no MESMO comando
/// e na MESMA transacao: ou o fato e a trilha dele existem, ou nenhum dos dois. Gravar depois, num
/// segundo `SaveChanges`, abriria a janela em que a venda foi cancelada e ninguem registrou quem.
///
/// ⚠️ SQL CRU NAO PASSA POR AQUI. `DadosMensagem` e `DadosFollowUp` escrevem direto — o que e
/// correto, porque o que elas alteram (`ack`, `enviada_em`, `nao_lidas`) nao e evento de
/// auditoria. Se um dia alguma delas mexer em entidade auditavel, tera que declarar na mao.
/// ===============================================================================================</summary>
public class InterceptorTrilha(
    ColetorAuditoria coletor, IContextoEmpresa contexto, TimeProvider relogio) : SaveChangesInterceptor
{
    /// <summary>Colunas que NAO entram no diff.
    ///
    /// `atualizado_em` muda em toda escrita e nao informa nada que a propria linha da trilha ja
    /// nao diga. `criado_em` e `xmin` sao mecanica. `ordem_kanban` e posicao dentro da coluna:
    /// arrastar um card dois lugares acima nao e fato de negocio, e registrar isso encheria a
    /// linha do tempo de ruido — o que importa e a MUDANCA DE ETAPA, que vem declarada.</summary>
    private static readonly HashSet<string> Ignoradas =
        new(StringComparer.Ordinal) { "CriadoEm", "AtualizadoEm", "Versao", "OrdemKanban" };

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> resultado)
    {
        Registrar(eventData.Context);
        return base.SavingChanges(eventData, resultado);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> resultado, CancellationToken ct = default)
    {
        Registrar(eventData.Context);
        return base.SavingChangesAsync(eventData, resultado, ct);
    }

    private void Registrar(DbContext? contexto_)
    {
        if (contexto_ is not NexoraDbContext db) return;

        var declaracoes = coletor.Consumir();
        if (declaracoes.Count == 0) return;

        var agora = relogio.GetUtcNow().UtcDateTime;
        var usuarioId = contexto.UsuarioId;

        foreach (var d in declaracoes)
        {
            var alteracoes = Diferenca(db, d);

            db.Set<Auditoria>().Add(new Auditoria
            {
                // O `empresa_id` vem do CONTEXTO, nao da entidade: a trilha e do tenant que agiu.
                EmpresaId = contexto.EmpresaId,
                Entidade = d.Entidade,
                EntidadeId = d.EntidadeId,
                Acao = d.Acao,
                Alteracoes = alteracoes,
                UsuarioId = usuarioId == 0 ? null : usuarioId,
                // Sem usuario no contexto = job. `Sistema` em vez de um id inventado.
                Ator = usuarioId == 0 ? AtorAuditoria.Sistema : AtorAuditoria.Usuario,
                Quando = agora
            });
        }
    }

    /// <summary>O `jsonb` do evento. Junta o que o servico informou explicitamente com o que o
    /// ChangeTracker mostra — o explicito GANHA, porque ele carrega o valor legivel (nome da
    /// etapa) no lugar do id que a coluna guarda.</summary>
    private static string Diferenca(NexoraDbContext db, DeclaracaoAuditoria d)
    {
        var mapa = new Dictionary<string, object?>(StringComparer.Ordinal);

        var entrada = Entrada(db, d);
        if (entrada is not null)
        {
            foreach (var p in entrada.Properties)
            {
                if (p.Metadata.IsPrimaryKey() || Ignoradas.Contains(p.Metadata.Name)) continue;

                // Em INSERT tudo "mudou"; o interessante e o estado inicial, nao um diff de nulos.
                if (entrada.State == EntityState.Added)
                {
                    if (p.CurrentValue is not null)
                        mapa[Chave(p.Metadata.Name)] = new { antes = (object?)null, depois = Valor(p.CurrentValue) };
                    continue;
                }

                if (!p.IsModified) continue;
                if (Equals(p.OriginalValue, p.CurrentValue)) continue;

                mapa[Chave(p.Metadata.Name)] =
                    new { antes = Valor(p.OriginalValue), depois = Valor(p.CurrentValue) };
            }
        }

        if (d.Explicitas is not null)
            foreach (var (campo, v) in d.Explicitas)
                mapa[campo] = new { antes = Valor(v.Antes), depois = Valor(v.Depois) };

        return JsonSerializer.Serialize(mapa);
    }

    /// <summary>A entrada rastreada da entidade declarada. `null` quando a acao nao tem diff — o
    /// cancelamento de venda declara sobre a venda, mas quem esta no tracker pode ser so o
    /// contato. O evento vale mesmo sem diff.</summary>
    private static EntityEntry? Entrada(NexoraDbContext db, DeclaracaoAuditoria d)
    {
        var tipo = TipoDe(d.Entidade);
        if (tipo is null) return null;

        foreach (var e in db.ChangeTracker.Entries())
        {
            if (e.Entity.GetType() != tipo) continue;
            if (e.State is not (EntityState.Added or EntityState.Modified)) continue;

            // Em INSERT o id ainda e 0 — a declaracao veio antes de o banco gerar a chave. Com
            // uma entrada so daquele tipo, e ela.
            var id = e.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey())?.CurrentValue;
            if (id is long l && (l == d.EntidadeId || l == 0)) return e;
        }
        return null;
    }

    /// <summary>`GanhoEm` -> `ganhoEm`. A tela nunca mostra nome de coluna — ela traduz a partir
    /// desta chave —, mas a chave precisa ser estavel e reconhecivel para quem consultar o jsonb
    /// direto numa investigacao.</summary>
    private static string Chave(string propriedade) =>
        char.ToLowerInvariant(propriedade[0]) + propriedade[1..];

    /// <summary>Enum vira texto e `DateTime` vira ISO; o resto vai como esta. Sem isto o jsonb
    /// guardaria o inteiro do enum, e um relatorio de seis meses atras seria indecifravel depois
    /// de alguem reordenar os valores.</summary>
    private static object? Valor(object? v) => v switch
    {
        null => null,
        Enum e => e.ToString(),
        DateTime dt => dt.ToString("O"),
        DateOnly d => d.ToString("O"),
        _ => v
    };

    private static Type? TipoDe(EntidadeAuditada e) => e switch
    {
        EntidadeAuditada.Contato => typeof(Contato),
        EntidadeAuditada.Venda => typeof(Venda),
        EntidadeAuditada.Lembrete => typeof(Lembrete),
        EntidadeAuditada.Usuario => typeof(Usuario),
        EntidadeAuditada.Empresa => typeof(Empresa),
        EntidadeAuditada.EtapaFunil => typeof(EtapaFunil),
        EntidadeAuditada.Conexao => typeof(Conexao),
        EntidadeAuditada.Conversa => typeof(Conversa),
        _ => null
    };
}
