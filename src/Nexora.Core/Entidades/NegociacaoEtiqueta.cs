namespace Nexora.Core.Entidades;

/// <summary>===================== A ETIQUETA DO NEGÓCIO, NÃO DA PESSOA =====================
///
/// Relatado assim: "incluí o contato Ysia em Vendas e Pós-venda e ela ficou com a mesma etiqueta
/// em pipeline diferente".
///
/// O card do funil mostrava as etiquetas do CONTATO, então os dois cards da mesma pessoa saíam
/// idênticos — e não havia como dizer "este negócio está urgente" sem dizer o mesmo do outro.
///
/// ⚠️ ISTO NÃO SUBSTITUI `ContatoEtiqueta`, e a diferença é o que cada uma responde:
///
///   `contatos_etiquetas`     quem a PESSOA é         "Revendedor", "VIP"     vale em tudo
///   `negociacoes_etiquetas`  como ESTE negócio está  "Urgente", "Aguardando" vale num card só
///
/// A pessoa na caixa de entrada não tem negócio nenhum (E6) e continua marcável — é por isso que
/// a tabela do contato não podia simplesmente virar esta.
///
/// ===================== UM VOCABULÁRIO SÓ =====================
/// As duas apontam para a MESMA `etiquetas`. "Urgente" e "VIP" saem da mesma lista, e o que
/// decide onde a marca gruda é ONDE foi aplicada — na caixa/contato gruda na pessoa, no card
/// gruda no negócio.
///
/// A alternativa — um `escopo` na etiqueta, dizendo se ela é de pessoa ou de negócio — foi
/// considerada e descartada por enquanto: obriga o dono a classificar cada etiqueta na criação,
/// antes de saber como vai usá-la, e o erro dessa classificação só aparece semanas depois, na
/// hora de marcar. Se a mistura incomodar na prática, o escopo entra depois sem migrar dado.
/// ==========================================================================</summary>
public class NegociacaoEtiqueta : IEntidadeCriada
{
    public long EmpresaId { get; set; }
    public long NegociacaoId { get; set; }
    public long EtiquetaId { get; set; }

    /// <summary>Quando a etiqueta foi colada. Mesma razão do par em `ContatoEtiqueta`: "desde
    /// quando este negócio está urgente?" é pergunta que aparece sozinha depois, e a linha não
    /// tem como responder retroativamente se ninguém anotou na hora.</summary>
    public DateTime CriadoEm { get; set; }

    /// <summary>Quem colou. NULO quando não havia sessão — semente, migração, script.</summary>
    public long? CriadoPor { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Negociacao Negociacao { get; set; } = null!;
    public Etiqueta Etiqueta { get; set; } = null!;
}
