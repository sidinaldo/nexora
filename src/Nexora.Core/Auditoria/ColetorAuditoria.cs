using Nexora.Core.Entidades;

namespace Nexora.Core.Auditoria;

/// <summary>Um valor antes e depois. `object?` porque o diff atravessa tipos — decimal, DateTime,
/// enum, string —, e o serializador de jsonb resolve na hora de gravar.</summary>
public readonly record struct AlteracaoValor(object? Antes, object? Depois);

/// <summary>O que um servico declarou. `Explicitas` cobre o caso em que o valor legivel NAO e o
/// da coluna: mover de etapa muda `etapa_id` de 4 para 3, e a tela precisa de "Negociacao" e
/// "Proposta". Quem sabe os nomes e o servico, que acabou de le-los.
///
/// `Ator` e NULO em quase toda declaracao, e nulo significa "decida como sempre decidiu": tem
/// usuario no contexto, e `Usuario`; nao tem, e `Sistema`. So quem sabe ser outra coisa informa —
/// hoje, so a area do operador. Ver <see cref="AtorAuditoria"/>.</summary>
public sealed record DeclaracaoAuditoria(
    EntidadeAuditada Entidade,
    long EntidadeId,
    AcaoAuditoria Acao,
    IReadOnlyDictionary<string, AlteracaoValor>? Explicitas = null,
    AtorAuditoria? Ator = null);

/// <summary>===================== OS SERVICOS DECLARAM, O INTERCEPTOR PREENCHE =====================
///
/// O interceptor sabe O QUE mudou (le o ChangeTracker) e nao sabe POR QUE. Ele ve `ganho_em` indo
/// de NULL para uma data; nao distingue "o vendedor fechou a venda" de "a migracao carimbou" nem
/// de "o suporte corrigiu um erro". Adivinhar produziria trilha que mente com confianca — pior
/// que trilha ausente, porque parece confiavel.
///
/// Entao o servico anuncia a acao antes do `SaveChanges`, e o interceptor monta o resto: o diff,
/// o autor, o instante, o tenant.
///
/// SEM DECLARACAO NAO HA LINHA. E deliberado: escrita que ninguem declarou (um contador
/// atualizado pelo webhook, `nao_lidas` subindo a cada mensagem) nao e evento de auditoria. A
/// alternativa — auditar tudo por padrao e ir excluindo — encheria a tabela de ruido e faria a
/// linha do tempo do contato ficar ilegivel no primeiro dia de uso.
///
/// ESCOPO POR REQUISICAO: uma instancia por request/escopo de DI. `Consumir` esvazia, para um
/// segundo `SaveChanges` no mesmo request nao reescrever os eventos do primeiro.
/// ==========================================================================================</summary>
public class ColetorAuditoria
{
    private readonly List<DeclaracaoAuditoria> _pendentes = [];

    public void Declarar(EntidadeAuditada entidade, long id, AcaoAuditoria acao) =>
        _pendentes.Add(new DeclaracaoAuditoria(entidade, id, acao));

    /// <summary>Com valores legiveis fornecidos pelo servico — ver `Explicitas`.</summary>
    public void Declarar(
        EntidadeAuditada entidade, long id, AcaoAuditoria acao,
        IReadOnlyDictionary<string, AlteracaoValor> explicitas) =>
        _pendentes.Add(new DeclaracaoAuditoria(entidade, id, acao, explicitas));

    /// <summary>Com o ATOR dito na mao (OPE-1). Existe porque o interceptor decide o ator por
    /// eliminacao — sem usuario no contexto, `Sistema` —, e a area do operador tambem roda sem
    /// usuario. Sem este caminho, toda mudanca de plano apareceria na trilha como se a rodada
    /// automatica a tivesse feito.</summary>
    public void Declarar(
        EntidadeAuditada entidade, long id, AcaoAuditoria acao, AtorAuditoria ator,
        IReadOnlyDictionary<string, AlteracaoValor>? explicitas = null) =>
        _pendentes.Add(new DeclaracaoAuditoria(entidade, id, acao, explicitas, ator));

    /// <summary>Devolve e ESVAZIA. Chamado uma vez por `SaveChanges`.</summary>
    public IReadOnlyList<DeclaracaoAuditoria> Consumir()
    {
        if (_pendentes.Count == 0) return [];
        var copia = _pendentes.ToArray();
        _pendentes.Clear();
        return copia;
    }
}
